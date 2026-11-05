using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HelixPythonNamespace.CppAsmCompiler
{
    /// <summary>
    /// Reads NASM (win64) produced by <see cref="CppToNasm"/> back into C++.
    ///
    /// Also reads PART of the assembly the Python translator emits; both write the same
    /// <c>; helix.</c> symbol map. That emitter speaks a different dialect — it parks RBX in a
    /// frame slot across the body, keeps integers in 64-bit RAX rather than 32-bit EAX, types its
    /// slots <c>float</c> where this one says <c>double</c>, and tests truth with <c>cmp rax, 0</c>.
    /// Those shapes are rewritten into this emitter's spelling (<see cref="ToCxxSpelling"/>), and
    /// a Python <c>int</c> maps to <c>long long</c>, not <c>int</c>, or large values would
    /// silently truncate. Straight-line integer and float code, calls, if/else and while are
    /// read; every other Python shape (range loops, <c>and</c>/<c>or</c>, conditional expressions,
    /// the floor-division correction after <c>idiv</c>) is refused with the offending label or
    /// instruction rather than guessed at.
    ///
    /// Forward compilation is lossy. A <c>while</c> and an <c>if</c> around a backward jump compile
    /// to the same instructions, a local and a compiler temporary are both just an offset from RBP,
    /// and a promotion the author never wrote is indistinguishable from one they did. Recovering
    /// structure from arbitrary machine code is what decompilers spend decades on, so this one does
    /// not attempt it: it recognises the exact shapes the emitters produce, using the
    /// <c>; helix.</c> symbol map they write alongside — the same debug information a compiler puts
    /// in <c>.debug_info</c> under <c>-g</c> — and refuses anything else.
    ///
    /// The contract is therefore a ROUND TRIP, not general decompilation: for source the forward
    /// path accepts, <c>x -&gt; asm -&gt; c++</c> computes what the original computed. That is what
    /// makes <c>Python -&gt; ASM -&gt; C++</c> a defensible pipeline: the assembly in the middle is
    /// not arbitrary, it is assembly this repository emitted and annotated.
    /// </summary>
    public static class NasmToCpp
    {
        public static DecompileResult Decompile(string assembly)
        {
            if (string.IsNullOrWhiteSpace(assembly)) return DecompileResult.Refuse("empty assembly");

            List<AsmLine> lines;
            List<FunctionInfo> functions;
            bool python = PythonDialect.IsMatch(assembly);
            string reason = Parse(assembly, python, out lines, out functions);
            if (reason != null) return DecompileResult.Refuse(reason);
            if (functions.Count == 0)
                return DecompileResult.Refuse(
                    "no '; helix.function' blocks found; this decompiler reads assembly produced by "
                    + "HelixPython, which carries a symbol map. Arbitrary NASM has no record of "
                    + "which offsets were variables or which jumps were loops.");

            var known = new Dictionary<string, FunctionInfo>(StringComparer.Ordinal);
            foreach (FunctionInfo f in functions) known[f.Name] = f;

            var text = new StringBuilder();
            text.AppendLine("// Recovered from NASM by HelixPython.");
            text.AppendLine();

            // Prototypes first: the recovered order is the emitted order, and C++ requires a name
            // to be declared before it is used. Without these, any forward or mutual call in the
            // original would not compile after the round trip.
            foreach (FunctionInfo f in functions)
                text.AppendLine(Signature(f) + ";");

            foreach (FunctionInfo f in functions)
            {
                var decoder = new Decoder(lines, f, known, python);
                List<string> body = decoder.Run();
                if (body == null) return DecompileResult.Refuse(decoder.Refusal, decoder.RefusalLine);

                text.AppendLine();
                text.AppendLine(Signature(f));
                text.AppendLine("{");
                foreach (string line in body) text.AppendLine("    " + line);
                text.AppendLine("}");
            }
            return DecompileResult.Ok(text.ToString());
        }

        private static string Signature(FunctionInfo f)
        {
            var parameters = new List<string>();
            foreach (SlotInfo p in f.Parameters) parameters.Add(p.Type + " " + p.Name);
            return f.Returns + " " + f.Name + "(" + string.Join(", ", parameters) + ")";
        }

        // ---- assembly model ------------------------------------------------------------------

        private sealed class AsmLine
        {
            public string Label;
            public string Op = string.Empty;
            public string[] Args = new string[0];
            public int Number;

            public string Arg(int i) { return i < Args.Length ? Args[i] : string.Empty; }
            public bool Is(string op, params string[] args)
            {
                if (Op != op || args.Length > Args.Length) return false;
                for (int i = 0; i < args.Length; i++) if (Args[i] != args[i]) return false;
                return true;
            }
            public override string ToString()
            {
                return Label != null ? Label + ":" : Op + " " + string.Join(", ", Args);
            }
        }

        private sealed class SlotInfo
        {
            public int Offset;
            public string Kind = "temp";
            public string Name = string.Empty;
            public string Type = "int";
        }

        private sealed class FunctionInfo
        {
            public string Name = string.Empty;
            public string Returns = "int";
            public readonly List<SlotInfo> Parameters = new List<SlotInfo>();
            public readonly Dictionary<int, SlotInfo> Slots = new Dictionary<int, SlotInfo>();
            public int BodyStart;
            public int BodyEnd;
        }

        // ---- the Python emitter's dialect ----------------------------------------------------

        /// <summary>
        /// Only the Python emitter parks RBX in a frame slot, and it says so in the symbol map
        /// (<c>; helix.slot N saved $rbx any</c>). That line, not the banner comment, is what
        /// identifies the dialect.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex PythonDialect =
            new System.Text.RegularExpressions.Regex(@"^\s*;\s*helix\.slot\s+\d+\s+saved\s",
                System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>
        /// The labels whose Python shapes this reader knows: if/else, while, and the epilogue.
        /// Every other kind (<c>for</c> over range, <c>and</c>/<c>or</c>, conditional
        /// expressions, the floor-division correction) has a shape of its own that would decode
        /// into something plausible and wrong through the C++ rules, so it is refused instead.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex PythonKnownLabel =
            new System.Text.RegularExpressions.Regex(@"^\.L(?:else|endif|while|endwhile)\d+$|^\.Lepi_");

        /// <summary>
        /// Rewrites the Python emitter's instructions into the C++ emitter's spelling, so one
        /// decoder reads both. The value semantics do not change: the Python side computes in
        /// 64-bit registers, and its integer slots are typed <c>long long</c> (see
        /// <see cref="CxxType"/>) so the recovered C++ computes in 64 bits too. Only register
        /// NAMES are narrowed here, because the decoder matches names, never widths.
        /// </summary>
        private static string ToCxxSpelling(List<AsmLine> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                AsmLine l = lines[i];
                if (l.Label != null)
                {
                    if (l.Label.StartsWith(".L", StringComparison.Ordinal) && !PythonKnownLabel.IsMatch(l.Label))
                        return "the Python shape behind label '" + l.Label + "' is not read back into C++ yet";
                    continue;
                }
                if (l.Op.StartsWith("j", StringComparison.Ordinal) && l.Arg(0).StartsWith(".L", StringComparison.Ordinal)
                    && !PythonKnownLabel.IsMatch(l.Arg(0)))
                    return "the Python shape behind label '" + l.Arg(0) + "' is not read back into C++ yet";

                // A double literal travels through RAX as raw bits: `mov rax, <bits>` / `movq xmm, rax`.
                bool bitsOfADouble = l.Op == "movq"
                    || (l.Op == "mov" && i + 1 < lines.Count && lines[i + 1].Op == "movq");
                if (!bitsOfADouble)
                    for (int a = 0; a < l.Args.Length; a++) l.Args[a] = Narrow(l.Args[a]);

                if (l.Op == "ucomisd") l.Op = "comisd";
                else if (l.Op == "shl") l.Op = "sal";

                // A truth test: `cmp rax, 0` / `je L` is the C++ side's `test eax, eax` / `jz L`.
                if (l.Is("cmp", "eax", "0") && i + 1 < lines.Count && lines[i + 1].Op == "je")
                {
                    l.Args = new[] { "eax", "eax" };
                    l.Op = "test";
                    lines[i + 1].Op = "jz";
                }
            }
            return null;
        }

        private static string Narrow(string register)
        {
            switch (register)
            {
                case "rax": return "eax";
                case "rcx": return "ecx";
                case "rdx": return "edx";
                case "r8": return "r8d";
                case "r9": return "r9d";
                default: return register;
            }
        }

        /// <summary>
        /// A Python type as C++ spells it. A Python <c>int</c> lives in a 64-bit register, so it is
        /// <c>long long</c>: as <c>int</c> any value past 2^31 would silently truncate.
        /// </summary>
        private static string CxxType(string pythonType)
        {
            switch (pythonType)
            {
                case "int": return "long long";
                case "float": return "double";
                default: return pythonType;
            }
        }

        private static string Parse(string assembly, bool python, out List<AsmLine> lines,
                                    out List<FunctionInfo> functions)
        {
            lines = new List<AsmLine>();
            functions = new List<FunctionInfo>();
            FunctionInfo pending = null;
            int number = 0;

            foreach (string raw in assembly.Replace("\r\n", "\n").Split('\n'))
            {
                number++;
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith(";", StringComparison.Ordinal))
                {
                    string reason = ReadMetadata(line, ref pending, functions);
                    if (reason != null) return reason;
                    continue;
                }
                int comment = line.IndexOf(';');
                string code = comment >= 0 ? line.Substring(0, comment).Trim() : line;
                if (code.Length == 0) continue;

                if (code.StartsWith("default ", StringComparison.Ordinal)
                    || code.StartsWith("section ", StringComparison.Ordinal)
                    || code.StartsWith("global ", StringComparison.Ordinal)
                    || code.StartsWith("extern ", StringComparison.Ordinal)) continue;

                var asm = new AsmLine { Number = number };
                if (code.EndsWith(":", StringComparison.Ordinal))
                {
                    asm.Label = code.Substring(0, code.Length - 1).Trim();
                }
                else
                {
                    int space = code.IndexOf(' ');
                    if (space < 0) { asm.Op = code; }
                    else
                    {
                        asm.Op = code.Substring(0, space).Trim();
                        var args = code.Substring(space + 1).Split(',');
                        for (int i = 0; i < args.Length; i++) args[i] = Normalize(args[i]);
                        asm.Args = args;
                    }
                }
                lines.Add(asm);
            }

            if (python)
            {
                string unread = ToCxxSpelling(lines);
                if (unread != null) return unread;
                foreach (FunctionInfo f in functions)
                {
                    f.Returns = CxxType(f.Returns);
                    foreach (SlotInfo s in f.Slots.Values) s.Type = CxxType(s.Type);
                }
            }

            foreach (FunctionInfo f in functions)
            {
                string reason = LocateBody(lines, f);
                if (reason != null) return reason;
            }
            return null;
        }

        private static string Normalize(string operand)
        {
            string s = operand.Trim();
            // "dword [rbp-8]" and "qword [rbp-8]" name the same slot; the width is implied by the
            // instruction, so the reader keeps only the address.
            foreach (string size in new[] { "dword ", "qword ", "byte ", "word " })
                if (s.StartsWith(size, StringComparison.Ordinal)) { s = s.Substring(size.Length).Trim(); break; }
            return s.Replace(" ", string.Empty);
        }

        private static string ReadMetadata(string comment, ref FunctionInfo pending,
                                           List<FunctionInfo> functions)
        {
            string body = comment.TrimStart(';').Trim();
            if (body.StartsWith("helix.function ", StringComparison.Ordinal))
            {
                string rest = body.Substring("helix.function ".Length).Trim();
                int arrow = rest.IndexOf("->", StringComparison.Ordinal);
                if (arrow < 0) return "malformed '; helix.function' line: " + body;
                pending = new FunctionInfo
                {
                    Name = rest.Substring(0, arrow).Trim(),
                    Returns = rest.Substring(arrow + 2).Trim(),
                };
                functions.Add(pending);
                return null;
            }
            if (body.StartsWith("helix.slot ", StringComparison.Ordinal))
            {
                if (pending == null) return "'; helix.slot' before any '; helix.function'";
                string[] parts = body.Substring("helix.slot ".Length)
                                     .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) return "malformed '; helix.slot' line: " + body;
                int offset;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
                    return "malformed slot offset in: " + body;
                var slot = new SlotInfo { Offset = offset, Kind = parts[1], Name = parts[2], Type = parts[3] };
                pending.Slots[offset] = slot;
                if (slot.Kind == "param") pending.Parameters.Add(slot);
                return null;
            }
            return null;   // other comments are commentary
        }

        /// <summary>
        /// Finds the instructions between a function's prologue and its epilogue. Everything the
        /// prologue does (frame setup, homing the parameters) is implied by the signature, so it is
        /// skipped rather than decoded.
        /// </summary>
        private static string LocateBody(List<AsmLine> lines, FunctionInfo f)
        {
            int start = -1;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Label == f.Name) { start = i; break; }
            if (start < 0) return "no label for function '" + f.Name + "'";

            int i2 = start + 1;
            while (i2 < lines.Count && (lines[i2].Op == "push" || lines[i2].Op == "mov" || lines[i2].Op == "sub"
                                        || lines[i2].Op == "movsd"))
            {
                AsmLine l = lines[i2];
                bool prologue =
                    l.Is("push", "rbp") ||
                    l.Is("mov", "rbp", "rsp") ||
                    (l.Op == "sub" && l.Arg(0) == "rsp") ||
                    // homing an incoming argument into its slot
                    (l.Args.Length == 2 && l.Arg(0).StartsWith("[rbp-", StringComparison.Ordinal)
                        && IsIncomingRegister(l.Arg(1))) ||
                    // the Python emitter parks the callee-saved RBX in its 'saved' slot
                    (l.Args.Length == 2 && l.Arg(0).StartsWith("[rbp-", StringComparison.Ordinal)
                        && l.Arg(1) == "rbx") ||
                    (l.Args.Length == 2 && l.Arg(1).StartsWith("[rbp+", StringComparison.Ordinal)) ||
                    (l.Args.Length == 2 && l.Arg(0).StartsWith("[rbp-", StringComparison.Ordinal)
                        && (l.Arg(1) == "eax" || l.Arg(1) == "xmm0") && i2 > start + 2
                        && lines[i2 - 1].Arg(1).StartsWith("[rbp+", StringComparison.Ordinal));
                if (!prologue) break;
                i2++;
            }
            f.BodyStart = i2;

            int end = -1;
            for (int j = f.BodyStart; j < lines.Count; j++)
                if (lines[j].Label == ".Lepi_" + f.Name) { end = j; break; }
            if (end < 0) return "no epilogue label for function '" + f.Name + "'";

            // The emitter always plants a fall-off-the-end return just before the epilogue; it is
            // implied by the language, not something the source contained.
            if (end - 1 >= f.BodyStart
                && (lines[end - 1].Is("xor", "eax", "eax") || lines[end - 1].Is("xorpd", "xmm0", "xmm0")))
                end--;
            f.BodyEnd = end;
            return null;
        }

        private static bool IsIncomingRegister(string r)
        {
            switch (r)
            {
                case "ecx": case "edx": case "r8d": case "r9d":
                case "rcx": case "rdx": case "r8": case "r9":
                case "xmm0": case "xmm1": case "xmm2": case "xmm3": return true;
                default: return false;
            }
        }

        // ---- recovered expressions -----------------------------------------------------------

        private const int PrecAtom = 100;
        private const int PrecUnary = 90;
        private const int PrecMul = 80;
        private const int PrecAdd = 70;
        private const int PrecShift = 65;
        private const int PrecRel = 60;
        private const int PrecEq = 55;
        private const int PrecBitAnd = 50;
        private const int PrecBitXor = 45;
        private const int PrecBitOr = 40;
        private const int PrecLogAnd = 35;
        private const int PrecLogOr = 30;

        private sealed class Val
        {
            public readonly string Text;
            public readonly int Prec;
            public Val(string text, int prec) { Text = text; Prec = prec; }
            public static Val Atom(string text) { return new Val(text, PrecAtom); }
            /// <summary>Parenthesised only where precedence would otherwise change the meaning.</summary>
            public string In(int need) { return Prec < need ? "(" + Text + ")" : Text; }
            public override string ToString() { return Text; }
        }

        private static Val Binary(Val left, string op, Val right, int prec)
        {
            return new Val(left.In(prec) + " " + op + " " + right.In(prec + 1), prec);
        }

        // ---- decoder -------------------------------------------------------------------------

        private sealed class Decoder
        {
            private readonly List<AsmLine> _lines;
            private readonly FunctionInfo _fn;
            private readonly IReadOnlyDictionary<string, FunctionInfo> _known;
            private readonly Dictionary<int, Val> _slots = new Dictionary<int, Val>();
            private readonly List<string> _loopEnds = new List<string>();
            private readonly List<string> _loopContinues = new List<string>();
            // The loop head each innermost back edge jumps to. In a `while` the back edge and a
            // source-level `continue` are the SAME jump to the SAME label; what separates them is
            // that the back edge is the last thing before the loop's end label.
            private readonly List<string> _loopBackEdges = new List<string>();
            private readonly HashSet<string> _declared = new HashSet<string>(StringComparer.Ordinal);

            private Val _acc = Val.Atom("0");
            private Val _second = Val.Atom("0");
            private int _i;

            public string Refusal { get; private set; }
            public int RefusalLine { get; private set; }

            private readonly bool _python;

            public Decoder(List<AsmLine> lines, FunctionInfo fn,
                           IReadOnlyDictionary<string, FunctionInfo> known, bool python)
            {
                _lines = lines;
                _fn = fn;
                _known = known;
                _python = python;
                foreach (SlotInfo p in fn.Parameters) _declared.Add(p.Name);
            }

            private void Refuse(string reason, int index)
            {
                if (Refusal == null)
                {
                    Refusal = reason;
                    RefusalLine = index >= 0 && index < _lines.Count ? _lines[index].Number : 0;
                }
            }

            public List<string> Run()
            {
                _i = _fn.BodyStart;
                List<string> body = DecodeStatements(_fn.BodyEnd);
                return Refusal == null ? body : null;
            }

            private SlotInfo SlotAt(int offset)
            {
                SlotInfo info;
                return _fn.Slots.TryGetValue(offset, out info) ? info : null;
            }

            private static bool TryFrameOffset(string operand, out int offset)
            {
                offset = 0;
                if (!operand.StartsWith("[rbp-", StringComparison.Ordinal)
                    || !operand.EndsWith("]", StringComparison.Ordinal)) return false;
                string digits = operand.Substring(5, operand.Length - 6);
                return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
            }

            private static bool IsStoreToSlot(AsmLine line, out int offset)
            {
                offset = 0;
                if (line.Op != "mov" && line.Op != "movsd") return false;
                if (line.Args.Length != 2) return false;
                if (line.Arg(1) != "eax" && line.Arg(1) != "xmm0") return false;
                return TryFrameOffset(line.Arg(0), out offset);
            }

            private static bool IsLoadFromSlot(AsmLine line, out int offset)
            {
                offset = 0;
                if (line.Op != "mov" && line.Op != "movsd") return false;
                if (line.Args.Length != 2) return false;
                if (line.Arg(0) != "eax" && line.Arg(0) != "xmm0") return false;
                return TryFrameOffset(line.Arg(1), out offset);
            }

            /// <summary>The label a structured block ends at, given its opening label.</summary>
            private static string Companion(string label, string openKind, string closeKind)
            {
                return closeKind + label.Substring(openKind.Length);
            }

            private List<string> DecodeStatements(int stop)
            {
                var output = new List<string>();
                while (_i < stop && Refusal == null)
                {
                    AsmLine line = _lines[_i];

                    if (line.Label != null)
                    {
                        if (line.Label.StartsWith(".Lwhile", StringComparison.Ordinal)) { DecodeWhile(output, stop); continue; }
                        if (line.Label.StartsWith(".Ldo", StringComparison.Ordinal)
                            && !line.Label.StartsWith(".Ldotest", StringComparison.Ordinal)) { DecodeDo(output, stop); continue; }
                        if (line.Label.StartsWith(".Lfor", StringComparison.Ordinal)
                            && !line.Label.StartsWith(".Lforcont", StringComparison.Ordinal)) { DecodeFor(output, stop); continue; }
                        // Any other label at statement level closes a block the caller opened.
                        return output;
                    }

                    int slot;
                    if (IsStoreToSlot(line, out slot))
                    {
                        SlotInfo info = SlotAt(slot);
                        if (info == null) { Refuse("store to an unmapped frame slot", _i); return output; }
                        _i++;
                        if (info.Kind == "local" || info.Kind == "param")
                        {
                            output.Add(Assignment(info));
                            continue;
                        }
                        _slots[slot] = _acc;          // a compiler temporary: remember, emit nothing
                        continue;
                    }

                    if (line.Op == "jmp")
                    {
                        string target = line.Arg(0);
                        if (target == ".Lepi_" + _fn.Name) { output.Add("return " + _acc + ";"); _i++; continue; }
                        if (_loopBackEdges.Count > 0 && target == _loopBackEdges[_loopBackEdges.Count - 1]
                            && _i + 1 < _lines.Count
                            && _lines[_i + 1].Label == _loopEnds[_loopEnds.Count - 1])
                            return output;                 // the loop's own back edge, not `continue`
                        if (_loopEnds.Count > 0 && target == _loopEnds[_loopEnds.Count - 1])
                        { output.Add("break;"); _i++; continue; }
                        if (_loopContinues.Count > 0 && target == _loopContinues[_loopContinues.Count - 1])
                        { output.Add("continue;"); _i++; continue; }
                        return output;                 // a jump out of a block the caller opened
                    }

                    // A conditional jump at statement level opens an `if`.
                    string ifTarget;
                    if (LooksLikeCondition(_i, out ifTarget)) { DecodeIf(output, stop); continue; }

                    if (!Step(output)) return output;
                }
                return output;
            }

            /// <summary>Emits a declaration the first time a local is written, an assignment after.</summary>
            private string Assignment(SlotInfo info)
            {
                if (_declared.Add(info.Name)) return info.Type + " " + info.Name + " = " + _acc + ";";
                return info.Name + " = " + _acc + ";";
            }

            // ---- conditions -------------------------------------------------------------------

            /// <summary>
            /// True when the instruction at <paramref name="at"/> starts a jump-if-false sequence,
            /// reporting where it jumps. Both the integer and the double shapes are recognised.
            /// </summary>
            private bool LooksLikeCondition(int at, out string target)
            {
                target = null;
                if (at + 1 >= _lines.Count) return false;
                AsmLine a = _lines[at], b = _lines[at + 1];
                if (a.Is("test", "eax", "eax") && b.Op == "jz") { target = b.Arg(0); return true; }
                if (a.Is("xorpd", "xmm1", "xmm1") && at + 3 < _lines.Count
                    && _lines[at + 1].Op == "comisd" && _lines[at + 2].Op == "jp" && _lines[at + 3].Op == "je")
                { target = _lines[at + 3].Arg(0); return true; }
                return false;
            }

            /// <summary>
            /// Consumes one jump-if-false sequence and returns the condition it tested, already
            /// negated into "the expression that must hold to CONTINUE".
            /// </summary>
            private Val ConsumeCondition(out string target)
            {
                target = null;
                AsmLine a = _lines[_i];
                if (a.Is("test", "eax", "eax"))
                {
                    target = _lines[_i + 1].Arg(0);
                    _i += 2;
                    return _acc;
                }
                // double: xorpd / comisd / jp / je  -> "value != 0" with NaN counting as true
                target = _lines[_i + 3].Arg(0);
                _i += 4;
                return _acc;
            }

            /// <summary>
            /// Reads a whole condition, folding the `&amp;&amp;` and `||` shapes the emitter
            /// produces back into one expression. `&amp;&amp;` is a run of jump-if-false to the
            /// same target; `||` is the .Lorfail / .Lor dance.
            /// </summary>
            /// <summary>
            /// Where the next jump-if-false in the current straight-line run jumps, WITHOUT
            /// consuming anything. A condition is preceded by the instructions that evaluate its
            /// operands, so the reader has to look past those to know whether what follows belongs
            /// to this condition (another `&amp;&amp;` term) or is the next statement's own test.
            /// Returns null if a label or a jump intervenes.
            /// </summary>
            private string PeekNextConditionTarget(int from)
            {
                for (int k = from; k < _lines.Count; k++)
                {
                    if (_lines[k].Label != null) return null;
                    if (_lines[k].Op == "jmp") return null;
                    string target;
                    if (LooksLikeCondition(k, out target)) return target;
                }
                return null;
            }

            /// <summary>Folds the operand instructions that precede a test into the accumulator.</summary>
            private bool EvaluateUntilCondition(List<string> output)
            {
                while (Refusal == null)
                {
                    string ignored;
                    if (LooksLikeCondition(_i, out ignored)) return true;
                    if (_i >= _lines.Count || _lines[_i].Label != null || _lines[_i].Op == "jmp")
                    { Refuse("expected a condition", _i); return false; }
                    if (!Step(output)) return false;
                }
                return false;
            }

            private Val DecodeCondition(out string falseTarget)
            {
                falseTarget = null;
                var scratch = new List<string>();
                var terms = new List<Val>();
                string firstTarget = null;

                while (Refusal == null)
                {
                    // `||`: jump-if-false to .Lorfail, jmp .Lor, .Lorfail:, <rhs>, .Lor:
                    string ahead = PeekNextConditionTarget(_i);
                    if (ahead != null && ahead.StartsWith(".Lorfail", StringComparison.Ordinal))
                    {
                        if (!EvaluateUntilCondition(scratch)) return Val.Atom("0");
                        string fail;
                        Val lhs = ConsumeCondition(out fail);
                        if (_i >= _lines.Count || _lines[_i].Op != "jmp")
                        { Refuse("malformed '||' shape", _i); return lhs; }
                        string keep = _lines[_i].Arg(0);
                        _i++;
                        if (_i >= _lines.Count || _lines[_i].Label != fail)
                        { Refuse("malformed '||' shape", _i); return lhs; }
                        _i++;
                        string innerTarget;
                        Val rhs = DecodeCondition(out innerTarget);
                        if (Refusal != null) return lhs;
                        if (_i < _lines.Count && _lines[_i].Label == keep) _i++;
                        firstTarget = innerTarget;
                        terms.Add(Binary(lhs, "||", rhs, PrecLogOr));
                        break;
                    }

                    if (!EvaluateUntilCondition(scratch)) return Val.Atom("0");
                    string t;
                    Val term = ConsumeCondition(out t);
                    if (firstTarget == null) firstTarget = t;
                    else if (t != firstTarget)
                    { Refuse("conditions of one test jump to different targets", _i); return term; }
                    terms.Add(term);

                    // Another jump-if-false to the same place is the next `&&` term; anything else
                    // ends the condition.
                    if (PeekNextConditionTarget(_i) != firstTarget) break;
                }
                if (scratch.Count > 0)
                { Refuse("a condition contained a statement, which this reader cannot place", _i); }

                falseTarget = firstTarget;
                if (terms.Count == 0) { Refuse("expected a condition", _i); return Val.Atom("0"); }
                Val all = terms[0];
                for (int k = 1; k < terms.Count; k++) all = Binary(all, "&&", terms[k], PrecLogAnd);
                return all;
            }

            // ---- structured statements ---------------------------------------------------------

            private void DecodeIf(List<string> output, int stop)
            {
                string falseTarget;
                Val cond = DecodeCondition(out falseTarget);
                if (Refusal != null) return;

                List<string> thenBody = DecodeStatements(stop);
                if (Refusal != null) return;

                // With an else, the then-branch ends `jmp .Lendif`; without one it just falls into
                // the .Lelse label, which is then the end of the statement.
                string endLabel = null;
                if (_i < _lines.Count && _lines[_i].Op == "jmp"
                    && _lines[_i].Arg(0).StartsWith(".Lendif", StringComparison.Ordinal))
                {
                    endLabel = _lines[_i].Arg(0);
                    _i++;
                }
                if (_i >= _lines.Count || _lines[_i].Label != falseTarget)
                {
                    Refuse("the false branch of an 'if' does not land where its test jumped", _i);
                    return;
                }
                _i++;

                output.Add("if (" + cond + ")");
                output.Add("{");
                foreach (string s in thenBody) output.Add("    " + s);
                output.Add("}");

                if (endLabel == null) return;

                List<string> elseBody = DecodeStatements(stop);
                if (Refusal != null) return;
                if (_i < _lines.Count && _lines[_i].Label == endLabel) _i++;
                output.Add("else");
                output.Add("{");
                foreach (string s in elseBody) output.Add("    " + s);
                output.Add("}");
            }

            private void DecodeWhile(List<string> output, int stop)
            {
                string top = _lines[_i].Label;
                string end = Companion(top, ".Lwhile", ".Lendwhile");
                _i++;

                string falseTarget;
                Val cond = DecodeCondition(out falseTarget);
                if (Refusal != null) return;
                // The C++ emitter numbers a loop's labels as one group; the Python emitter numbers
                // each label on its own, so there the end is whichever .Lendwhile the test names.
                if (_python && falseTarget != null && falseTarget.StartsWith(".Lendwhile", StringComparison.Ordinal))
                    end = falseTarget;
                if (falseTarget != end) { Refuse("a while-loop test jumps somewhere other than its end (" + falseTarget + " vs " + end + ")", _i); return; }

                _loopEnds.Add(end);
                _loopContinues.Add(top);
                _loopBackEdges.Add(top);
                List<string> body = DecodeStatements(stop);
                _loopEnds.RemoveAt(_loopEnds.Count - 1);
                _loopContinues.RemoveAt(_loopContinues.Count - 1);
                _loopBackEdges.RemoveAt(_loopBackEdges.Count - 1);
                if (Refusal != null) return;

                if (_i < _lines.Count && _lines[_i].Op == "jmp" && _lines[_i].Arg(0) == top) _i++;
                if (_i < _lines.Count && _lines[_i].Label == end) _i++;

                output.Add("while (" + cond + ")");
                output.Add("{");
                foreach (string s in body) output.Add("    " + s);
                output.Add("}");
            }

            private void DecodeDo(List<string> output, int stop)
            {
                string top = _lines[_i].Label;
                string test = Companion(top, ".Ldo", ".Ldotest");
                string end = Companion(top, ".Ldo", ".Lenddo");
                _i++;

                _loopEnds.Add(end);
                _loopContinues.Add(test);
                _loopBackEdges.Add(top);
                List<string> body = DecodeStatements(stop);
                _loopEnds.RemoveAt(_loopEnds.Count - 1);
                _loopContinues.RemoveAt(_loopContinues.Count - 1);
                _loopBackEdges.RemoveAt(_loopBackEdges.Count - 1);
                if (Refusal != null) return;

                if (_i >= _lines.Count || _lines[_i].Label != test)
                { Refuse("a do-while body does not end at its test", _i); return; }
                _i++;

                string falseTarget;
                Val cond = DecodeCondition(out falseTarget);
                if (Refusal != null) return;
                if (falseTarget != end) { Refuse("a do-while test jumps somewhere other than its end", _i); return; }
                if (_i < _lines.Count && _lines[_i].Op == "jmp" && _lines[_i].Arg(0) == top) _i++;
                if (_i < _lines.Count && _lines[_i].Label == end) _i++;

                output.Add("do");
                output.Add("{");
                foreach (string s in body) output.Add("    " + s);
                output.Add("} while (" + cond + ");");
            }

            /// <summary>
            /// A for-loop is recovered as `while`, not `for`. The initialiser was emitted before
            /// the loop label and is already a statement in the enclosing block, and the increment
            /// sits after the continue label; re-assembling them into a `for` header would be a
            /// guess about which statements the author had written there.
            /// </summary>
            private void DecodeFor(List<string> output, int stop)
            {
                string top = _lines[_i].Label;
                string cont = Companion(top, ".Lfor", ".Lforcont");
                string end = Companion(top, ".Lfor", ".Lendfor");
                _i++;

                string falseTarget = null;
                Val cond = Val.Atom("true");
                if (PeekNextConditionTarget(_i) == end)
                {
                    cond = DecodeCondition(out falseTarget);
                    if (Refusal != null) return;
                    if (falseTarget != end) { Refuse("a for-loop test jumps somewhere other than its end", _i); return; }
                }

                _loopEnds.Add(end);
                _loopContinues.Add(cont);
                _loopBackEdges.Add(top);
                List<string> body = DecodeStatements(stop);
                if (Refusal != null) { _loopEnds.RemoveAt(_loopEnds.Count - 1); _loopContinues.RemoveAt(_loopContinues.Count - 1); _loopBackEdges.RemoveAt(_loopBackEdges.Count - 1); return; }

                if (_i < _lines.Count && _lines[_i].Label == cont) _i++;
                List<string> increment = DecodeStatements(stop);
                _loopEnds.RemoveAt(_loopEnds.Count - 1);
                _loopContinues.RemoveAt(_loopContinues.Count - 1);
                _loopBackEdges.RemoveAt(_loopBackEdges.Count - 1);
                if (Refusal != null) return;

                if (_i < _lines.Count && _lines[_i].Op == "jmp" && _lines[_i].Arg(0) == top) _i++;
                if (_i < _lines.Count && _lines[_i].Label == end) _i++;

                output.Add("while (" + cond + ")");
                output.Add("{");
                foreach (string s in body) output.Add("    " + s);
                foreach (string s in increment) output.Add("    " + s);
                output.Add("}");
            }

            // ---- straight-line instructions ------------------------------------------------------

            /// <summary>Folds one non-structural instruction into the symbolic state.</summary>
            private bool Step(List<string> output)
            {
                AsmLine line = _lines[_i];
                int offset;

                // A spill of the left operand. These occur inside expressions — including inside a
                // condition, which is why Step has to know about them and not only DecodeStatements.
                if (IsStoreToSlot(line, out offset))
                {
                    SlotInfo target = SlotAt(offset);
                    if (target == null) { Refuse("store to an unmapped frame slot", _i); return false; }
                    if (target.Kind == "local" || target.Kind == "param")
                    {
                        Refuse("an assignment appears inside an expression, which this reader "
                               + "cannot place back into C++", _i);
                        return false;
                    }
                    _slots[offset] = _acc;
                    _i++;
                    return true;
                }

                if (IsLoadFromSlot(line, out offset))
                {
                    SlotInfo info = SlotAt(offset);
                    if (info == null) { Refuse("load from an unmapped frame slot", _i); return false; }
                    Val known;
                    _acc = (info.Kind == "local" || info.Kind == "param")
                        ? Val.Atom(info.Name)
                        : (_slots.TryGetValue(offset, out known) ? known : Val.Atom(info.Name));
                    _i++;
                    return true;
                }

                // Staging a call argument into its ABI register. DecodeCall walks these backwards
                // from the `call`, so at this point they are simply passed over.
                if (ArgumentIndex(line) >= 0 && line.Args.Length == 2
                    && (TryFrameOffset(line.Arg(1), out offset) || line.Arg(1) == "eax"))
                {
                    if (line.Arg(0) == "ecx" && line.Arg(1) == "eax") { _second = _acc; _i++; return true; }
                    if (TryFrameOffset(line.Arg(1), out offset)) { _i++; return true; }
                }

                switch (line.Op)
                {
                    case "mov":
                        if (line.Arg(0) == "eax" && IsNumber(line.Arg(1)))
                        { _acc = Val.Atom(Decimal(line.Arg(1))); _i++; return true; }
                        if (line.Arg(0) == "ecx" && line.Arg(1) == "eax") { _second = _acc; _i++; return true; }
                        if (line.Arg(0) == "rax" && IsNumber(line.Arg(1)))
                        {
                            // A double literal: `mov rax, <bits>` then `movq xmm0, rax`.
                            long bits = ParseNumber(line.Arg(1));
                            if (_i + 1 < _lines.Count && _lines[_i + 1].Is("movq", "xmm0", "rax"))
                            {
                                _acc = Val.Atom(Literal(BitConverter.Int64BitsToDouble(bits)));
                                _i += 2;
                                return true;
                            }
                            if (_i + 1 < _lines.Count && _lines[_i + 1].Is("movq", "xmm1", "rax"))
                            {
                                _second = Val.Atom(Literal(BitConverter.Int64BitsToDouble(bits)));
                                _i += 2;
                                return true;
                            }
                        }
                        break;

                    case "movapd":
                        if (line.Is("movapd", "xmm1", "xmm0")) { _second = _acc; _i++; return true; }
                        break;

                    case "xorpd":
                        if (line.Is("xorpd", "xmm0", "xmm0")) { _acc = Val.Atom("0.0"); _i++; return true; }
                        if (line.Is("xorpd", "xmm0", "xmm1")) { _acc = new Val("-" + _acc.In(PrecUnary), PrecUnary); _i++; return true; }
                        break;

                    case "cvtsi2sd": _acc = new Val("(double)" + _acc.In(PrecUnary), PrecUnary); _i++; return true;
                    case "cvttsd2si":
                        _acc = new Val((_python ? "(long long)" : "(int)") + _acc.In(PrecUnary), PrecUnary);
                        _i++;
                        return true;

                    case "add": _acc = Binary(_acc, "+", Operand(line), PrecAdd); _i++; return true;
                    case "sub": _acc = Binary(_acc, "-", Operand(line), PrecAdd); _i++; return true;
                    case "imul": _acc = Binary(_acc, "*", _second, PrecMul); _i++; return true;
                    case "and": _acc = Binary(_acc, "&", _second, PrecBitAnd); _i++; return true;
                    case "or": _acc = Binary(_acc, "|", _second, PrecBitOr); _i++; return true;
                    case "xor":
                        if (line.Is("xor", "eax", "eax")) { _acc = Val.Atom("0"); _i++; return true; }
                        _acc = Binary(_acc, "^", _second, PrecBitXor); _i++; return true;
                    case "sal": _acc = Binary(_acc, "<<", _second, PrecShift); _i++; return true;
                    case "sar": _acc = Binary(_acc, ">>", _second, PrecShift); _i++; return true;
                    case "neg": _acc = new Val("-" + _acc.In(PrecUnary), PrecUnary); _i++; return true;
                    case "not": _acc = new Val("~" + _acc.In(PrecUnary), PrecUnary); _i++; return true;

                    case "addsd": _acc = Binary(_acc, "+", _second, PrecAdd); _i++; return true;
                    case "subsd": _acc = Binary(_acc, "-", _second, PrecAdd); _i++; return true;
                    case "mulsd": _acc = Binary(_acc, "*", _second, PrecMul); _i++; return true;
                    case "divsd": _acc = Binary(_acc, "/", _second, PrecMul); _i++; return true;

                    case "cdq":
                        // cdq + idiv is division; a following `mov eax, edx` makes it modulo.
                        if (_i + 1 < _lines.Count && _lines[_i + 1].Is("idiv", "ecx"))
                        {
                            bool modulo = _i + 2 < _lines.Count && _lines[_i + 2].Is("mov", "eax", "edx");
                            _acc = Binary(_acc, modulo ? "%" : "/", _second, PrecMul);
                            _i += modulo ? 3 : 2;
                            return true;
                        }
                        break;

                    case "cmp":
                        if (line.Is("cmp", "eax", "ecx")) return DecodeIntCompare(output);
                        break;

                    case "comisd":
                        return DecodeDoubleCompare(output);

                    case "call":
                        return DecodeCall(output);

                    case "movsxd":
                        _i++;                         // widening a staged stack argument
                        return true;
                }

                Refuse("instruction '" + line + "' is not a shape the HelixPython emitter produces", _i);
                return false;
            }

            /// <summary>`add eax, 1` style immediates read as literals; otherwise the second register.</summary>
            private Val Operand(AsmLine line)
            {
                return IsNumber(line.Arg(1)) ? Val.Atom(Decimal(line.Arg(1))) : _second;
            }

            private bool DecodeIntCompare(List<string> output)
            {
                if (_i + 2 >= _lines.Count) { Refuse("truncated comparison", _i); return false; }
                string op = ConditionOperator(_lines[_i + 1].Op);
                if (op == null) { Refuse("unrecognised comparison '" + _lines[_i + 1].Op + "'", _i + 1); return false; }
                if (!_lines[_i + 2].Is("movzx", "eax", "al")) { Refuse("comparison is not widened", _i + 2); return false; }
                _acc = Binary(_acc, op, _second, op == "==" || op == "!=" ? PrecEq : PrecRel);
                _i += 3;
                return true;
            }

            private bool DecodeDoubleCompare(List<string> output)
            {
                AsmLine cmp = _lines[_i];
                bool swapped = cmp.Arg(0) == "xmm1";        // `a < b` is emitted as `b > a`
                if (_i + 1 >= _lines.Count) { Refuse("truncated comparison", _i); return false; }
                string setcc = _lines[_i + 1].Op;

                // == and != fold the parity flag in with two more instructions.
                if (_i + 3 < _lines.Count
                    && (_lines[_i + 2].Op == "setnp" || _lines[_i + 2].Op == "setp"))
                {
                    string op = setcc == "sete" ? "==" : "!=";
                    if (!_lines[_i + 4 - 1].Is("and", "al", "cl") && !_lines[_i + 3].Is("or", "al", "cl"))
                    { Refuse("unrecognised NaN-aware comparison", _i); return false; }
                    _acc = Binary(_acc, op, _second, PrecEq);
                    _i += 4;
                    if (_i < _lines.Count && _lines[_i].Is("movzx", "eax", "al")) _i++;
                    return true;
                }

                string relational;
                switch (setcc)
                {
                    case "seta": relational = swapped ? "<" : ">"; break;
                    case "setae": relational = swapped ? "<=" : ">="; break;
                    default: Refuse("unrecognised floating comparison '" + setcc + "'", _i + 1); return false;
                }
                _acc = Binary(_acc, relational, _second, PrecRel);
                _i += 2;
                if (_i < _lines.Count && _lines[_i].Is("movzx", "eax", "al")) _i++;
                return true;
            }

            private static string ConditionOperator(string setcc)
            {
                switch (setcc)
                {
                    case "setl": return "<";
                    case "setle": return "<=";
                    case "setg": return ">";
                    case "setge": return ">=";
                    case "sete": return "==";
                    case "setne": return "!=";
                    default: return null;
                }
            }

            /// <summary>
            /// A call: its arguments were staged into temporaries, then loaded into the ABI
            /// registers immediately before the `call`. Those loads are walked backwards from the
            /// call so the recovered argument list is in source order.
            /// </summary>
            private bool DecodeCall(List<string> output)
            {
                string name = _lines[_i].Arg(0);
                FunctionInfo callee;
                if (!_known.TryGetValue(name, out callee))
                { Refuse("call to unknown function '" + name + "'", _i); return false; }

                int arity = callee.Parameters.Count;
                var args = new Val[arity];
                int scan = _i - 1;
                int found = 0;
                while (found < arity && scan >= _fn.BodyStart)
                {
                    AsmLine l = _lines[scan];
                    int index = ArgumentIndex(l);
                    int offset;
                    if (index >= 0 && index < arity && args[index] == null
                        && TryFrameOffset(l.Arg(1), out offset))
                    {
                        Val staged;
                        SlotInfo info = SlotAt(offset);
                        args[index] = _slots.TryGetValue(offset, out staged) ? staged
                                    : info != null ? Val.Atom(info.Name) : null;
                        if (args[index] == null) { Refuse("a call argument came from an unmapped slot", scan); return false; }
                        found++;
                        scan--;
                        continue;
                    }
                    if (l.Op == "mov" && l.Arg(0).StartsWith("[rsp+", StringComparison.Ordinal)) { scan--; continue; }
                    if (l.Op == "movsd" && l.Arg(0).StartsWith("[rsp+", StringComparison.Ordinal)) { scan--; continue; }
                    if (l.Op == "movsxd") { scan--; continue; }
                    break;
                }
                if (found != arity)
                { Refuse("could not recover all " + arity + " arguments of the call to '" + name + "'", _i); return false; }

                var parts = new List<string>();
                foreach (Val v in args) parts.Add(v.ToString());
                _acc = Val.Atom(name + "(" + string.Join(", ", parts) + ")");
                _i++;
                return true;
            }

            /// <summary>Which argument slot a register load targets, or -1.</summary>
            private static int ArgumentIndex(AsmLine line)
            {
                if (line.Op != "mov" && line.Op != "movsd") return -1;
                switch (line.Arg(0))
                {
                    case "ecx": case "xmm0": return 0;
                    case "edx": case "xmm1": return 1;
                    case "r8d": case "xmm2": return 2;
                    case "r9d": case "xmm3": return 3;
                    default: return -1;
                }
            }

            private static bool IsNumber(string s)
            {
                if (string.IsNullOrEmpty(s)) return false;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true;
                long ignored;
                return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ignored);
            }

            private static long ParseNumber(string s)
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return unchecked((long)Convert.ToUInt64(s.Substring(2), 16));
                return long.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }

            private static string Decimal(string s)
            {
                return ParseNumber(s).ToString(CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// A double spelled so it reads back as the same bits. "R" round-trips, and a value
            /// that would otherwise look like an integer keeps a decimal point so C++ does not
            /// re-type it as int.
            /// </summary>
            private static string Literal(double value)
            {
                string text = value.ToString("R", CultureInfo.InvariantCulture);
                if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0
                    && text.IndexOf("Infinity", StringComparison.Ordinal) < 0
                    && text.IndexOf("NaN", StringComparison.Ordinal) < 0)
                    text += ".0";
                return text;
            }
        }
    }
}
