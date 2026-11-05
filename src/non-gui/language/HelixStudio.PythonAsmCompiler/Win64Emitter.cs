using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.PythonAsmCompiler
{
    /// <summary>
    /// Emits NASM (win64) for one Python function of the supported subset.
    ///
    /// Expressions are evaluated as a stack machine — integers accumulate in RAX, doubles in XMM0
    /// — but the "stack" is a block of numbered spill slots inside the frame rather than
    /// <c>push</c>/<c>pop</c>. That choice is what makes calls possible: RSP never moves between
    /// the prologue and the epilogue, so the 16-byte alignment the ABI demands at every
    /// <c>call</c> is a property of the frame size alone and cannot be broken by however deeply an
    /// expression happens to nest. With push/pop the alignment depended on the parity of the
    /// expression depth at the call site, which is exactly the sort of thing that works in testing
    /// and faults inside a callee that uses SSE.
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
    /// Each function is preceded by a <c>; helix.</c> metadata block naming its slots. That is
    /// debug information, the same thing a compiler writes into <c>.debug_info</c> under
    /// <c>-g</c>, and it is what lets <see cref="NasmToPython"/> read the assembly back.
    /// </summary>
    internal sealed class Win64Emitter
    {
        private readonly StringBuilder _out = new StringBuilder();
        private readonly Dictionary<string, int> _slots = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _slotKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _breakLabels = new List<string>();
        private readonly List<string> _continueLabels = new List<string>();
        private readonly IReadOnlyDictionary<string, FunctionType> _signatures;
        private TypeTable _types;
        private FunctionType _self;
        private string _epilogue;
        private int _labelSeq;
        private int _spillDepth;
        private int _maxCallArity = -1;
        private string _refusal;
        private int _refusalLine;

        internal string Refusal { get { return _refusal; } }
        internal int RefusalLine { get { return _refusalLine; } }

        private static readonly string[] ArgRegisters = { "rcx", "rdx", "r8", "r9" };
        private static readonly string[] FloatArgRegisters = { "xmm0", "xmm1", "xmm2", "xmm3" };

        /// <summary>
        /// Win64 makes RBX callee-saved. The emitter uses it as a scratch register during floor
        /// division, so the incoming value is parked in a frame slot and restored by the epilogue.
        /// A compiler that clobbers a non-volatile register corrupts its CALLER — the kind of bug
        /// that shows up only in whichever build happens to keep a live value there.
        /// </summary>
        private const string SavedRbx = "$rbx";

        public Win64Emitter(IReadOnlyDictionary<string, FunctionType> signatures)
        {
            _signatures = signatures ?? new Dictionary<string, FunctionType>(StringComparer.Ordinal);
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

        private void Emit(string instruction) { _out.Append("    ").AppendLine(instruction); }
        private void Label(string label) { _out.Append(label).AppendLine(":"); }

        /// <summary>Emit a whole function. Returns null (and sets <see cref="Refusal"/>) on refusal.</summary>
        public string EmitFunction(FunctionDef fn, TypeTable types)
        {
            _types = types;
            _self = _signatures[fn.Name];
            _epilogue = ".Lepi_" + fn.Name;

            var parameters = new List<string>();
            if (fn.Args != null && fn.Args.Args != null)
                foreach (Arg a in fn.Args.Args) parameters.Add(a.Name);
            foreach (string p in parameters) { SlotOf(p); _slotKinds[p] = "param"; }
            SlotOf(SavedRbx); _slotKinds[SavedRbx] = "saved";
            CollectAssignedNames(fn.Body);

            // The body is emitted before the prologue because emitting it is what discovers the
            // remaining locals, the spill depth, and the widest call — none of which the prologue
            // can size the frame without.
            EmitStatements(fn.Body);
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
            text.AppendLine("    mov     " + Slot(SavedRbx) + ", rbx");
            for (int i = 0; i < parameters.Count; i++)
            {
                VType t = _self.Parameters[i];
                if (i < ArgRegisters.Length)
                {
                    text.AppendLine(t == VType.Float
                        ? "    movsd   " + Mem(parameters[i]) + ", " + FloatArgRegisters[i]
                        : "    mov     " + Slot(parameters[i]) + ", " + ArgRegisters[i]);
                }
                else
                {
                    // Arguments past the fourth were pushed by the caller above its shadow space,
                    // which puts argument i at [rbp + 8*(i+2)] once the return address and the
                    // saved RBP are accounted for. The bits move through RAX whatever they mean.
                    text.AppendLine("    mov     rax, qword [rbp+" + (8 * (i + 2)) + "]");
                    text.AppendLine("    mov     " + Slot(parameters[i]) + ", rax");
                }
            }
            text.Append(body);
            // A Python function that falls off the end returns None; in this subset that is
            // represented as 0, which is also what a bare `return` yields.
            text.AppendLine(_self.Returns == VType.Float ? "    xorpd   xmm0, xmm0" : "    xor     rax, rax");
            text.AppendLine(_epilogue + ":");
            text.AppendLine("    mov     rbx, " + Slot(SavedRbx));
            text.AppendLine("    mov     rsp, rbp");
            text.AppendLine("    pop     rbp");
            text.AppendLine("    ret");
            return text.ToString();
        }

        /// <summary>
        /// The <c>; helix.</c> block: which frame offset holds which name, and of what type. It is
        /// what makes the assembly readable back; without it a slot is just a number and a
        /// decompiler cannot tell a user's variable from a compiler temporary.
        /// </summary>
        private string Metadata(string name, List<string> parameters)
        {
            var meta = new StringBuilder();
            meta.AppendLine("; helix.function " + name + " -> " + TypeTable.Describe(_self.Returns));
            for (int i = 0; i < parameters.Count; i++)
                meta.AppendLine("; helix.slot " + SlotOf(parameters[i]) + " param " + parameters[i]
                                + " " + TypeTable.Describe(_self.Parameters[i]));
            var ordered = new List<KeyValuePair<string, int>>(_slots);
            ordered.Sort((x, y) => x.Value.CompareTo(y.Value));
            foreach (KeyValuePair<string, int> entry in ordered)
            {
                string kind;
                if (!_slotKinds.TryGetValue(entry.Key, out kind)) kind = "temp";
                if (kind == "param") continue;
                VType t;
                string type = (kind == "stop" || kind == "step") ? "int"
                    : _types != null && _types.TryGetLocal(entry.Key, out t) ? TypeTable.Describe(t)
                    : "any";
                meta.AppendLine("; helix.slot " + entry.Value + " " + kind + " " + entry.Key + " " + type);
            }
            return meta.ToString();
        }

        private int SlotOf(string name)
        {
            int slot;
            if (_slots.TryGetValue(name, out slot)) return slot;
            slot = (_slots.Count + 1) * 8;
            _slots[name] = slot;
            return slot;
        }

        private string Slot(string name) { return "qword [rbp-" + SlotOf(name) + "]"; }
        private string Mem(string name) { return "[rbp-" + SlotOf(name) + "]"; }

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

        private void CollectAssignedNames(IReadOnlyList<Stmt> body)
        {
            if (body == null) return;
            foreach (Stmt s in body)
            {
                switch (s)
                {
                    case Assign a:
                        foreach (Expr t in a.Targets) CollectTarget(t);
                        break;
                    case AugAssign ag: CollectTarget(ag.Target); break;
                    case AnnAssign an: CollectTarget(an.Target); break;
                    case If iff: CollectAssignedNames(iff.Body); CollectAssignedNames(iff.OrElse); break;
                    case While w: CollectAssignedNames(w.Body); CollectAssignedNames(w.OrElse); break;
                    case For f:
                        CollectTarget(f.Target);
                        CollectAssignedNames(f.Body); CollectAssignedNames(f.OrElse);
                        break;
                }
            }
        }

        private void CollectTarget(Expr target)
        {
            var name = target as Name;
            if (name != null) { SlotOf(name.Id); _slotKinds[name.Id] = "local"; return; }
            var tuple = target as TupleExpr;
            if (tuple != null) { foreach (Expr e in tuple.Elts) CollectTarget(e); return; }
            var list = target as ListExpr;
            if (list != null) { foreach (Expr e in list.Elts) CollectTarget(e); }
        }

        // ---- value movement ----------------------------------------------

        private void StoreAccumulator(string name, VType t)
        {
            if (t == VType.Float) Emit("movsd   " + Mem(name) + ", xmm0");
            else Emit("mov     " + Slot(name) + ", rax");
        }

        private void LoadAccumulator(string name, VType t)
        {
            if (t == VType.Float) Emit("movsd   xmm0, " + Mem(name));
            else Emit("mov     rax, " + Slot(name));
        }

        private void LoadSecond(string name, VType t)
        {
            if (t == VType.Float) Emit("movsd   xmm1, " + Mem(name));
            else Emit("mov     rcx, " + Slot(name));
        }

        private void AccumulatorToSecond(VType t)
        {
            if (t == VType.Float) Emit("movapd  xmm1, xmm0");
            else Emit("mov     rcx, rax");
        }

        /// <summary>
        /// Widens the accumulator when an int flows into a float context. The reverse never
        /// happens implicitly: Python does not silently drop a fraction, and neither does this.
        /// </summary>
        private void Coerce(VType from, VType to, int line)
        {
            if (from == to) return;
            if (from == VType.Int && to == VType.Float) { Emit("cvtsi2sd xmm0, rax"); return; }
            Refuse("a float cannot be used where an int is required; use int(...) to truncate it "
                   + "explicitly, as Python requires", line);
        }

        // ---- statements --------------------------------------------------

        private void EmitStatements(IReadOnlyList<Stmt> body)
        {
            if (body == null) return;
            foreach (Stmt s in body)
            {
                if (_refusal != null) return;
                EmitStatement(s);
            }
        }

        private void EmitStatement(Stmt s)
        {
            switch (s)
            {
                case Return r:
                    if (r.Value == null)
                    {
                        Emit(_self.Returns == VType.Float ? "xorpd   xmm0, xmm0" : "xor     rax, rax");
                    }
                    else
                    {
                        VType t = EmitExpr(r.Value);
                        Coerce(t, _self.Returns, r.Span.Start.Line);
                    }
                    Emit("jmp     " + _epilogue);
                    return;

                case Assign a: EmitAssign(a); return;

                case AnnAssign an:
                    {
                        var target = an.Target as Name;
                        if (target == null) { Refuse("only assignment to a simple name is supported", an.Span.Start.Line); return; }
                        if (an.Value == null) { SlotOf(target.Id); return; }   // a bare annotation declares only storage
                        VType want = LocalType(target.Id);
                        VType got = EmitExpr(an.Value);
                        Coerce(got, want, an.Span.Start.Line);
                        StoreAccumulator(target.Id, want);
                    }
                    return;

                case AugAssign ag:
                    {
                        var target = ag.Target as Name;
                        if (target == null) { Refuse("only augmented assignment to a simple name is supported", ag.Span.Start.Line); return; }
                        VType want = LocalType(target.Id);
                        int depth = _spillDepth++;
                        VType got = EmitExpr(ag.Value);
                        _spillDepth--;
                        Coerce(got, want, ag.Span.Start.Line);
                        if (_refusal != null) return;
                        string spill = SpillName(depth);
                        StoreAccumulator(spill, want);
                        LoadAccumulator(target.Id, want);
                        LoadSecond(spill, want);
                        EmitBinaryOp(ag.Op, want, ag.Span.Start.Line);
                        StoreAccumulator(target.Id, want);
                    }
                    return;

                case If iff:
                    {
                        string elseLabel = NewLabel("else");
                        string endLabel = NewLabel("endif");
                        EmitTruthTest(iff.Test);
                        Emit("je      " + (iff.OrElse.Count > 0 ? elseLabel : endLabel));
                        EmitStatements(iff.Body);
                        if (iff.OrElse.Count > 0)
                        {
                            Emit("jmp     " + endLabel);
                            Label(elseLabel);
                            EmitStatements(iff.OrElse);
                        }
                        Label(endLabel);
                    }
                    return;

                case While w:
                    {
                        if (w.OrElse != null && w.OrElse.Count > 0)
                        { Refuse("while/else is not supported", w.Span.Start.Line); return; }
                        string top = NewLabel("while");
                        string end = NewLabel("endwhile");
                        Label(top);
                        EmitTruthTest(w.Test);
                        Emit("je      " + end);
                        _breakLabels.Add(end); _continueLabels.Add(top);
                        EmitStatements(w.Body);
                        _breakLabels.RemoveAt(_breakLabels.Count - 1);
                        _continueLabels.RemoveAt(_continueLabels.Count - 1);
                        Emit("jmp     " + top);
                        Label(end);
                    }
                    return;

                case For f: EmitForRange(f); return;

                case Break b:
                    if (_breakLabels.Count == 0) { Refuse("'break' outside a loop", b.Span.Start.Line); return; }
                    Emit("jmp     " + _breakLabels[_breakLabels.Count - 1]);
                    return;

                case Continue c:
                    if (_continueLabels.Count == 0) { Refuse("'continue' outside a loop", c.Span.Start.Line); return; }
                    Emit("jmp     " + _continueLabels[_continueLabels.Count - 1]);
                    return;

                case Pass _: return;

                case ExprStmt e:
                    // Evaluated for effect only. A bare literal (a docstring, usually) has none,
                    // but a call does, so it still has to be emitted.
                    if (e.Value is Constant) return;
                    EmitExpr(e.Value);
                    return;

                default:
                    Refuse(s.GetType().Name + " is not supported in the Python-to-NASM subset",
                           s.Span.Start.Line);
                    return;
            }
        }

        private VType LocalType(string name)
        {
            VType t;
            return _types != null && _types.TryGetLocal(name, out t) ? t : VType.Int;
        }

        /// <summary>
        /// Plain, chained (<c>a = b = 0</c>) and unpacking (<c>a, b = b, a</c>) assignment.
        ///
        /// Python evaluates the whole right-hand side before storing anything, which is what makes
        /// <c>a, b = b, a</c> a swap. Storing each element as it is computed would instead leave
        /// both names holding the old <c>b</c>, so the values are gathered into spill slots first.
        /// </summary>
        private void EmitAssign(Assign a)
        {
            var unpackTarget = a.Targets.Count == 1 ? SequenceElements(a.Targets[0]) : null;
            if (unpackTarget != null)
            {
                IReadOnlyList<Expr> sources = SequenceElements(a.Value);
                if (sources == null)
                {
                    Refuse("unpacking assignment needs a tuple or list on the right-hand side; "
                           + "unpacking an arbitrary iterable needs the object protocol",
                           a.Span.Start.Line);
                    return;
                }
                if (sources.Count != unpackTarget.Count)
                {
                    Refuse("cannot unpack " + sources.Count + " values into "
                           + unpackTarget.Count + " targets", a.Span.Start.Line);
                    return;
                }

                int baseDepth = _spillDepth;
                var types = new VType[sources.Count];
                for (int i = 0; i < sources.Count; i++)
                {
                    var name = unpackTarget[i] as Name;
                    if (name == null)
                    { Refuse("only simple names can be unpacking targets", a.Span.Start.Line); return; }
                    types[i] = LocalType(name.Id);
                    _spillDepth = baseDepth + i;
                    VType got = EmitExpr(sources[i]);
                    Coerce(got, types[i], a.Span.Start.Line);
                    if (_refusal != null) { _spillDepth = baseDepth; return; }
                    StoreAccumulator(SpillName(baseDepth + i), types[i]);
                }
                _spillDepth = baseDepth;

                for (int i = 0; i < unpackTarget.Count; i++)
                {
                    var name = (Name)unpackTarget[i];
                    LoadAccumulator(SpillName(baseDepth + i), types[i]);
                    StoreAccumulator(name.Id, types[i]);
                }
                return;
            }

            // `a = b = value` stores the one value into every target, left to right.
            VType first = a.Targets.Count > 0 ? TargetType(a.Targets[0]) : VType.Int;
            VType value = EmitExpr(a.Value);
            Coerce(value, first, a.Span.Start.Line);
            if (_refusal != null) return;
            foreach (Expr target in a.Targets)
            {
                var name = target as Name;
                if (name == null)
                { Refuse("only assignment to a simple name is supported", a.Span.Start.Line); return; }
                if (LocalType(name.Id) != first)
                { Refuse("chained assignment needs every target to have the same type", a.Span.Start.Line); return; }
                StoreAccumulator(name.Id, first);
            }
        }

        private VType TargetType(Expr target)
        {
            var name = target as Name;
            return name != null ? LocalType(name.Id) : VType.Int;
        }

        private static IReadOnlyList<Expr> SequenceElements(Expr e)
        {
            var tuple = e as TupleExpr;
            if (tuple != null) return tuple.Elts;
            var list = e as ListExpr;
            if (list != null) return list.Elts;
            return null;
        }

        /// <summary>
        /// <c>for i in range(...)</c> only, desugared to an explicit counter loop. A general
        /// iterator needs the object protocol, which this subset has no runtime for.
        /// </summary>
        private void EmitForRange(For f)
        {
            var target = f.Target as Name;
            var call = f.Iter as Call;
            var callee = call != null ? call.Func as Name : null;
            if (target == null || callee == null || callee.Id != "range")
            {
                Refuse("only 'for <name> in range(...)' is supported; a general iterator needs the "
                       + "object protocol", f.Span.Start.Line);
                return;
            }
            if (call.Args.Count < 1 || call.Args.Count > 3)
            { Refuse("range() takes 1 to 3 arguments", f.Span.Start.Line); return; }
            if (call.Keywords != null && call.Keywords.Count > 0)
            { Refuse("range() takes no keyword arguments", f.Span.Start.Line); return; }
            if (f.OrElse != null && f.OrElse.Count > 0)
            { Refuse("for/else is not supported", f.Span.Start.Line); return; }
            foreach (Expr arg in call.Args)
            {
                if (_types.TypeOf(arg) == VType.Float)
                { Refuse("range() takes integer arguments", f.Span.Start.Line); return; }
            }

            // The loop's labels are allocated first so the bookkeeping slots can carry the same
            // sequence number: `.Lfor7` pairs with `$stop7` and `$step7`, which is how the
            // decompiler knows which slots belong to which loop.
            string top = NewLabel("for");
            string suffix = top.Substring(".Lfor".Length);
            string end = NewLabel("endfor");
            string step = NewLabel("forstep");
            string negative = NewLabel("fneg");
            string test = NewLabel("ftest");

            // Bounds are materialised into locals so they are evaluated exactly once, matching
            // Python: range's arguments are not re-evaluated on each iteration.
            string stopVar = "$stop" + suffix;
            string stepVar = "$step" + suffix;
            SlotOf(stopVar); _slotKinds[stopVar] = "stop";
            SlotOf(stepVar); _slotKinds[stepVar] = "step";

            if (call.Args.Count == 1)
            {
                Emit("xor     rax, rax");
                Emit("mov     " + Slot(target.Id) + ", rax");
                EmitExpr(call.Args[0]);
                Emit("mov     " + Slot(stopVar) + ", rax");
                Emit("mov     rax, 1");
                Emit("mov     " + Slot(stepVar) + ", rax");
            }
            else
            {
                EmitExpr(call.Args[0]);
                Emit("mov     " + Slot(target.Id) + ", rax");
                EmitExpr(call.Args[1]);
                Emit("mov     " + Slot(stopVar) + ", rax");
                if (call.Args.Count == 3) EmitExpr(call.Args[2]); else Emit("mov     rax, 1");
                Emit("mov     " + Slot(stepVar) + ", rax");
            }

            Label(top);
            // Direction matters: a negative step counts down, so the terminating comparison flips.
            Emit("mov     rax, " + Slot(stepVar));
            Emit("cmp     rax, 0");
            Emit("jl      " + negative);
            Emit("mov     rax, " + Slot(target.Id));
            Emit("cmp     rax, " + Slot(stopVar));
            Emit("jge     " + end);
            Emit("jmp     " + test);
            Label(negative);
            Emit("mov     rax, " + Slot(target.Id));
            Emit("cmp     rax, " + Slot(stopVar));
            Emit("jle     " + end);
            Label(test);

            _breakLabels.Add(end); _continueLabels.Add(step);
            EmitStatements(f.Body);
            _breakLabels.RemoveAt(_breakLabels.Count - 1);
            _continueLabels.RemoveAt(_continueLabels.Count - 1);

            Label(step);
            Emit("mov     rax, " + Slot(target.Id));
            Emit("add     rax, " + Slot(stepVar));
            Emit("mov     " + Slot(target.Id) + ", rax");
            Emit("jmp     " + top);
            Label(end);
        }

        /// <summary>Leaves ZF set when the expression is falsey, so <c>je</c> skips the body.</summary>
        private void EmitTruthTest(Expr test)
        {
            VType t = EmitExpr(test);
            if (t == VType.Float) FloatToTruthValue();
            Emit("cmp     rax, 0");
        }

        /// <summary>
        /// Turns the double in XMM0 into 0 or 1 in RAX. NaN is truthy in Python but compares
        /// unordered here, which sets ZF exactly as equality does — so the parity flag, not the
        /// zero flag, is what separates "equal to zero" from "not a number".
        /// </summary>
        private void FloatToTruthValue()
        {
            Emit("xorpd   xmm1, xmm1");
            Emit("ucomisd xmm0, xmm1");
            Emit("setne   al");
            Emit("setp    cl");
            Emit("or      al, cl");
            Emit("movzx   rax, al");
        }

        // ---- expressions -------------------------------------------------

        /// <summary>Emits <paramref name="e"/>, leaving an int in RAX or a double in XMM0.</summary>
        private VType EmitExpr(Expr e)
        {
            if (_refusal != null) return VType.Int;
            switch (e)
            {
                case Constant c: return EmitConstant(c);

                case Name nm:
                    {
                        if (!_slots.ContainsKey(nm.Id))
                        { Refuse("name '" + nm.Id + "' is not a parameter or local of this function", e.Span.Start.Line); return VType.Int; }
                        VType t = LocalType(nm.Id);
                        LoadAccumulator(nm.Id, t);
                        return t;
                    }

                case UnaryOpExpr u: return EmitUnary(u);

                case BinOpExpr b:
                    {
                        VType common = TypeTable.Combine(b.Op, _types.TypeOf(b.Left), _types.TypeOf(b.Right));
                        EmitOperandPair(b.Left, b.Right, common);
                        if (_refusal != null) return common;
                        EmitBinaryOp(b.Op, common, e.Span.Start.Line);
                        return common;
                    }

                case Compare cmp: return EmitCompare(cmp);

                case BoolOpExpr bo: return EmitBoolOp(bo);

                case Call call: return EmitCall(call);

                case IfExp cond:
                    {
                        VType want = _types.TypeOf(cond);
                        string elseL = NewLabel("celse"), endL = NewLabel("cend");
                        EmitTruthTest(cond.Test);
                        Emit("je      " + elseL);
                        Coerce(EmitExpr(cond.Body), want, e.Span.Start.Line);
                        Emit("jmp     " + endL);
                        Label(elseL);
                        Coerce(EmitExpr(cond.OrElse), want, e.Span.Start.Line);
                        Label(endL);
                        return want;
                    }

                default:
                    Refuse(e.GetType().Name + " expressions are not supported in the Python-to-NASM subset",
                           e.Span.Start.Line);
                    return VType.Int;
            }
        }

        private VType EmitConstant(Constant c)
        {
            // bool is tested before the integer forms: in Python True IS 1, but a boxed bool is
            // not a boxed int in C# and would otherwise fall through.
            if (c.Value is bool bb) { Emit("mov     rax, " + (bb ? "1" : "0")); return VType.Int; }
            if (c.Value == null) { Emit("xor     rax, rax"); return VType.Int; }   // None
            if (c.Value is double d) { EmitDoubleLiteral(d); return VType.Float; }
            if (c.Value is float ff) { EmitDoubleLiteral(ff); return VType.Float; }
            string literal = IntegerLiteral(c);
            if (literal != null) Emit("mov     rax, " + literal);
            return VType.Int;
        }

        /// <summary>
        /// A double literal is materialised from its exact bit pattern rather than written as
        /// decimal text into a data section: the bits are what the program must compute with, and
        /// round-tripping them through a decimal spelling is one more place to lose a unit in the
        /// last place.
        /// </summary>
        private void EmitDoubleLiteral(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            Emit("mov     rax, 0x" + bits.ToString("X16", CultureInfo.InvariantCulture)
                 + "        ; " + value.ToString("R", CultureInfo.InvariantCulture));
            Emit("movq    xmm0, rax");
        }

        private VType EmitUnary(UnaryOpExpr u)
        {
            if (u.Op == UnaryOp.Not)
            {
                VType t = EmitExpr(u.Operand);
                if (t == VType.Float) FloatToTruthValue();
                Emit("cmp     rax, 0");
                Emit("sete    al");
                Emit("movzx   rax, al");
                return VType.Int;
            }
            if (u.Op == UnaryOp.Invert)
            {
                VType t = EmitExpr(u.Operand);
                if (t == VType.Float)
                { Refuse("'~' is not defined for floats", u.Span.Start.Line); return VType.Int; }
                Emit("not     rax");
                return VType.Int;
            }
            {
                VType t = EmitExpr(u.Operand);
                if (u.Op == UnaryOp.USub)
                {
                    // Flipping the sign bit is exact for every double, including -0.0 and NaN,
                    // which a subtraction from zero is not.
                    if (t == VType.Float)
                    {
                        Emit("movq    rax, xmm0");
                        Emit("btc     rax, 63");
                        Emit("movq    xmm0, rax");
                    }
                    else Emit("neg     rax");
                }
                else if (u.Op != UnaryOp.UAdd) Refuse("unsupported unary operator", u.Span.Start.Line);
                return t;
            }
        }

        /// <summary>
        /// Evaluates <paramref name="left"/> then <paramref name="right"/> — Python's order —
        /// coercing both to <paramref name="common"/> and leaving the left value in the
        /// accumulator (RAX or XMM0) and the right in the second register (RCX or XMM1).
        /// </summary>
        private void EmitOperandPair(Expr left, Expr right, VType common)
        {
            int depth = _spillDepth;
            Coerce(EmitExpr(left), common, left.Span.Start.Line);
            if (_refusal != null) return;
            string spill = SpillName(depth);
            StoreAccumulator(spill, common);
            _spillDepth = depth + 1;
            Coerce(EmitExpr(right), common, right.Span.Start.Line);
            _spillDepth = depth;
            if (_refusal != null) return;
            AccumulatorToSecond(common);
            LoadAccumulator(spill, common);
        }

        /// <summary>
        /// A call to another function of the same module, or one of the two numeric conversions.
        /// Arguments are evaluated left to right into spill slots first, then moved into place: an
        /// argument that is itself a call would otherwise overwrite the argument registers already
        /// loaded for the outer one.
        /// </summary>
        private VType EmitCall(Call call)
        {
            var callee = call.Func as Name;
            if (callee == null)
            {
                Refuse("only a direct call to a function of this module is supported; there are no "
                       + "function objects in this subset", call.Span.Start.Line);
                return VType.Int;
            }
            if (callee.Id == "range")
            {
                Refuse("range() is only supported as the iterable of a 'for' statement",
                       call.Span.Start.Line);
                return VType.Int;
            }
            if (callee.Id == "int" || callee.Id == "float") return EmitConversion(call, callee.Id);

            FunctionType target;
            if (!_signatures.TryGetValue(callee.Id, out target))
            {
                Refuse("'" + callee.Id + "' is not a function defined in this module; built-ins and "
                       + "imports have no implementation to call", call.Span.Start.Line);
                return VType.Int;
            }
            if (call.Keywords != null && call.Keywords.Count > 0)
            {
                Refuse("keyword arguments are not supported; the calling convention is positional",
                       call.Span.Start.Line);
                return VType.Int;
            }
            // Splats are checked before arity: `g(*xs)` supplies an unknown number of arguments,
            // so counting the syntactic ones and reporting "takes 2 but 1 were given" would send
            // the author looking for the wrong mistake.
            foreach (Expr arg in call.Args)
            {
                if (arg is Starred)
                {
                    Refuse("argument unpacking ('*') is not supported; the number of arguments must "
                           + "be known when the call is compiled", call.Span.Start.Line);
                    return VType.Int;
                }
            }
            if (call.Args.Count != target.Parameters.Count)
            {
                int arity = target.Parameters.Count;
                Refuse("'" + callee.Id + "' takes " + arity + " argument" + (arity == 1 ? "" : "s")
                       + " but " + call.Args.Count + " were given", call.Span.Start.Line);
                return VType.Int;
            }

            if (call.Args.Count > _maxCallArity) _maxCallArity = call.Args.Count;

            int baseDepth = _spillDepth;
            for (int i = 0; i < call.Args.Count; i++)
            {
                _spillDepth = baseDepth + i;
                Coerce(EmitExpr(call.Args[i]), target.Parameters[i], call.Args[i].Span.Start.Line);
                if (_refusal != null) { _spillDepth = baseDepth; return VType.Int; }
                StoreAccumulator(SpillName(baseDepth + i), target.Parameters[i]);
            }
            _spillDepth = baseDepth;

            // Stack arguments go above the callee's shadow space, at [rsp + 8*i]. They are written
            // before the register arguments so RAX is still free to carry them, and their bits
            // move the same way whether they mean an integer or a double.
            for (int i = call.Args.Count - 1; i >= ArgRegisters.Length; i--)
            {
                Emit("mov     rax, " + Slot(SpillName(baseDepth + i)));
                Emit("mov     qword [rsp+" + (8 * i) + "], rax");
            }
            for (int i = 0; i < call.Args.Count && i < ArgRegisters.Length; i++)
            {
                string spill = SpillName(baseDepth + i);
                Emit(target.Parameters[i] == VType.Float
                    ? "movsd   " + FloatArgRegisters[i] + ", " + Mem(spill)
                    : "mov     " + ArgRegisters[i] + ", " + Slot(spill));
            }

            Emit("call    " + callee.Id);
            return target.Returns;
        }

        /// <summary>
        /// <c>int(x)</c> and <c>float(x)</c>. <c>cvttsd2si</c> truncates toward zero, which is
        /// exactly what Python's <c>int()</c> does to a float — unlike <c>//</c>, which floors.
        /// </summary>
        private VType EmitConversion(Call call, string which)
        {
            if (call.Args.Count != 1 || (call.Keywords != null && call.Keywords.Count > 0))
            {
                Refuse(which + "() takes exactly one argument in this subset", call.Span.Start.Line);
                return VType.Int;
            }
            VType from = EmitExpr(call.Args[0]);
            if (which == "int")
            {
                if (from == VType.Float) Emit("cvttsd2si rax, xmm0");
                return VType.Int;
            }
            if (from == VType.Int) Emit("cvtsi2sd xmm0, rax");
            return VType.Float;
        }

        /// <summary>
        /// Literal text for an integer constant, or null after refusing. Python integers are
        /// arbitrary precision and a 64-bit register is not, so anything that does not fit is
        /// refused rather than silently truncated.
        /// </summary>
        private string IntegerLiteral(Constant n)
        {
            if (n.Value is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (n.Value is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (n.Value is System.Numerics.BigInteger big)
            {
                if (big >= long.MinValue && big <= long.MaxValue)
                    return ((long)big).ToString(CultureInfo.InvariantCulture);
                Refuse("integer literal does not fit in 64 bits; Python integers are arbitrary "
                       + "precision and this subset has no bignum runtime", n.Span.Start.Line);
                return null;
            }
            if (n.Value is string || n.Value is char)
            {
                Refuse("string constants are not supported: there is no heap in this subset",
                       n.Span.Start.Line);
                return null;
            }
            Refuse("only int, float, True/False and None constants are supported", n.Span.Start.Line);
            return null;
        }

        /// <summary>accumulator op second -> accumulator.</summary>
        private void EmitBinaryOp(BinOp op, VType type, int line)
        {
            if (type == VType.Float)
            {
                switch (op)
                {
                    case BinOp.Add: Emit("addsd   xmm0, xmm1"); return;
                    case BinOp.Sub: Emit("subsd   xmm0, xmm1"); return;
                    case BinOp.Mult: Emit("mulsd   xmm0, xmm1"); return;
                    case BinOp.Div: Emit("divsd   xmm0, xmm1"); return;
                    case BinOp.FloorDiv:
                    case BinOp.Mod:
                        // CPython computes both through libm's fmod and then corrects the sign.
                        // floor(a/b) is NOT the same value in the last place, so emitting it would
                        // be a compiler that is nearly right — the worst kind.
                        Refuse("'" + (op == BinOp.Mod ? "%" : "//") + "' on floats needs libm's "
                               + "fmod to match CPython exactly, and there is no runtime to call",
                               line);
                        return;
                    default:
                        Refuse("operator '" + op + "' is not defined for floats", line);
                        return;
                }
            }

            switch (op)
            {
                case BinOp.Add: Emit("add     rax, rcx"); return;
                case BinOp.Sub: Emit("sub     rax, rcx"); return;
                case BinOp.Mult: Emit("imul    rax, rcx"); return;
                case BinOp.BitAnd: Emit("and     rax, rcx"); return;
                case BinOp.BitOr: Emit("or      rax, rcx"); return;
                case BinOp.BitXor: Emit("xor     rax, rcx"); return;
                // The shift count already lives in RCX, which is exactly where x86 wants it.
                case BinOp.LShift: Emit("shl     rax, cl"); return;
                case BinOp.RShift: Emit("sar     rax, cl"); return;   // arithmetic: Python's >> keeps the sign
                case BinOp.FloorDiv: EmitFloorDiv(false); return;
                case BinOp.Mod: EmitFloorDiv(true); return;
                default:
                    Refuse("operator '" + op + "' is not supported in the integer subset", line);
                    return;
            }
        }

        /// <summary>
        /// Python's <c>//</c> and <c>%</c> FLOOR toward negative infinity; x86 <c>idiv</c>
        /// truncates toward zero. They differ whenever the operands have opposite signs
        /// (<c>-7 // 2</c> is -4 in Python but -3 from idiv), so the correction below is not
        /// optional — without it the compiler is silently wrong on negative numbers.
        ///
        /// Division by zero traps the process rather than raising ZeroDivisionError: a
        /// bare-assembly subset has no exception machinery to raise it with.
        /// </summary>
        private void EmitFloorDiv(bool wantRemainder)
        {
            string done = NewLabel("divdone");
            Emit("cqo");                     // sign-extend RAX into RDX:RAX
            Emit("idiv    rcx");             // RAX = quotient (truncated), RDX = remainder
            Emit("test    rdx, rdx");
            Emit("je      " + done);         // exact division needs no correction
            Emit("mov     rbx, rdx");
            Emit("xor     rbx, rcx");        // sign(remainder) != sign(divisor)?
            Emit("jns     " + done);
            Emit("sub     rax, 1");          // floor the quotient
            Emit("add     rdx, rcx");        // and bring the remainder into the divisor's sign
            Label(done);
            if (wantRemainder) Emit("mov     rax, rdx");
        }

        private VType EmitCompare(Compare cmp)
        {
            if (cmp.Comparators.Count != 1 || cmp.Ops.Count != 1)
            {
                // Chained comparisons (a < b < c) short-circuit and evaluate b once; refuse rather
                // than emit the subtly different `a < b and b < c`.
                Refuse("chained comparisons are not supported", cmp.Span.Start.Line);
                return VType.Int;
            }
            VType common = (_types.TypeOf(cmp.Left) == VType.Float
                            || _types.TypeOf(cmp.Comparators[0]) == VType.Float)
                ? VType.Float : VType.Int;
            EmitOperandPair(cmp.Left, cmp.Comparators[0], common);
            if (_refusal != null) return VType.Int;
            if (common == VType.Float) return EmitFloatCompare(cmp.Ops[0], cmp.Span.Start.Line);

            Emit("cmp     rax, rcx");
            string set;
            switch (cmp.Ops[0])
            {
                case CmpOp.Eq: set = "sete"; break;
                case CmpOp.NotEq: set = "setne"; break;
                case CmpOp.Lt: set = "setl"; break;
                case CmpOp.LtE: set = "setle"; break;
                case CmpOp.Gt: set = "setg"; break;
                case CmpOp.GtE: set = "setge"; break;
                default:
                    Refuse("comparison '" + cmp.Ops[0] + "' is not supported", cmp.Span.Start.Line);
                    return VType.Int;
            }
            Emit(set + "    al");
            Emit("movzx   rax, al");
            return VType.Int;
        }

        /// <summary>
        /// Ordered comparison of XMM0 against XMM1.
        ///
        /// NaN makes every comparison false except <c>!=</c>. <c>ucomisd</c> reports "unordered"
        /// by setting CF, ZF and PF together, which looks identical to "less than or equal" — so
        /// <c>&lt;</c> and <c>&lt;=</c> are emitted with the operands swapped, where the unordered
        /// case sets CF and <c>seta</c>/<c>setae</c> correctly answer false. Equality has no such
        /// trick and has to consult the parity flag directly.
        /// </summary>
        private VType EmitFloatCompare(CmpOp op, int line)
        {
            switch (op)
            {
                case CmpOp.Gt: Emit("ucomisd xmm0, xmm1"); Emit("seta    al"); break;
                case CmpOp.GtE: Emit("ucomisd xmm0, xmm1"); Emit("setae   al"); break;
                case CmpOp.Lt: Emit("ucomisd xmm1, xmm0"); Emit("seta    al"); break;
                case CmpOp.LtE: Emit("ucomisd xmm1, xmm0"); Emit("setae   al"); break;
                case CmpOp.Eq:
                    Emit("ucomisd xmm0, xmm1");
                    Emit("sete    al");
                    Emit("setnp   cl");          // unordered is not equality
                    Emit("and     al, cl");
                    break;
                case CmpOp.NotEq:
                    Emit("ucomisd xmm0, xmm1");
                    Emit("setne   al");
                    Emit("setp    cl");          // unordered IS inequality
                    Emit("or      al, cl");
                    break;
                default:
                    Refuse("comparison '" + op + "' is not supported for floats", line);
                    return VType.Int;
            }
            Emit("movzx   rax, al");
            return VType.Int;
        }

        /// <summary>
        /// <c>and</c>/<c>or</c> short-circuit. Python yields the OPERAND rather than a bool, and
        /// this preserves that: the value left behind is the operand that decided the result, and
        /// the right side is not evaluated when the left already settles it.
        /// </summary>
        private VType EmitBoolOp(BoolOpExpr bo)
        {
            VType want = _types.TypeOf(bo);
            string end = NewLabel("boolend");
            bool isAnd = bo.Op == BoolOp.And;
            for (int i = 0; i < bo.Values.Count; i++)
            {
                Coerce(EmitExpr(bo.Values[i]), want, bo.Values[i].Span.Start.Line);
                if (_refusal != null) return want;
                if (i == bo.Values.Count - 1) break;
                // The operand itself is the result when it decides the outcome, so the truth test
                // must not disturb it. FloatToTruthValue only touches XMM1 and RAX.
                if (want == VType.Float) FloatToTruthValue();
                Emit("cmp     rax, 0");
                Emit(isAnd ? "je      " + end : "jne     " + end);
            }
            Label(end);
            return want;
        }
    }
}
