using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.PythonAsmCompiler
{
    /// <summary>
    /// Reads NASM (win64) produced by <see cref="PythonToNasm"/> back into Python.
    ///
    /// Forward compilation is lossy. A <c>while</c> and an <c>if</c> around a backward jump
    /// compile to the same instructions, a local and a compiler temporary are both just an offset
    /// from RBP, and a promotion the author never wrote is indistinguishable from one they did.
    /// Recovering structure from arbitrary machine code is what decompilers spend decades on, so
    /// this one does not attempt it: it recognises the exact shapes the emitter produces, using
    /// the <c>; helix.</c> block the emitter writes alongside them — the same debug information a
    /// compiler puts in <c>.debug_info</c> under <c>-g</c> — and refuses anything else.
    ///
    /// The contract is therefore a ROUND TRIP, not general decompilation: for source the forward
    /// path accepts, <c>python -&gt; asm -&gt; python'</c> computes what the original computed.
    /// </summary>
    public static class NasmToPython
    {
        public static DecompileResult Decompile(string assembly)
        {
            if (string.IsNullOrWhiteSpace(assembly)) return DecompileResult.Refuse("empty assembly");

            List<AsmLine> lines;
            List<FunctionInfo> functions;
            string reason = Parse(assembly, out lines, out functions);
            if (reason != null) return DecompileResult.Refuse(reason);
            if (functions.Count == 0)
                return DecompileResult.Refuse(
                    "no '; helix.function' blocks found; this decompiler reads assembly produced by "
                    + "HelixPython, which carries a symbol map. Arbitrary NASM has no record of "
                    + "which offsets were variables or which jumps were loops.");

            var arity = new Dictionary<string, FunctionInfo>(StringComparer.Ordinal);
            foreach (FunctionInfo f in functions) arity[f.Name] = f;

            var text = new StringBuilder();
            text.AppendLine("# Recovered from NASM by HelixPython.");
            foreach (FunctionInfo f in functions)
            {
                var decoder = new Decoder(lines, f, arity);
                List<string> body = decoder.Run();
                if (body == null) return DecompileResult.Refuse(decoder.Refusal, decoder.RefusalLine);

                text.AppendLine();
                var parameters = new List<string>();
                foreach (SlotInfo p in f.Parameters) parameters.Add(p.Name + ": " + p.Type);
                text.AppendLine("def " + f.Name + "(" + string.Join(", ", parameters) + ") -> " + f.Returns + ":");
                if (body.Count == 0) body.Add("pass");
                foreach (string line in body) text.AppendLine("    " + line);
            }
            return DecompileResult.Ok(text.ToString());
        }

        // ---- assembly model ----------------------------------------------

        private sealed class AsmLine
        {
            public int Number;                 // 1-based, for refusals
            public string Label;               // non-null when the line is a label
            public string Op;                  // mnemonic, lower case
            public string[] Args = new string[0];
            public string Comment;

            public string Arg(int i) { return i < Args.Length ? Args[i] : string.Empty; }
            public bool Is(string op, params string[] args)
            {
                if (Op != op || Args.Length != args.Length) return false;
                for (int i = 0; i < args.Length; i++) if (Args[i] != args[i]) return false;
                return true;
            }
            public override string ToString() { return Label != null ? Label + ":" : Op + " " + string.Join(", ", Args); }
        }

        private sealed class SlotInfo
        {
            public int Offset;
            public string Kind;        // param | local | temp | saved | stop | step
            public string Name;
            public string Type;        // int | float | any
        }

        private sealed class FunctionInfo
        {
            public string Name;
            public string Returns;
            public readonly List<SlotInfo> Parameters = new List<SlotInfo>();
            public readonly Dictionary<int, SlotInfo> Slots = new Dictionary<int, SlotInfo>();
            public int BodyStart;      // first instruction after the prologue
            public int BodyEnd;        // exclusive; the implicit-return instruction
            // NASM scopes a local label to the previous global one, so `.Lwhile1` legitimately
            // appears once per function. Label lookup has to be scoped the same way.
            public int RangeStart;
            public int RangeEnd;
        }

        /// <summary>Splits the text into lines, reads the metadata, and locates each body.</summary>
        private static string Parse(string assembly, out List<AsmLine> lines, out List<FunctionInfo> functions)
        {
            lines = new List<AsmLine>();
            functions = new List<FunctionInfo>();
            var raw = assembly.Replace("\r\n", "\n").Split('\n');

            FunctionInfo pending = null;
            var pendingByName = new Dictionary<string, FunctionInfo>(StringComparer.Ordinal);
            var order = new List<FunctionInfo>();

            for (int n = 0; n < raw.Length; n++)
            {
                string line = raw[n];
                int hash = line.IndexOf(';');
                string comment = hash >= 0 ? line.Substring(hash + 1).Trim() : null;
                string code = (hash >= 0 ? line.Substring(0, hash) : line).Trim();

                if (comment != null && comment.StartsWith("helix.", StringComparison.Ordinal))
                {
                    string error = ReadMetadata(comment, ref pending, pendingByName, order);
                    if (error != null) return error + " (line " + (n + 1) + ")";
                }
                if (code.Length == 0) continue;

                var asm = new AsmLine { Number = n + 1, Comment = comment };
                if (code.EndsWith(":", StringComparison.Ordinal) && code.IndexOf(' ') < 0)
                {
                    asm.Label = code.Substring(0, code.Length - 1);
                }
                else
                {
                    int space = code.IndexOf(' ');
                    if (space < 0) { asm.Op = code.ToLowerInvariant(); }
                    else
                    {
                        asm.Op = code.Substring(0, space).ToLowerInvariant();
                        asm.Args = code.Substring(space + 1)
                                       .Split(',')
                                       .Select(a => Normalize(a))
                                       .ToArray();
                    }
                    if (asm.Op == "global" || asm.Op == "default" || asm.Op == "section"
                        || asm.Op == "extern") continue;
                }
                lines.Add(asm);
            }

            foreach (FunctionInfo f in order)
            {
                string error = LocateBody(lines, f);
                if (error != null) return error;
                functions.Add(f);
            }
            return null;
        }

        /// <summary>Collapses runs of whitespace so operand text compares reliably.</summary>
        private static string Normalize(string operand)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in operand.Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string ReadMetadata(string comment, ref FunctionInfo pending,
                                           Dictionary<string, FunctionInfo> byName, List<FunctionInfo> order)
        {
            string[] parts = comment.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "helix.function")
            {
                // helix.function NAME -> TYPE
                if (parts.Length < 4) return "malformed '; helix.function' directive";
                pending = new FunctionInfo { Name = parts[1], Returns = parts[3] };
                byName[pending.Name] = pending;
                order.Add(pending);
                return null;
            }
            if (parts[0] == "helix.slot")
            {
                // helix.slot OFFSET KIND NAME TYPE
                if (pending == null) return "'; helix.slot' outside a function block";
                if (parts.Length < 5) return "malformed '; helix.slot' directive";
                int offset;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
                    return "'; helix.slot' has a non-numeric offset";
                var slot = new SlotInfo { Offset = offset, Kind = parts[2], Name = parts[3], Type = parts[4] };
                pending.Slots[offset] = slot;
                if (slot.Kind == "param") pending.Parameters.Add(slot);
                return null;
            }
            return null;      // unknown helix directives are ignored, not fatal
        }

        /// <summary>Finds the instruction range that is the function body, skipping its frame setup.</summary>
        private static string LocateBody(List<AsmLine> lines, FunctionInfo f)
        {
            int entry = lines.FindIndex(l => l.Label == f.Name);
            if (entry < 0) return "no label found for function '" + f.Name + "'";
            int epilogue = lines.FindIndex(entry, l => l.Label == ".Lepi_" + f.Name);
            if (epilogue < 0) return "no epilogue label found for function '" + f.Name + "'";

            int i = entry + 1;
            if (!lines[i].Is("push", "rbp")) return "unexpected prologue in '" + f.Name + "'";
            i++;
            if (!lines[i].Is("mov", "rbp", "rsp")) return "unexpected prologue in '" + f.Name + "'";
            i++;
            if (lines[i].Op == "sub" && lines[i].Arg(0) == "rsp") i++;
            if (lines[i].Op == "mov" && lines[i].Arg(1) == "rbx") i++;      // saved RBX
            // Register-homed parameters take one instruction each; stack ones take two.
            for (int p = 0; p < f.Parameters.Count; p++) i += (p < 4) ? 1 : 2;

            // The instruction immediately before the epilogue label is the implicit `return None`.
            f.RangeStart = entry;
            f.RangeEnd = epilogue;
            f.BodyStart = i;
            f.BodyEnd = epilogue - 1;
            if (f.BodyEnd < f.BodyStart) f.BodyEnd = f.BodyStart;
            return null;
        }

        // ---- expression values -------------------------------------------

        private const int PrecOr = 1, PrecAnd = 2, PrecNot = 3, PrecCmp = 4, PrecBitOr = 5,
                          PrecBitXor = 6, PrecBitAnd = 7, PrecShift = 8, PrecAdd = 9,
                          PrecMul = 10, PrecUnary = 11, PrecAtom = 12;

        private sealed class Val
        {
            public readonly string Text;
            public readonly int Prec;
            public Val(string text, int prec) { Text = text; Prec = prec; }
            public static Val Atom(string text) { return new Val(text, PrecAtom); }
            /// <summary>The text, parenthesised when the surrounding operator binds tighter.</summary>
            public string In(int need) { return Prec < need ? "(" + Text + ")" : Text; }
            public override string ToString() { return Text; }
        }

        private static Val Binary(Val left, string op, Val right, int prec)
        {
            return new Val(left.In(prec) + " " + op + " " + right.In(prec + 1), prec);
        }

        // ---- the decoder --------------------------------------------------

        /// <summary>
        /// A symbolic interpreter over one function body. Instead of computing values it builds
        /// the expression that would have produced them, so a run of stack-machine instructions
        /// collapses back into the expression the author wrote.
        /// </summary>
        private sealed class Decoder
        {
            private readonly List<AsmLine> _lines;
            private readonly FunctionInfo _fn;
            private readonly IReadOnlyDictionary<string, FunctionInfo> _module;
            private readonly Dictionary<int, Val> _slots = new Dictionary<int, Val>();
            private readonly Dictionary<string, List<Val>> _pendingBool = new Dictionary<string, List<Val>>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _pendingBoolOp = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _labels = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<string> _loopEnds = new List<string>();
            private readonly List<string> _loopContinues = new List<string>();

            private Val _acc = Val.Atom("0");
            private Val _second;
            private int _i;

            public string Refusal { get; private set; }
            public int RefusalLine { get; private set; }

            public Decoder(List<AsmLine> lines, FunctionInfo fn,
                           IReadOnlyDictionary<string, FunctionInfo> module)
            {
                _lines = lines; _fn = fn; _module = module;
                for (int k = fn.RangeStart; k <= fn.RangeEnd && k < lines.Count; k++)
                    if (lines[k].Label != null && !_labels.ContainsKey(lines[k].Label))
                        _labels[lines[k].Label] = k;
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

            // ---- statements ----------------------------------------------

            private List<string> DecodeStatements(int stop)
            {
                var output = new List<string>();
                var pendingChain = new List<string>();      // targets of `a = b = value`
                Val chainValue = null;

                while (_i < stop && Refusal == null)
                {
                    AsmLine line = _lines[_i];

                    if (line.Label != null)
                    {
                        FlushChain(output, pendingChain, ref chainValue);
                        if (ResolveBoolLabel(line.Label)) { _i++; continue; }
                        if (line.Label.StartsWith(".Lwhile", StringComparison.Ordinal)) { DecodeWhile(output); continue; }
                        if (line.Label.StartsWith(".Lfor", StringComparison.Ordinal)
                            && !line.Label.StartsWith(".Lforstep", StringComparison.Ordinal))
                        { DecodeFor(output); continue; }
                        Refuse("unexpected label '" + line.Label + "'; this is not a shape the "
                               + "HelixPython emitter produces", _i);
                        return output;
                    }

                    // A store into a named local ends a statement.
                    int slot;
                    if (IsStoreToSlot(line, out slot))
                    {
                        SlotInfo info = SlotAt(slot);
                        if (info == null) { Refuse("store to an unmapped frame slot", _i); return output; }
                        if (info.Kind == "local" || info.Kind == "param")
                        {
                            // `a = b = value` stores the same accumulator twice with nothing in
                            // between; recovering it as two statements would evaluate a call twice.
                            if (chainValue == null) chainValue = _acc;
                            pendingChain.Add(info.Name);
                            _i++;
                            if (_i < stop && IsStoreToSlot(_lines[_i], out slot))
                            {
                                SlotInfo next = SlotAt(slot);
                                if (next != null && (next.Kind == "local" || next.Kind == "param")) continue;
                            }
                            FlushChain(output, pendingChain, ref chainValue);
                            continue;
                        }
                        _slots[slot] = _acc;
                        _i++;
                        continue;
                    }

                    // A run of (load temp, store local) pairs is an unpacking assignment. Decoding
                    // it as separate statements would break `a, b = b, a` into two copies of b.
                    int pairs = CountUnpackPairs(stop);
                    if (pairs >= 2) { DecodeUnpack(output, pairs); continue; }

                    if (line.Op == "cmp" && line.Arg(0) == "rax" && line.Arg(1) == "0")
                    {
                        AsmLine next = _i + 1 < _lines.Count ? _lines[_i + 1] : null;
                        if (next != null && next.Op == "je"
                            && (next.Arg(0).StartsWith(".Lelse", StringComparison.Ordinal)
                                || next.Arg(0).StartsWith(".Lendif", StringComparison.Ordinal)))
                        { DecodeIf(output); continue; }
                    }

                    if (line.Op == "jmp")
                    {
                        string target = line.Arg(0);
                        if (target == ".Lepi_" + _fn.Name) { output.Add("return " + _acc); _i++; continue; }
                        if (_loopEnds.Count > 0 && target == _loopEnds[_loopEnds.Count - 1])
                        { output.Add("break"); _i++; continue; }
                        if (_loopContinues.Count > 0 && target == _loopContinues[_loopContinues.Count - 1])
                        { output.Add("continue"); _i++; continue; }
                        Refuse("unexpected jump to '" + target + "'", _i);
                        return output;
                    }

                    if (!Step(output)) return output;
                }
                FlushChain(output, pendingChain, ref chainValue);
                return output;
            }

            private void FlushChain(List<string> output, List<string> targets, ref Val value)
            {
                if (targets.Count == 0) return;
                output.Add(string.Join(" = ", targets) + " = " + value);
                targets.Clear();
                value = null;
            }

            private SlotInfo SlotAt(int offset)
            {
                SlotInfo info;
                return _fn.Slots.TryGetValue(offset, out info) ? info : null;
            }

            private static bool IsStoreToSlot(AsmLine line, out int offset)
            {
                offset = 0;
                if (line.Label != null) return false;
                bool isMov = line.Op == "mov" && line.Arg(1) == "rax";
                bool isMovsd = line.Op == "movsd" && line.Arg(1) == "xmm0";
                if (!isMov && !isMovsd) return false;
                return TryFrameOffset(line.Arg(0), out offset);
            }

            private static bool IsLoadFromSlot(AsmLine line, out int offset)
            {
                offset = 0;
                if (line.Label != null) return false;
                bool isMov = line.Op == "mov" && line.Arg(0) == "rax";
                bool isMovsd = line.Op == "movsd" && line.Arg(0) == "xmm0";
                if (!isMov && !isMovsd) return false;
                return TryFrameOffset(line.Arg(1), out offset);
            }

            /// <summary>Parses <c>qword [rbp-24]</c> or <c>[rbp-24]</c> into 24.</summary>
            private static bool TryFrameOffset(string operand, out int offset)
            {
                offset = 0;
                int open = operand.IndexOf('[');
                if (open < 0 || !operand.EndsWith("]", StringComparison.Ordinal)) return false;
                string inner = operand.Substring(open + 1, operand.Length - open - 2).Replace(" ", "");
                if (!inner.StartsWith("rbp-", StringComparison.Ordinal)) return false;
                return int.TryParse(inner.Substring(4), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out offset);
            }

            private int CountUnpackPairs(int stop)
            {
                int j = _i, pairs = 0;
                while (j + 1 < stop)
                {
                    int from, to;
                    if (!IsLoadFromSlot(_lines[j], out from)) break;
                    if (!IsStoreToSlot(_lines[j + 1], out to)) break;
                    SlotInfo source = SlotAt(from), target = SlotAt(to);
                    if (source == null || target == null) break;
                    if (source.Kind != "temp") break;
                    if (target.Kind != "local" && target.Kind != "param") break;
                    pairs++; j += 2;
                }
                return pairs;
            }

            private void DecodeUnpack(List<string> output, int pairs)
            {
                var targets = new List<string>();
                var values = new List<string>();
                for (int p = 0; p < pairs; p++)
                {
                    int from, to;
                    IsLoadFromSlot(_lines[_i], out from);
                    IsStoreToSlot(_lines[_i + 1], out to);
                    Val v;
                    values.Add(_slots.TryGetValue(from, out v) ? v.Text : "0");
                    targets.Add(SlotAt(to).Name);
                    _i += 2;
                }
                output.Add(string.Join(", ", targets) + " = " + string.Join(", ", values));
            }

            private void DecodeIf(List<string> output)
            {
                Val test = _acc;
                string target = _lines[_i + 1].Arg(0);
                _i += 2;

                List<string> then, otherwise = null;
                if (target.StartsWith(".Lelse", StringComparison.Ordinal))
                {
                    int elseAt = LabelIndex(target);
                    if (elseAt < 0) return;
                    // The then-block ends with `jmp .Lendif`, which sits immediately before the
                    // else label.
                    AsmLine bridge = _lines[elseAt - 1];
                    if (bridge.Op != "jmp") { Refuse("malformed if/else", elseAt - 1); return; }
                    string endLabel = bridge.Arg(0);
                    int endAt = LabelIndex(endLabel);
                    if (endAt < 0) return;

                    then = DecodeStatements(elseAt - 1);
                    _i = elseAt + 1;
                    otherwise = DecodeStatements(endAt);
                    _i = endAt + 1;
                }
                else
                {
                    int endAt = LabelIndex(target);
                    if (endAt < 0) return;
                    then = DecodeStatements(endAt);
                    _i = endAt + 1;
                }

                output.Add("if " + test + ":");
                Indent(output, then);
                if (otherwise != null && otherwise.Count > 0)
                {
                    output.Add("else:");
                    Indent(output, otherwise);
                }
            }

            private void DecodeWhile(List<string> output)
            {
                string top = _lines[_i].Label;
                _i++;

                // The emitter allocates a loop's labels consecutively, so `.Lwhile7` is always
                // paired with `.Lendwhile8`. Searching for the nearest matching label instead
                // would find a NESTED loop's, which is how a decompiler silently reassociates
                // one loop's body with another's.
                int number = LabelNumber(top, ".Lwhile");
                if (number < 0) { Refuse("loop label '" + top + "' is not numbered", _i); return; }
                string endLabel = ".Lendwhile" + (number + 1);

                int guard = FindGuard(endLabel);
                if (guard < 0) { Refuse("could not find the exit test of loop '" + top + "'", _i); return; }
                DecodeStatements(guard);
                if (Refusal != null) return;
                Val test = _acc;
                int endAt = LabelIndex(endLabel);
                if (endAt < 0) return;
                if (_lines[endAt - 1].Op != "jmp" || _lines[endAt - 1].Arg(0) != top)
                { Refuse("loop '" + top + "' does not end with its back edge", endAt - 1); return; }

                _i = guard + 2;
                _loopEnds.Add(endLabel); _loopContinues.Add(top);
                List<string> body = DecodeStatements(endAt - 1);
                _loopEnds.RemoveAt(_loopEnds.Count - 1);
                _loopContinues.RemoveAt(_loopContinues.Count - 1);
                _i = endAt + 1;

                output.Add("while " + test + ":");
                if (body.Count == 0) body.Add("pass");
                Indent(output, body);
            }

            /// <summary>
            /// The counted loop the emitter desugars <c>for i in range(...)</c> into. Its three
            /// bounds were stored just before the loop label — the target as an ordinary
            /// assignment, the stop and step into slots the metadata marks as such — so the
            /// range() call can be put back together exactly.
            /// </summary>
            private void DecodeFor(List<string> output)
            {
                string top = _lines[_i].Label;
                string suffix = top.Substring(".Lfor".Length);
                SlotInfo stopSlot = _fn.Slots.Values.FirstOrDefault(x => x.Kind == "stop" && x.Name == "$stop" + suffix);
                SlotInfo stepSlot = _fn.Slots.Values.FirstOrDefault(x => x.Kind == "step" && x.Name == "$step" + suffix);
                if (stopSlot == null || stepSlot == null)
                { Refuse("loop '" + top + "' has no range bookkeeping slots", _i); return; }

                if (output.Count == 0) { Refuse("loop '" + top + "' has no initialiser", _i); return; }
                string init = output[output.Count - 1];
                int eq = init.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0) { Refuse("loop '" + top + "' has no initialiser", _i); return; }
                string variable = init.Substring(0, eq);
                string start = init.Substring(eq + 3);
                output.RemoveAt(output.Count - 1);

                Val stopValue, stepValue;
                if (!_slots.TryGetValue(stopSlot.Offset, out stopValue)
                    || !_slots.TryGetValue(stepSlot.Offset, out stepValue))
                { Refuse("loop '" + top + "' has unresolved range bounds", _i); return; }

                // The five labels of one loop are allocated consecutively, so they are derived by
                // number rather than by proximity: the nearest `.Lforstep` after this loop's body
                // belongs to a NESTED loop, and using it would splice the two bodies together.
                int number = LabelNumber(top, ".Lfor");
                if (number < 0) { Refuse("loop label '" + top + "' is not numbered", _i); return; }
                int testAt = LabelIndex(".Lftest" + (number + 4));
                int stepAt = LabelIndex(".Lforstep" + (number + 2));
                int endAt = LabelIndex(".Lendfor" + (number + 1));
                if (testAt < 0 || stepAt < 0 || endAt < 0) return;

                _i = testAt + 1;
                _loopEnds.Add(_lines[endAt].Label); _loopContinues.Add(_lines[stepAt].Label);
                List<string> body = DecodeStatements(stepAt);
                _loopEnds.RemoveAt(_loopEnds.Count - 1);
                _loopContinues.RemoveAt(_loopContinues.Count - 1);
                _i = endAt + 1;

                string range;
                if (stepValue.Text == "1" && start == "0") range = "range(" + stopValue + ")";
                else if (stepValue.Text == "1") range = "range(" + start + ", " + stopValue + ")";
                else range = "range(" + start + ", " + stopValue + ", " + stepValue + ")";

                output.Add("for " + variable + " in " + range + ":");
                if (body.Count == 0) body.Add("pass");
                Indent(output, body);
            }

            private int FindLabelStartingWith(int from, string prefix)
            {
                for (int k = from; k <= _fn.RangeEnd && k < _lines.Count; k++)
                    if (_lines[k].Label != null && _lines[k].Label.StartsWith(prefix, StringComparison.Ordinal))
                        return k;
                return -1;
            }

            /// <summary>Index of the <c>cmp rax, 0</c> whose branch leaves the construct.</summary>
            private int FindGuard(string exitLabel)
            {
                for (int k = _i; k + 1 < _lines.Count && k <= _fn.RangeEnd; k++)
                {
                    AsmLine c = _lines[k];
                    if (c.Label != null) continue;
                    if (c.Op != "cmp" || c.Arg(0) != "rax" || c.Arg(1) != "0") continue;
                    AsmLine j = _lines[k + 1];
                    if (j.Op == "je" && j.Arg(0) == exitLabel) return k;
                }
                return -1;
            }

            /// <summary>The sequence number in a label such as <c>.Lwhile7</c>.</summary>
            private static int LabelNumber(string label, string prefix)
            {
                if (!label.StartsWith(prefix, StringComparison.Ordinal)) return -1;
                int number;
                return int.TryParse(label.Substring(prefix.Length), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out number) ? number : -1;
            }

            private int LabelIndex(string label)
            {
                int index;
                if (_labels.TryGetValue(label, out index)) return index;
                Refuse("jump to an unknown label '" + label + "'", _i);
                return -1;
            }

            private static void Indent(List<string> output, List<string> block)
            {
                foreach (string line in block) output.Add("    " + line);
            }

            // ---- one instruction ------------------------------------------

            /// <summary>Interprets one instruction symbolically. Returns false after refusing.</summary>
            private bool Step(List<string> output)
            {
                AsmLine line = _lines[_i];
                switch (line.Op)
                {
                    case "mov": return StepMov(output);
                    case "movsd": return StepMov(output);
                    case "movq": _i++; return true;             // literal materialisation, already folded
                    case "movapd":
                        if (line.Arg(0) == "xmm1") _second = _acc;
                        _i++; return true;
                    case "cvtsi2sd": _acc = Call1("float", _acc); _i++; return true;
                    case "cvttsd2si": _acc = Call1("int", _acc); _i++; return true;
                    case "xorpd":
                        // `xorpd xmm0, xmm0` is the implicit float zero; `xorpd xmm1, xmm1` starts
                        // a truthiness test, which the test consumer handles.
                        if (line.Arg(0) == "xmm0" && line.Arg(1) == "xmm0") _acc = Val.Atom("0.0");
                        _i++; return true;

                    case "add": _acc = Binary(_acc, "+", _second, PrecAdd); _i++; return true;
                    case "sub":
                        if (line.Arg(0) == "rsp") { _i++; return true; }
                        _acc = Binary(_acc, "-", _second, PrecAdd); _i++; return true;
                    case "imul": _acc = Binary(_acc, "*", _second, PrecMul); _i++; return true;
                    case "and":
                        if (line.Arg(0) == "al") { _i++; return true; }        // part of a float compare
                        _acc = Binary(_acc, "&", _second, PrecBitAnd); _i++; return true;
                    case "or":
                        if (line.Arg(0) == "al") { _i++; return true; }
                        _acc = Binary(_acc, "|", _second, PrecBitOr); _i++; return true;
                    case "xor":
                        if (line.Arg(0) == "rax" && line.Arg(1) == "rax") { _acc = Val.Atom("0"); _i++; return true; }
                        if (line.Arg(0) == "rbx") { _i++; return true; }       // floor-division correction
                        _acc = Binary(_acc, "^", _second, PrecBitXor); _i++; return true;
                    case "shl": _acc = Binary(_acc, "<<", _second, PrecShift); _i++; return true;
                    case "sar": _acc = Binary(_acc, ">>", _second, PrecShift); _i++; return true;
                    case "neg": _acc = new Val("-" + _acc.In(PrecUnary), PrecUnary); _i++; return true;
                    case "not": _acc = new Val("~" + _acc.In(PrecUnary), PrecUnary); _i++; return true;
                    case "btc": _acc = new Val("-" + _acc.In(PrecUnary), PrecUnary); _i++; return true;

                    case "addsd": _acc = Binary(_acc, "+", _second, PrecAdd); _i++; return true;
                    case "subsd": _acc = Binary(_acc, "-", _second, PrecAdd); _i++; return true;
                    case "mulsd": _acc = Binary(_acc, "*", _second, PrecMul); _i++; return true;
                    case "divsd": _acc = Binary(_acc, "/", _second, PrecMul); _i++; return true;

                    case "cqo": return StepFloorDiv();
                    case "cmp": return StepCompare();
                    case "ucomisd": return StepFloatCompare();
                    case "test": _i++; return true;             // only appears inside floor division
                    case "call": return StepCall(output);

                    case "je":
                    case "jne":
                        return StepBoolBranch();

                    case "movzx":
                    case "sete": case "setne": case "setl": case "setle": case "setg": case "setge":
                    case "seta": case "setae": case "setb": case "setp": case "setnp":
                        _i++; return true;                      // folded by the compare handlers

                    default:
                        Refuse("instruction '" + line.Op + "' is outside the shapes HelixPython "
                               + "emits, so its meaning cannot be recovered", _i);
                        return false;
                }
            }

            private static Val Call1(string name, Val argument)
            {
                return Val.Atom(name + "(" + argument + ")");
            }

            private bool StepMov(List<string> output)
            {
                AsmLine line = _lines[_i];
                string destination = line.Arg(0), source = line.Arg(1);

                if (destination == "rax" || destination == "xmm0")
                {
                    int offset;
                    if (TryFrameOffset(source, out offset)) { _acc = ValueOfSlot(offset); _i++; return true; }
                    if (source == "xmm0") { _i++; return true; }
                    if (source == "rcx" || source == "xmm1") { _acc = _second; _i++; return true; }
                    if (source == "rdx") { _i++; return true; }          // floor-division remainder
                    Val literal = Literal(source);
                    if (literal == null) { Refuse("cannot read the literal '" + source + "'", _i); return false; }
                    _acc = literal; _i++; return true;
                }
                if (destination == "rcx" || destination == "xmm1"
                    || destination == "rdx" || destination == "r8" || destination == "r9"
                    || destination == "xmm2" || destination == "xmm3")
                {
                    int offset;
                    if (TryFrameOffset(source, out offset)) { _second = ValueOfSlot(offset); _i++; return true; }
                    if (source == "rax" || source == "xmm0") { _second = _acc; _i++; return true; }
                    Val literal = Literal(source);
                    if (literal != null) { _second = literal; _i++; return true; }
                    _i++; return true;
                }
                if (destination == "rbx" || destination == "rsp" || destination == "rbp")
                { _i++; return true; }
                if (destination.Contains("[rsp")) { _i++; return true; }        // an outgoing argument
                int slot;
                if (IsStoreToSlot(line, out slot)) { _slots[slot] = _acc; _i++; return true; }

                Refuse("cannot follow 'mov " + destination + ", " + source + "'", _i);
                return false;
            }

            private Val ValueOfSlot(int offset)
            {
                SlotInfo info = SlotAt(offset);
                if (info == null) return Val.Atom("0");
                if (info.Kind == "param" || info.Kind == "local") return Val.Atom(info.Name);
                Val v;
                return _slots.TryGetValue(offset, out v) ? v : Val.Atom("0");
            }

            /// <summary>Reads an immediate back as Python source, doubles included.</summary>
            private Val Literal(string text)
            {
                if (text.Length == 0) return null;
                long value;
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("0X", StringComparison.Ordinal))
                {
                    ulong bits;
                    if (!ulong.TryParse(text.Substring(2), NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture, out bits)) return null;
                    // Only a double literal is spelled in hex by the emitter; the comment on that
                    // line carries the decimal it came from, but the bits are authoritative.
                    double d = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                    return Val.Atom(PythonDouble(d));
                }
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return value < 0
                        ? new Val(value.ToString(CultureInfo.InvariantCulture), PrecUnary)
                        : Val.Atom(value.ToString(CultureInfo.InvariantCulture));
                return null;
            }

            /// <summary>
            /// A double as Python source that parses back to the identical bit pattern. NaN and
            /// the infinities have no literal spelling in Python, so they go through float().
            /// </summary>
            private static string PythonDouble(double d)
            {
                if (double.IsNaN(d)) return "float('nan')";
                if (double.IsPositiveInfinity(d)) return "float('inf')";
                if (double.IsNegativeInfinity(d)) return "float('-inf')";
                string text = d.ToString("R", CultureInfo.InvariantCulture);
                if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0)
                    text += ".0";
                return text;
            }

            private bool StepFloorDiv()
            {
                // cqo / idiv rcx / test rdx,rdx / je .Ldivdone / mov rbx,rdx / xor rbx,rcx /
                // jns .Ldivdone / sub rax,1 / add rdx,rcx / .Ldivdone: [mov rax, rdx]
                int end = FindLabelStartingWith(_i, ".Ldivdone");
                if (end < 0) { Refuse("a division has no completion label", _i); return false; }
                bool remainder = end + 1 < _lines.Count
                                 && _lines[end + 1].Is("mov", "rax", "rdx");
                _acc = Binary(_acc, remainder ? "%" : "//", _second, PrecMul);
                _i = end + 1 + (remainder ? 1 : 0);
                return true;
            }

            private bool StepCompare()
            {
                AsmLine line = _lines[_i];
                if (line.Arg(0) == "rax" && line.Arg(1) == "0")
                {
                    // `cmp rax, 0` before `sete` is `not x`; before a jump it is a branch, which
                    // the statement decoder handles.
                    AsmLine next = _i + 1 < _lines.Count ? _lines[_i + 1] : null;
                    if (next != null && next.Op == "sete")
                    {
                        _acc = new Val("not " + _acc.In(PrecNot), PrecNot);
                        _i += 3;                                  // cmp / sete / movzx
                        return true;
                    }
                    if (next != null && (next.Op == "je" || next.Op == "jne")) { _i++; return true; }
                    if (next != null && next.Op == "jl") { _i++; return true; }   // range direction test
                    _i++; return true;
                }
                if (line.Arg(0) == "rax" && line.Arg(1) == "rcx")
                {
                    AsmLine set = _i + 1 < _lines.Count ? _lines[_i + 1] : null;
                    string op = set != null ? IntegerCompareOperator(set.Op) : null;
                    if (op == null) { Refuse("unrecognised comparison", _i); return false; }
                    _acc = Binary(_acc, op, _second, PrecCmp);
                    _i += 3;                                      // cmp / setXX / movzx
                    return true;
                }
                _i++;                                             // the range direction test
                return true;
            }

            private static string IntegerCompareOperator(string set)
            {
                switch (set)
                {
                    case "sete": return "==";
                    case "setne": return "!=";
                    case "setl": return "<";
                    case "setle": return "<=";
                    case "setg": return ">";
                    case "setge": return ">=";
                    default: return null;
                }
            }

            private bool StepFloatCompare()
            {
                AsmLine line = _lines[_i];
                bool swapped = line.Arg(0) == "xmm1";
                AsmLine set = _i + 1 < _lines.Count ? _lines[_i + 1] : null;
                if (set == null) { Refuse("unrecognised float comparison", _i); return false; }

                // A truthiness test compares against a zeroed XMM1 and leaves the value alone.
                if (!swapped && _i >= 1 && _lines[_i - 1].Is("xorpd", "xmm1", "xmm1"))
                { _i += 5; return true; }                         // ucomisd/setne/setp/or/movzx

                string op;
                switch (set.Op)
                {
                    case "seta": op = swapped ? "<" : ">"; _i += 3; break;
                    case "setae": op = swapped ? "<=" : ">="; _i += 3; break;
                    case "sete": op = "=="; _i += 5; break;       // sete/setnp/and/movzx
                    case "setne": op = "!="; _i += 5; break;      // setne/setp/or/movzx
                    default:
                        Refuse("unrecognised float comparison", _i);
                        return false;
                }
                _acc = Binary(_acc, op, _second, PrecCmp);
                return true;
            }

            /// <summary>
            /// A call. The arguments were staged into spill slots and then moved into their
            /// registers immediately before the <c>call</c>, so they are read back off those
            /// moves — the register order is the argument order.
            /// </summary>
            private bool StepCall(List<string> output)
            {
                string name = _lines[_i].Arg(0);
                FunctionInfo callee;
                if (!_module.TryGetValue(name, out callee))
                { Refuse("call to '" + name + "' which this module does not define", _i); return false; }

                int count = callee.Parameters.Count;
                var arguments = new string[count];
                int scan = _i - 1;
                for (int a = Math.Min(count, 4) - 1; a >= 0; a--)
                {
                    int offset;
                    if (scan < 0 || !TryFrameOffset(_lines[scan].Arg(1), out offset))
                    { Refuse("cannot recover argument " + a + " of the call to '" + name + "'", _i); return false; }
                    Val v;
                    arguments[a] = _slots.TryGetValue(offset, out v) ? v.Text : "0";
                    scan--;
                }
                for (int a = 4; a < count; a++)
                {
                    // Stack arguments were written high index first, as `mov rax, [slot]` then
                    // `mov [rsp+N], rax`, so walking further back finds them in ascending order.
                    int offset;
                    if (scan - 1 < 0 || !TryFrameOffset(_lines[scan - 1].Arg(1), out offset))
                    { Refuse("cannot recover stack argument " + a + " of the call to '" + name + "'", _i); return false; }
                    Val v;
                    arguments[a] = _slots.TryGetValue(offset, out v) ? v.Text : "0";
                    scan -= 2;
                }

                _acc = Val.Atom(name + "(" + string.Join(", ", arguments) + ")");
                _i++;

                // A call whose value nothing reads was a statement in the source.
                if (!AccumulatorIsRead()) output.Add(_acc.Text);
                return true;
            }

            private bool AccumulatorIsRead()
            {
                if (_i >= _lines.Count) return false;
                AsmLine next = _lines[_i];
                if (next.Label != null) return false;
                switch (next.Op)
                {
                    case "mov":
                    case "movsd":
                        // A store of RAX/XMM0 reads it; a load into RAX/XMM0 overwrites it.
                        if (next.Arg(1) == "rax" || next.Arg(1) == "xmm0") return true;
                        return !(next.Arg(0) == "rax" || next.Arg(0) == "xmm0");
                    case "jmp":
                        return next.Arg(0) == ".Lepi_" + _fn.Name;
                    case "call":
                        return false;
                    default:
                        return true;
                }
            }

            /// <summary>
            /// <c>je</c>/<c>jne</c> to a <c>.Lboolend</c> label is the short-circuit of
            /// <c>and</c>/<c>or</c>; to a <c>.Lcelse</c> label it is a conditional expression.
            /// </summary>
            private bool StepBoolBranch()
            {
                AsmLine line = _lines[_i];
                string target = line.Arg(0);

                if (target.StartsWith(".Lboolend", StringComparison.Ordinal))
                {
                    List<Val> values;
                    if (!_pendingBool.TryGetValue(target, out values))
                    {
                        values = new List<Val>();
                        _pendingBool[target] = values;
                        _pendingBoolOp[target] = line.Op == "je" ? "and" : "or";
                    }
                    values.Add(_acc);
                    _i++;
                    return true;
                }

                if (target.StartsWith(".Lcelse", StringComparison.Ordinal))
                {
                    Val test = _acc;
                    int elseAt = LabelIndex(target);
                    if (elseAt < 0) return false;
                    AsmLine bridge = _lines[elseAt - 1];
                    if (bridge.Op != "jmp") { Refuse("malformed conditional expression", elseAt - 1); return false; }
                    int endAt = LabelIndex(bridge.Arg(0));
                    if (endAt < 0) return false;

                    _i++;
                    DecodeStatements(elseAt - 1);
                    if (Refusal != null) return false;
                    Val body = _acc;
                    _i = elseAt + 1;
                    DecodeStatements(endAt);
                    if (Refusal != null) return false;
                    Val otherwise = _acc;
                    _i = endAt + 1;

                    _acc = new Val(body.In(PrecOr + 1) + " if " + test.In(PrecOr + 1)
                                   + " else " + otherwise.In(PrecOr), PrecOr);
                    return true;
                }

                Refuse("unexpected branch to '" + target + "'", _i);
                return false;
            }

            /// <summary>Folds a completed short-circuit chain back into one expression.</summary>
            private bool ResolveBoolLabel(string label)
            {
                List<Val> values;
                if (!_pendingBool.TryGetValue(label, out values)) return false;
                _pendingBool.Remove(label);
                string op = _pendingBoolOp[label];
                _pendingBoolOp.Remove(label);

                int prec = op == "and" ? PrecAnd : PrecOr;
                var parts = new List<string>();
                foreach (Val v in values) parts.Add(v.In(prec + 1));
                parts.Add(_acc.In(prec + 1));
                _acc = new Val(string.Join(" " + op + " ", parts), prec);
                return true;
            }
        }
    }
}
