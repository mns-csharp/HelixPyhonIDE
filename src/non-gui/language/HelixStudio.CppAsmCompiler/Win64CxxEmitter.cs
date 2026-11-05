using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;

namespace HelixPythonNamespace.CppAsmCompiler
{
    /// <summary>
    /// Emits NASM (win64) for one C++ function of the supported subset.
    ///
    /// The evaluation strategy is the one <c>Win64Emitter</c> uses for Python, and for the same
    /// reason: expressions accumulate in EAX (ints) or XMM0 (doubles), but the "stack" of pending
    /// operands is a block of numbered spill slots inside the frame rather than push/pop. RSP
    /// therefore never moves between prologue and epilogue, so the 16-byte alignment the ABI
    /// demands at every <c>call</c> is a property of the frame size alone and cannot be broken by
    /// however deeply an expression happens to nest.
    ///
    /// Frame layout, high address to low:
    /// <code>
    ///   [rbp+8*(i+2)]  incoming argument i, for i >= 4 (the caller put it there)
    ///   [rbp+8]        return address
    ///   [rbp+0]        saved RBP
    ///   [rbp-8 ...]    named locals, parameters, and expression spill slots
    ///   [rsp+0 ...]    outgoing argument area: shadow space plus any stack arguments
    /// </code>
    ///
    /// Where this deliberately differs from the Python emitter is C++ SEMANTICS, and those
    /// differences are exactly why the two cannot share an emitter:
    ///
    /// * <c>int</c> is 32 bits and wraps. All integer work is done in 32-bit registers so that
    ///   overflow produces the same value a real compiler produces, not a 64-bit result that only
    ///   agrees until it doesn't.
    /// * <c>/</c> and <c>%</c> truncate toward zero (C++), where Python's <c>//</c> and <c>%</c>
    ///   floor. <c>idiv</c> gives the C++ answer directly; the Python emitter has to correct it.
    /// * comparisons involving NaN are all false except <c>!=</c>, which needs the parity flag —
    ///   <c>comisd</c> reports unordered in PF, and ignoring it makes <c>x == x</c> true for NaN.
    /// </summary>
    internal sealed class Win64CxxEmitter
    {
        private readonly StringBuilder _out = new StringBuilder();
        private readonly Dictionary<string, int> _slots = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _slotKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _breakLabels = new List<string>();
        private readonly List<string> _continueLabels = new List<string>();
        private readonly IReadOnlyDictionary<string, CxxFunctionType> _signatures;
        private CxxTypeTable _types;
        private CxxFunctionType _self;
        private string _epilogue;
        private int _labelSeq;
        private int _spillDepth;
        private int _maxCallArity = -1;
        private string _refusal;
        private int _refusalLine;

        internal string Refusal { get { return _refusal; } }
        internal int RefusalLine { get { return _refusalLine; } }

        // Win64 passes the first four arguments in these, BY POSITION: a double in argument slot 1
        // goes in XMM1, not XMM0, and the integer register of that index is then unused.
        private static readonly string[] ArgRegisters64 = { "rcx", "rdx", "r8", "r9" };
        private static readonly string[] ArgRegisters32 = { "ecx", "edx", "r8d", "r9d" };
        private static readonly string[] FloatArgRegisters = { "xmm0", "xmm1", "xmm2", "xmm3" };

        public Win64CxxEmitter(IReadOnlyDictionary<string, CxxFunctionType> signatures)
        {
            _signatures = signatures ?? new Dictionary<string, CxxFunctionType>(StringComparer.Ordinal);
        }

        private void Refuse(string reason, int line)
        {
            if (_refusal == null) { _refusal = reason; _refusalLine = line; }
        }

        private string NewLabel(string kind)
        {
            _labelSeq++;
            return ".L" + kind + _labelSeq;
        }

        /// <summary>
        /// One number shared by every label of a single construct, so `.Lwhile7` is closed by
        /// `.Lendwhile7`. The reader pairs a block's labels by that number; numbering them
        /// independently would leave it guessing which end belongs to which beginning.
        /// </summary>
        private int NewLabelGroup() { return ++_labelSeq; }

        private void Emit(string instruction) { _out.Append("    ").AppendLine(instruction); }
        private void Label(string label) { _out.Append(label).AppendLine(":"); }

        // ---- frame slots -------------------------------------------------------------------

        private int SlotOf(string name)
        {
            int slot;
            if (_slots.TryGetValue(name, out slot)) return slot;
            slot = (_slots.Count + 1) * 8;
            _slots[name] = slot;
            return slot;
        }

        private string IntMem(string name) { return "dword [rbp-" + SlotOf(name) + "]"; }
        private string DblMem(string name) { return "qword [rbp-" + SlotOf(name) + "]"; }
        private string Mem(string name, VCxx t) { return t == VCxx.Double ? DblMem(name) : IntMem(name); }

        /// <summary>
        /// The spill slot for the current expression depth. Depth-indexed rather than pooled so a
        /// nested sub-expression can never be handed a slot its parent is still holding a value in.
        /// </summary>
        private string SpillName(int depth)
        {
            string name = "$t" + depth;
            if (!_slotKinds.ContainsKey(name)) { SlotOf(name); _slotKinds[name] = "temp"; }
            return name;
        }

        // ---- accumulator moves -------------------------------------------------------------

        private void StoreAcc(string name, VCxx t)
        {
            Emit(t == VCxx.Double ? "movsd   " + DblMem(name) + ", xmm0"
                                  : "mov     " + IntMem(name) + ", eax");
        }

        private void LoadAcc(string name, VCxx t)
        {
            Emit(t == VCxx.Double ? "movsd   xmm0, " + DblMem(name)
                                  : "mov     eax, " + IntMem(name));
        }

        private void AccToSecond(VCxx t)
        {
            Emit(t == VCxx.Double ? "movapd  xmm1, xmm0" : "mov     ecx, eax");
        }

        /// <summary>Converts the accumulator between the two machine types, C++ rules.</summary>
        private void Coerce(VCxx from, VCxx to)
        {
            if (from == to) return;
            // int -> double is exact for every 32-bit int. double -> int truncates toward zero,
            // which is what a C++ cast does (and what `cvttsd2si` does — note the second t).
            Emit(to == VCxx.Double ? "cvtsi2sd xmm0, eax" : "cvttsd2si eax, xmm0");
        }

        private static VCxx Promote(VCxx a, VCxx b)
        {
            return (a == VCxx.Double || b == VCxx.Double) ? VCxx.Double : VCxx.Int;
        }

        // ---- function ----------------------------------------------------------------------

        /// <summary>Emit a whole function. Returns null (and sets <see cref="Refusal"/>) on refusal.</summary>
        public string EmitFunction(FunctionDecl fn, CxxTypeTable types)
        {
            _types = types;
            _self = _signatures[fn.Name];
            _epilogue = ".Lepi_" + fn.Name;

            var parameters = new List<string>();
            foreach (Parameter p in fn.Params) parameters.Add(p.Name);
            foreach (string p in parameters) { SlotOf(p); _slotKinds[p] = "param"; }
            foreach (KeyValuePair<string, VCxx> local in types.Locals)
                if (!_slotKinds.ContainsKey(local.Key)) { SlotOf(local.Key); _slotKinds[local.Key] = "local"; }

            // The body is emitted before the prologue because emitting it is what discovers the
            // spill depth and the widest call — neither of which the prologue can size the frame
            // without.
            EmitStatement(fn.Body);
            if (_refusal != null) return null;
            string body = _out.ToString();

            int localBytes = _slots.Count * 8;
            // A caller must always leave 32 bytes of shadow space for the callee to home its
            // register arguments in, even when it passes fewer than four.
            int outgoingBytes = _maxCallArity < 0 ? 0 : 8 * Math.Max(4, _maxCallArity);
            int frame = localBytes + outgoingBytes;
            if ((frame % 16) != 0) frame += 16 - (frame % 16);

            var text = new StringBuilder();
            text.Append(Metadata(fn.Name, parameters));
            text.AppendLine("global " + fn.Name);
            text.AppendLine(fn.Name + ":");
            text.AppendLine("    push    rbp");
            text.AppendLine("    mov     rbp, rsp");
            if (frame > 0) text.AppendLine("    sub     rsp, " + frame);
            for (int i = 0; i < parameters.Count; i++)
            {
                VCxx t = _self.Parameters[i];
                if (i < ArgRegisters64.Length)
                {
                    text.AppendLine(t == VCxx.Double
                        ? "    movsd   " + DblMem(parameters[i]) + ", " + FloatArgRegisters[i]
                        : "    mov     " + IntMem(parameters[i]) + ", " + ArgRegisters32[i]);
                }
                else
                {
                    // Arguments past the fourth were placed by the caller above its shadow space,
                    // which puts argument i at [rbp + 8*(i+2)] once the return address and the
                    // saved RBP are accounted for.
                    text.AppendLine(t == VCxx.Double
                        ? "    movsd   xmm0, qword [rbp+" + (8 * (i + 2)) + "]"
                        : "    mov     eax, dword [rbp+" + (8 * (i + 2)) + "]");
                    text.AppendLine(t == VCxx.Double
                        ? "    movsd   " + DblMem(parameters[i]) + ", xmm0"
                        : "    mov     " + IntMem(parameters[i]) + ", eax");
                }
            }
            text.Append(body);
            // Falling off the end of a value-returning C++ function is undefined behaviour. Real
            // compilers emit whatever is in the register; returning a deterministic zero is the
            // one choice that cannot vary between runs, and CppToNasm warns about it separately.
            text.AppendLine(_self.Returns == VCxx.Double ? "    xorpd   xmm0, xmm0" : "    xor     eax, eax");
            text.AppendLine(_epilogue + ":");
            text.AppendLine("    mov     rsp, rbp");
            text.AppendLine("    pop     rbp");
            text.AppendLine("    ret");
            return text.ToString();
        }

        /// <summary>
        /// The <c>; helix.</c> block: which frame offset holds which name, and of what type. It is
        /// what makes the assembly readable back; without it a slot is just a number and a
        /// decompiler cannot tell a user's variable from a compiler temporary. Same format the
        /// Python translator writes, so one reader understands both.
        /// </summary>
        private string Metadata(string name, List<string> parameters)
        {
            var meta = new StringBuilder();
            meta.AppendLine("; helix.function " + name + " -> " + CxxTypeTable.Describe(_self.Returns));
            meta.AppendLine("; helix.lang cpp");
            for (int i = 0; i < parameters.Count; i++)
                meta.AppendLine("; helix.slot " + SlotOf(parameters[i]) + " param " + parameters[i]
                                + " " + CxxTypeTable.Describe(_self.Parameters[i]));
            var ordered = new List<KeyValuePair<string, int>>(_slots);
            ordered.Sort((x, y) => x.Value.CompareTo(y.Value));
            foreach (KeyValuePair<string, int> entry in ordered)
            {
                string kind;
                if (!_slotKinds.TryGetValue(entry.Key, out kind)) kind = "temp";
                if (kind == "param") continue;
                VCxx t;
                string type = _types != null && _types.TryGetLocal(entry.Key, out t)
                    ? CxxTypeTable.Describe(t) : "any";
                meta.AppendLine("; helix.slot " + entry.Value + " " + kind + " " + entry.Key + " " + type);
            }
            return meta.ToString();
        }

        // ---- statements ---------------------------------------------------------------------

        private void EmitStatement(Stmt s)
        {
            if (s == null || _refusal != null) return;
            int line = s.Span.Start.Line;

            if (s is CompoundStmt block)
            {
                foreach (Stmt inner in block.Body) EmitStatement(inner);
                return;
            }
            if (s is EmptyStmt) return;

            if (s is DeclStmt decl)
            {
                var v = (VarDecl)decl.Decl;                 // CxxTypeTable already vetted this
                VCxx declared;
                _types.TryGetLocal(v.Name, out declared);
                VCxx got = EmitExpr(v.Initializer);
                if (_refusal != null) return;
                Coerce(got, declared);
                StoreAcc(v.Name, declared);
                return;
            }
            if (s is ExprStmt es)
            {
                EmitExpr(es.Expr);                          // value discarded
                return;
            }
            if (s is ReturnStmt ret)
            {
                if (ret.Value == null)
                {
                    Refuse("'return;' with no value in a function declared to return "
                           + CxxTypeTable.Describe(_self.Returns), line);
                    return;
                }
                VCxx got = EmitExpr(ret.Value);
                if (_refusal != null) return;
                Coerce(got, _self.Returns);
                Emit("jmp     " + _epilogue);
                return;
            }
            if (s is IfStmt ifs)
            {
                int g = NewLabelGroup();
                string elseLabel = ".Lelse" + g;
                string endLabel = ".Lendif" + g;
                EmitJumpIfFalse(ifs.Cond, elseLabel);
                if (_refusal != null) return;
                EmitStatement(ifs.Then);
                if (ifs.Else != null) Emit("jmp     " + endLabel);
                Label(elseLabel);
                if (ifs.Else != null)
                {
                    EmitStatement(ifs.Else);
                    Label(endLabel);
                }
                return;
            }
            if (s is WhileStmt w)
            {
                int g = NewLabelGroup();
                string top = ".Lwhile" + g;
                string done = ".Lendwhile" + g;
                Label(top);
                EmitJumpIfFalse(w.Cond, done);
                if (_refusal != null) return;
                _breakLabels.Add(done);
                _continueLabels.Add(top);
                EmitStatement(w.Body);
                _breakLabels.RemoveAt(_breakLabels.Count - 1);
                _continueLabels.RemoveAt(_continueLabels.Count - 1);
                Emit("jmp     " + top);
                Label(done);
                return;
            }
            if (s is DoStmt d)
            {
                int g = NewLabelGroup();
                string top = ".Ldo" + g;
                string test = ".Ldotest" + g;
                string done = ".Lenddo" + g;
                Label(top);
                _breakLabels.Add(done);
                // `continue` in a do-while jumps to the CONDITION, not the top: the body must not
                // re-run without the test.
                _continueLabels.Add(test);
                EmitStatement(d.Body);
                _breakLabels.RemoveAt(_breakLabels.Count - 1);
                _continueLabels.RemoveAt(_continueLabels.Count - 1);
                Label(test);
                EmitJumpIfFalse(d.Cond, done);
                if (_refusal != null) return;
                Emit("jmp     " + top);
                Label(done);
                return;
            }
            if (s is ForStmt f)
            {
                int g = NewLabelGroup();
                string top = ".Lfor" + g;
                string cont = ".Lforcont" + g;
                string done = ".Lendfor" + g;
                EmitStatement(f.Init);
                Label(top);
                // A `for` with no condition loops forever, which is legal C++ (`for(;;)`).
                if (f.Cond != null)
                {
                    EmitJumpIfFalse(f.Cond, done);
                    if (_refusal != null) return;
                }
                _breakLabels.Add(done);
                // `continue` must run the increment, so it targets the increment, not the top.
                _continueLabels.Add(cont);
                EmitStatement(f.Body);
                _breakLabels.RemoveAt(_breakLabels.Count - 1);
                _continueLabels.RemoveAt(_continueLabels.Count - 1);
                Label(cont);
                if (f.Inc != null) EmitExpr(f.Inc);
                Emit("jmp     " + top);
                Label(done);
                return;
            }
            if (s is BreakStmt)
            {
                if (_breakLabels.Count == 0) { Refuse("'break' outside a loop", line); return; }
                Emit("jmp     " + _breakLabels[_breakLabels.Count - 1]);
                return;
            }
            if (s is ContinueStmt)
            {
                if (_continueLabels.Count == 0) { Refuse("'continue' outside a loop", line); return; }
                Emit("jmp     " + _continueLabels[_continueLabels.Count - 1]);
                return;
            }
            if (s is SwitchStmt)
            {
                Refuse("'switch' is not supported; use if/else", line);
                return;
            }
            Refuse("statement '" + s.GetType().Name + "' is not supported", line);
        }

        // ---- conditions ---------------------------------------------------------------------

        /// <summary>
        /// Evaluates a condition and jumps to <paramref name="target"/> when it is FALSE.
        /// Short-circuit operators are handled here rather than as values, so `a && b` never
        /// evaluates `b` when `a` is false — the observable difference being any call in `b`.
        /// </summary>
        private void EmitJumpIfFalse(Expr cond, string target)
        {
            if (cond == null || _refusal != null) return;
            if (cond is ParenExpr paren) { EmitJumpIfFalse(paren.Inner, target); return; }

            if (cond is BinaryExpr b && b.Op == BinOpKind.LogAnd)
            {
                EmitJumpIfFalse(b.Lhs, target);
                EmitJumpIfFalse(b.Rhs, target);
                return;
            }
            if (cond is BinaryExpr bo && bo.Op == BinOpKind.LogOr)
            {
                string keep = NewLabel("or");
                string fail = NewLabel("orfail");
                EmitJumpIfFalse(bo.Lhs, fail);
                Emit("jmp     " + keep);
                Label(fail);
                EmitJumpIfFalse(bo.Rhs, target);
                Label(keep);
                return;
            }
            if (cond is UnaryExpr u && u.Op == UnaryOpKind.Not)
            {
                string skip = NewLabel("not");
                EmitJumpIfFalse(u.Operand, skip);   // operand true  -> !operand false -> jump
                Emit("jmp     " + target);
                Label(skip);
                return;
            }

            VCxx t = EmitExpr(cond);
            if (_refusal != null) return;
            if (t == VCxx.Double)
            {
                // NaN is not equal to zero, so a NaN condition is TRUE in C++. comisd reports
                // unordered in PF, and PF must be tested BEFORE ZF or NaN would read as zero.
                string live = NewLabel("nan");
                Emit("xorpd   xmm1, xmm1");
                Emit("comisd  xmm0, xmm1");
                Emit("jp      " + live);
                Emit("je      " + target);
                Label(live);
            }
            else
            {
                Emit("test    eax, eax");
                Emit("jz      " + target);
            }
        }

        // ---- expressions ---------------------------------------------------------------------

        /// <summary>Emits an expression into the accumulator and reports the type it left there.</summary>
        private VCxx EmitExpr(Expr e)
        {
            if (e == null || _refusal != null) return VCxx.Int;
            int line = e.Span.Start.Line;

            if (e is ParenExpr paren) return EmitExpr(paren.Inner);
            if (e is LiteralExpr lit) return EmitLiteral(lit);
            if (e is NameExpr name) return EmitName(name);
            if (e is CastExpr cast) return EmitCast(cast);
            if (e is UnaryExpr unary) return EmitUnary(unary);
            if (e is ConditionalExpr cond) return EmitConditional(cond);
            if (e is CallExpr call) return EmitCall(call);
            if (e is BinaryExpr bin)
            {
                if (bin.IsAssignment()) return EmitAssign(bin);
                if (bin.Op == BinOpKind.LogAnd || bin.Op == BinOpKind.LogOr) return EmitShortCircuitValue(bin);
                if (bin.Op == BinOpKind.Comma)
                {
                    EmitExpr(bin.Lhs);
                    return EmitExpr(bin.Rhs);
                }
                return EmitBinary(bin);
            }

            if (e is MemberExpr) { Refuse("member access is not supported (no classes or structs in this subset)", line); return VCxx.Int; }
            if (e is IndexExpr) { Refuse("indexing is not supported (no arrays or pointers in this subset)", line); return VCxx.Int; }
            if (e is NewExpr || e is DeleteExpr) { Refuse("'new'/'delete' are not supported; this subset has no heap", line); return VCxx.Int; }
            if (e is LambdaExpr) { Refuse("lambdas are not supported (a closure needs a capture object)", line); return VCxx.Int; }
            if (e is KernelLaunchExpr) { Refuse("CUDA kernel launches are not supported by this translator; it targets the host CPU", line); return VCxx.Int; }
            if (e is InitListExpr) { Refuse("braced initializer lists are not supported", line); return VCxx.Int; }

            Refuse("expression '" + e.GetType().Name + "' is not supported", line);
            return VCxx.Int;
        }

        private VCxx EmitLiteral(LiteralExpr lit)
        {
            int line = lit.Span.Start.Line;
            switch (lit.Kind)
            {
                case LiteralKind.Int:
                {
                    long value;
                    if (!TryParseIntLiteral(lit.Text, out value))
                    {
                        Refuse("integer literal '" + lit.Text + "' is not representable as a 32-bit int", line);
                        return VCxx.Int;
                    }
                    Emit("mov     eax, " + value.ToString(CultureInfo.InvariantCulture));
                    return VCxx.Int;
                }
                case LiteralKind.Float:
                {
                    double d;
                    string text = lit.Text.TrimEnd('f', 'F', 'l', 'L');
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    {
                        Refuse("floating literal '" + lit.Text + "' could not be read", line);
                        return VCxx.Int;
                    }
                    EmitDoubleLiteral(d);
                    return VCxx.Double;
                }
                case LiteralKind.Bool:
                    Emit("mov     eax, " + (lit.Text == "true" ? "1" : "0"));
                    return VCxx.Int;
                default:
                    Refuse(lit.Kind.ToString().ToLowerInvariant() + " literals are not supported "
                           + "(there is no data section or runtime to hold one)", line);
                    return VCxx.Int;
            }
        }

        /// <summary>
        /// C++ integer literals may be decimal, hex, octal or binary, with digit separators and a
        /// suffix. Refuses anything outside 32-bit int rather than silently truncating it.
        /// </summary>
        private static bool TryParseIntLiteral(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Replace("'", "").TrimEnd('u', 'U', 'l', 'L');
            if (t.Length == 0) return false;
            try
            {
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    value = Convert.ToInt64(t.Substring(2), 16);
                else if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                    value = Convert.ToInt64(t.Substring(2), 2);
                else if (t.Length > 1 && t[0] == '0')
                    value = Convert.ToInt64(t.Substring(1), 8);
                else if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return false;
            }
            catch (Exception) { return false; }
            return value >= int.MinValue && value <= int.MaxValue;
        }

        /// <summary>
        /// Materialises a double by its exact bit pattern. Writing the decimal text into the
        /// assembly and letting NASM re-parse it would round twice; the bits are what the C++
        /// compiler computed, so the bits are what get emitted.
        /// </summary>
        private void EmitDoubleLiteral(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            if (bits == 0) { Emit("xorpd   xmm0, xmm0"); return; }
            Emit("mov     rax, 0x" + bits.ToString("x16", CultureInfo.InvariantCulture)
                 + "        ; " + value.ToString("R", CultureInfo.InvariantCulture));
            Emit("movq    xmm0, rax");
        }

        private VCxx EmitName(NameExpr name)
        {
            VCxx t;
            if (!_types.TryGetLocal(name.Id, out t))
            {
                Refuse("'" + name.Id + "' is not a parameter or local of this function; globals and "
                       + "external symbols are not supported", name.Span.Start.Line);
                return VCxx.Int;
            }
            LoadAcc(name.Id, t);
            return t;
        }

        private VCxx EmitCast(CastExpr cast)
        {
            VCxx target;
            string why;
            if (!CxxTypeTable.TryType(cast.Type, out target, out why))
            {
                Refuse("cast to '" + cast.Type + "': " + why, cast.Span.Start.Line);
                return VCxx.Int;
            }
            VCxx got = EmitExpr(cast.Operand);
            if (_refusal != null) return VCxx.Int;
            Coerce(got, target);
            return target;
        }

        private VCxx EmitUnary(UnaryExpr u)
        {
            int line = u.Span.Start.Line;
            switch (u.Op)
            {
                case UnaryOpKind.Plus:
                    return EmitExpr(u.Operand);

                case UnaryOpKind.Minus:
                {
                    VCxx t = EmitExpr(u.Operand);
                    if (_refusal != null) return t;
                    if (t == VCxx.Double)
                    {
                        // Flip the sign bit rather than subtracting from zero, so -0.0 and NaN
                        // negate the way IEEE-754 says they do.
                        Emit("mov     rax, 0x8000000000000000");
                        Emit("movq    xmm1, rax");
                        Emit("xorpd   xmm0, xmm1");
                    }
                    else Emit("neg     eax");
                    return t;
                }

                case UnaryOpKind.BitNot:
                {
                    VCxx t = EmitExpr(u.Operand);
                    if (_refusal != null) return t;
                    if (t == VCxx.Double) { Refuse("'~' requires an integer operand", line); return t; }
                    Emit("not     eax");
                    return VCxx.Int;
                }

                case UnaryOpKind.Not:
                {
                    VCxx t = EmitExpr(u.Operand);
                    if (_refusal != null) return t;
                    if (t == VCxx.Double)
                    {
                        Emit("xorpd   xmm1, xmm1");
                        Emit("comisd  xmm0, xmm1");
                        // !NaN is false: unordered must not read as equal-to-zero.
                        Emit("sete    al");
                        Emit("setnp   cl");
                        Emit("and     al, cl");
                    }
                    else
                    {
                        Emit("test    eax, eax");
                        Emit("sete    al");
                    }
                    Emit("movzx   eax, al");
                    return VCxx.Int;
                }

                case UnaryOpKind.PreInc:
                case UnaryOpKind.PreDec:
                case UnaryOpKind.PostInc:
                case UnaryOpKind.PostDec:
                    return EmitIncDec(u);

                default:
                    Refuse("unary '" + u.Op + "' is not supported", line);
                    return VCxx.Int;
            }
        }

        /// <summary>
        /// ++ and -- on a local. Pre- yields the NEW value, post- the OLD one; getting that
        /// backwards is invisible in `for (i = 0; i < n; i++)` and wrong everywhere else.
        /// </summary>
        private VCxx EmitIncDec(UnaryExpr u)
        {
            int line = u.Span.Start.Line;
            var target = Unparen(u.Operand) as NameExpr;
            if (target == null)
            {
                Refuse("'" + u.Op + "' needs a variable to modify", line);
                return VCxx.Int;
            }
            VCxx t;
            if (!_types.TryGetLocal(target.Id, out t))
            {
                Refuse("'" + target.Id + "' is not a parameter or local of this function", line);
                return VCxx.Int;
            }
            bool increment = u.Op == UnaryOpKind.PreInc || u.Op == UnaryOpKind.PostInc;
            bool post = u.Op == UnaryOpKind.PostInc || u.Op == UnaryOpKind.PostDec;

            LoadAcc(target.Id, t);
            if (post)
            {
                // Keep the old value where the result will be read from, step the variable, and
                // leave the old value in the accumulator.
                string keep = SpillName(++_spillDepth);
                StoreAcc(keep, t);
                StepAndStore(target.Id, t, increment);
                LoadAcc(keep, t);
                _spillDepth--;
            }
            else
            {
                StepAndStore(target.Id, t, increment);
            }
            return t;
        }

        private void StepAndStore(string name, VCxx t, bool increment)
        {
            if (t == VCxx.Double)
            {
                Emit("mov     rax, 0x3ff0000000000000        ; 1.0");
                Emit("movq    xmm1, rax");
                Emit(increment ? "addsd   xmm0, xmm1" : "subsd   xmm0, xmm1");
            }
            else Emit(increment ? "add     eax, 1" : "sub     eax, 1");
            StoreAcc(name, t);
        }

        private static Expr Unparen(Expr e)
        {
            while (e is ParenExpr p) e = p.Inner;
            return e;
        }

        /// <summary>Assignment, plain or compound, to a parameter or local.</summary>
        private VCxx EmitAssign(BinaryExpr a)
        {
            int line = a.Span.Start.Line;
            var target = Unparen(a.Lhs) as NameExpr;
            if (target == null)
            {
                Refuse("only a variable can be assigned to in this subset", line);
                return VCxx.Int;
            }
            VCxx declared;
            if (!_types.TryGetLocal(target.Id, out declared))
            {
                Refuse("'" + target.Id + "' is not a parameter or local of this function", line);
                return VCxx.Int;
            }

            if (a.Op == BinOpKind.Assign)
            {
                VCxx got = EmitExpr(a.Rhs);
                if (_refusal != null) return declared;
                Coerce(got, declared);
                StoreAcc(target.Id, declared);
                return declared;
            }

            // `x op= e` is `x = x op e` with the result converted back to x's type. The operands
            // are emitted through the same path as a plain binary op so the conversions and the
            // integer-only checks stay in one place.
            BinOpKind op = CompoundToBinary(a.Op);
            string spill = SpillName(++_spillDepth);
            LoadAcc(target.Id, declared);
            StoreAcc(spill, declared);
            VCxx rhs = EmitExpr(a.Rhs);
            _spillDepth--;
            if (_refusal != null) return declared;

            VCxx common = Promote(declared, rhs);
            Coerce(rhs, common);
            AccToSecond(common);
            LoadAcc(spill, declared);
            Coerce(declared, common);
            VCxx produced = EmitBinaryOp(op, common, line);
            if (_refusal != null) return declared;
            Coerce(produced, declared);
            StoreAcc(target.Id, declared);
            return declared;
        }

        private static BinOpKind CompoundToBinary(BinOpKind op)
        {
            switch (op)
            {
                case BinOpKind.AddAssign: return BinOpKind.Add;
                case BinOpKind.SubAssign: return BinOpKind.Sub;
                case BinOpKind.MulAssign: return BinOpKind.Mul;
                case BinOpKind.DivAssign: return BinOpKind.Div;
                case BinOpKind.ModAssign: return BinOpKind.Mod;
                case BinOpKind.AndAssign: return BinOpKind.BitAnd;
                case BinOpKind.OrAssign: return BinOpKind.BitOr;
                case BinOpKind.XorAssign: return BinOpKind.BitXor;
                case BinOpKind.ShlAssign: return BinOpKind.Shl;
                default: return BinOpKind.Shr;
            }
        }

        /// <summary>
        /// `a && b` / `a || b` used as a VALUE (not as a condition) still short-circuits, and
        /// yields int 0 or 1 — never the operand itself, which is what C++ specifies and what
        /// distinguishes it from Python's `and`/`or`.
        /// </summary>
        private VCxx EmitShortCircuitValue(BinaryExpr b)
        {
            string zero = NewLabel("sc0");
            string one = NewLabel("sc1");
            string done = NewLabel("scend");

            if (b.Op == BinOpKind.LogAnd)
            {
                EmitJumpIfFalse(b.Lhs, zero);
                EmitJumpIfFalse(b.Rhs, zero);
                Emit("jmp     " + one);
            }
            else
            {
                string testRhs = NewLabel("scr");
                EmitJumpIfFalse(b.Lhs, testRhs);
                Emit("jmp     " + one);
                Label(testRhs);
                EmitJumpIfFalse(b.Rhs, zero);
                Emit("jmp     " + one);
            }
            if (_refusal != null) return VCxx.Int;
            Label(zero);
            Emit("xor     eax, eax");
            Emit("jmp     " + done);
            Label(one);
            Emit("mov     eax, 1");
            Label(done);
            return VCxx.Int;
        }

        private VCxx EmitConditional(ConditionalExpr c)
        {
            // Both arms must leave the same machine type, so the type is settled by emitting into
            // separate label paths and converting each to the promoted type. The arms are emitted
            // twice-typed: first to learn their types is not possible without emitting, so the
            // promotion is decided from the DECLARED shape via a dry type walk.
            VCxx thenType, elseType;
            if (!TryStaticType(c.Then, out thenType) || !TryStaticType(c.Else, out elseType))
            {
                Refuse("the arms of '?:' must both be int or double expressions this translator "
                       + "can type without evaluating them", c.Span.Start.Line);
                return VCxx.Int;
            }
            VCxx common = Promote(thenType, elseType);

            string elseLabel = NewLabel("celse");
            string endLabel = NewLabel("cend");
            EmitJumpIfFalse(c.Cond, elseLabel);
            if (_refusal != null) return common;
            VCxx got = EmitExpr(c.Then);
            Coerce(got, common);
            Emit("jmp     " + endLabel);
            Label(elseLabel);
            got = EmitExpr(c.Else);
            Coerce(got, common);
            Label(endLabel);
            return common;
        }

        /// <summary>
        /// Types an expression WITHOUT emitting it. Only needed where control flow forces the
        /// result type to be known before either arm runs (`?:`); everywhere else the emitter
        /// reports the type it actually produced, which cannot drift from the code.
        /// </summary>
        private bool TryStaticType(Expr e, out VCxx type)
        {
            type = VCxx.Int;
            e = Unparen(e);
            if (e is LiteralExpr lit)
            {
                if (lit.Kind == LiteralKind.Float) { type = VCxx.Double; return true; }
                return lit.Kind == LiteralKind.Int || lit.Kind == LiteralKind.Bool;
            }
            if (e is NameExpr n) return _types.TryGetLocal(n.Id, out type);
            if (e is CastExpr c) { string why; return CxxTypeTable.TryType(c.Type, out type, out why); }
            if (e is CallExpr call)
            {
                var callee = Unparen(call.Callee) as NameExpr;
                CxxFunctionType sig;
                if (callee == null || !_signatures.TryGetValue(callee.Id, out sig)) return false;
                type = sig.Returns;
                return true;
            }
            if (e is UnaryExpr u)
            {
                if (u.Op == UnaryOpKind.Not) { type = VCxx.Int; return true; }
                return TryStaticType(u.Operand, out type);
            }
            if (e is ConditionalExpr cond)
            {
                VCxx a, b;
                if (!TryStaticType(cond.Then, out a) || !TryStaticType(cond.Else, out b)) return false;
                type = Promote(a, b);
                return true;
            }
            if (e is BinaryExpr bin)
            {
                if (IsComparison(bin.Op) || bin.Op == BinOpKind.LogAnd || bin.Op == BinOpKind.LogOr)
                { type = VCxx.Int; return true; }
                if (bin.IsAssignment())
                {
                    var target = Unparen(bin.Lhs) as NameExpr;
                    return target != null && _types.TryGetLocal(target.Id, out type);
                }
                VCxx l, r;
                if (!TryStaticType(bin.Lhs, out l) || !TryStaticType(bin.Rhs, out r)) return false;
                type = Promote(l, r);
                return true;
            }
            return false;
        }

        private static bool IsComparison(BinOpKind op)
        {
            return op == BinOpKind.Lt || op == BinOpKind.Gt || op == BinOpKind.Le
                || op == BinOpKind.Ge || op == BinOpKind.Eq || op == BinOpKind.Ne;
        }

        private VCxx EmitBinary(BinaryExpr b)
        {
            int line = b.Span.Start.Line;

            // Left, spilled; then right; then both into place. Left is evaluated first because
            // C++17 fixes the order of evaluation for these operators and a call in either side
            // can have effects.
            string spill = SpillName(++_spillDepth);
            VCxx lt = EmitExpr(b.Lhs);
            if (_refusal != null) { _spillDepth--; return lt; }
            StoreAcc(spill, lt);
            VCxx rt = EmitExpr(b.Rhs);
            _spillDepth--;
            if (_refusal != null) return rt;

            VCxx common = Promote(lt, rt);
            Coerce(rt, common);
            AccToSecond(common);
            LoadAcc(spill, lt);
            Coerce(lt, common);

            if (IsComparison(b.Op)) return EmitCompare(b.Op, common);
            return EmitBinaryOp(b.Op, common, line);
        }

        /// <summary>
        /// Arithmetic with the left operand in the accumulator and the right in the second
        /// register, both already of type <paramref name="t"/>.
        /// </summary>
        private VCxx EmitBinaryOp(BinOpKind op, VCxx t, int line)
        {
            if (t == VCxx.Double)
            {
                switch (op)
                {
                    case BinOpKind.Add: Emit("addsd   xmm0, xmm1"); return t;
                    case BinOpKind.Sub: Emit("subsd   xmm0, xmm1"); return t;
                    case BinOpKind.Mul: Emit("mulsd   xmm0, xmm1"); return t;
                    case BinOpKind.Div: Emit("divsd   xmm0, xmm1"); return t;
                    default:
                        Refuse("'" + Spell(op) + "' requires integer operands", line);
                        return t;
                }
            }

            switch (op)
            {
                case BinOpKind.Add: Emit("add     eax, ecx"); return VCxx.Int;
                case BinOpKind.Sub: Emit("sub     eax, ecx"); return VCxx.Int;
                case BinOpKind.Mul: Emit("imul    eax, ecx"); return VCxx.Int;
                case BinOpKind.Div:
                    // idiv truncates toward zero, which IS C++ division. (Python's // floors, and
                    // the Python emitter has to correct for it — the two are not interchangeable.)
                    Emit("cdq");
                    Emit("idiv    ecx");
                    return VCxx.Int;
                case BinOpKind.Mod:
                    Emit("cdq");
                    Emit("idiv    ecx");
                    Emit("mov     eax, edx");
                    return VCxx.Int;
                case BinOpKind.BitAnd: Emit("and     eax, ecx"); return VCxx.Int;
                case BinOpKind.BitOr: Emit("or      eax, ecx"); return VCxx.Int;
                case BinOpKind.BitXor: Emit("xor     eax, ecx"); return VCxx.Int;
                case BinOpKind.Shl: Emit("sal     eax, cl"); return VCxx.Int;
                case BinOpKind.Shr:
                    // Arithmetic shift: `int` is signed, and a logical shift would turn a negative
                    // value positive.
                    Emit("sar     eax, cl");
                    return VCxx.Int;
                default:
                    Refuse("operator '" + Spell(op) + "' is not supported", line);
                    return VCxx.Int;
            }
        }

        private VCxx EmitCompare(BinOpKind op, VCxx t)
        {
            if (t == VCxx.Double)
            {
                // Every comparison with a NaN operand is false except !=. comisd puts "unordered"
                // in PF, so PF has to be folded into the result rather than ignored.
                switch (op)
                {
                    case BinOpKind.Lt: Emit("comisd  xmm1, xmm0"); Emit("seta    al"); break;
                    case BinOpKind.Le: Emit("comisd  xmm1, xmm0"); Emit("setae   al"); break;
                    case BinOpKind.Gt: Emit("comisd  xmm0, xmm1"); Emit("seta    al"); break;
                    case BinOpKind.Ge: Emit("comisd  xmm0, xmm1"); Emit("setae   al"); break;
                    case BinOpKind.Eq:
                        Emit("comisd  xmm0, xmm1");
                        Emit("sete    al");
                        Emit("setnp   cl");
                        Emit("and     al, cl");
                        break;
                    default:
                        Emit("comisd  xmm0, xmm1");
                        Emit("setne   al");
                        Emit("setp    cl");
                        Emit("or      al, cl");
                        break;
                }
            }
            else
            {
                Emit("cmp     eax, ecx");
                switch (op)
                {
                    case BinOpKind.Lt: Emit("setl    al"); break;
                    case BinOpKind.Le: Emit("setle   al"); break;
                    case BinOpKind.Gt: Emit("setg    al"); break;
                    case BinOpKind.Ge: Emit("setge   al"); break;
                    case BinOpKind.Eq: Emit("sete    al"); break;
                    default: Emit("setne   al"); break;
                }
            }
            Emit("movzx   eax, al");
            return VCxx.Int;
        }

        private static string Spell(BinOpKind op)
        {
            switch (op)
            {
                case BinOpKind.Add: return "+";
                case BinOpKind.Sub: return "-";
                case BinOpKind.Mul: return "*";
                case BinOpKind.Div: return "/";
                case BinOpKind.Mod: return "%";
                case BinOpKind.Shl: return "<<";
                case BinOpKind.Shr: return ">>";
                case BinOpKind.BitAnd: return "&";
                case BinOpKind.BitOr: return "|";
                case BinOpKind.BitXor: return "^";
                default: return op.ToString();
            }
        }

        /// <summary>
        /// A call to another function of this translation unit, recursion included.
        ///
        /// Every argument is evaluated into its own spill slot BEFORE any register is loaded:
        /// evaluating argument 2 could itself call something, which would destroy an argument
        /// already staged in a volatile register.
        /// </summary>
        private VCxx EmitCall(CallExpr call)
        {
            int line = call.Span.Start.Line;
            var callee = Unparen(call.Callee) as NameExpr;
            if (callee == null)
            {
                Refuse("only direct calls to functions in this file are supported", line);
                return VCxx.Int;
            }
            CxxFunctionType sig;
            if (!_signatures.TryGetValue(callee.Id, out sig))
            {
                Refuse("'" + callee.Id + "' is not a function defined in this file; there is no "
                       + "linker or standard library in this subset", line);
                return VCxx.Int;
            }
            if (call.Args.Count != sig.Parameters.Count)
            {
                Refuse("'" + callee.Id + "' takes " + sig.Parameters.Count + " argument(s) but "
                       + call.Args.Count + " were passed", line);
                return VCxx.Int;
            }

            var staged = new List<string>();
            for (int i = 0; i < call.Args.Count; i++)
            {
                string slot = SpillName(++_spillDepth);
                staged.Add(slot);
                VCxx got = EmitExpr(call.Args[i]);
                if (_refusal != null) { _spillDepth -= (i + 1); return sig.Returns; }
                Coerce(got, sig.Parameters[i]);
                StoreAcc(slot, sig.Parameters[i]);
            }
            _spillDepth -= call.Args.Count;

            for (int i = 0; i < staged.Count; i++)
            {
                VCxx t = sig.Parameters[i];
                if (i < ArgRegisters64.Length)
                {
                    Emit(t == VCxx.Double
                        ? "movsd   " + FloatArgRegisters[i] + ", " + DblMem(staged[i])
                        : "mov     " + ArgRegisters32[i] + ", " + IntMem(staged[i]));
                }
                else if (t == VCxx.Double)
                {
                    Emit("movsd   xmm0, " + DblMem(staged[i]));
                    Emit("movsd   qword [rsp+" + (8 * i) + "], xmm0");
                }
                else
                {
                    // Sign-extend into the full 8-byte stack slot: the callee reads a dword, but
                    // leaving the upper half as whatever happened to be there makes the frame
                    // contents depend on history, which is murder to debug.
                    Emit("movsxd  rax, " + IntMem(staged[i]));
                    Emit("mov     qword [rsp+" + (8 * i) + "], rax");
                }
            }
            if (call.Args.Count > _maxCallArity) _maxCallArity = call.Args.Count;
            Emit("call    " + callee.Id);
            return sig.Returns;
        }
    }
}
