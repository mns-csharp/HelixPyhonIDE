using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>Outcome of converting one module to strict OOP form.</summary>
    public sealed class OopConversionResult
    {
        public OopConversionResult(string text, bool changed)
        {
            Text = text;
            Changed = changed;
            MovedFunctions = new List<string>();
            Notes = new List<string>();
        }

        /// <summary>The full module text after conversion (== the input when nothing changed).</summary>
        public string Text { get; }

        public bool Changed { get; }

        /// <summary>The wrapper class every moved function became a @staticmethod of (null when unchanged).</summary>
        public string ClassName { get; set; }

        public List<string> MovedFunctions { get; }

        public List<string> Notes { get; }
    }

    /// <summary>
    /// Rewrites a Python module into strict OOP form: every module-level
    /// function (except the entry module's <c>main()</c>) becomes a
    /// <c>@staticmethod</c> of one generated per-module class, bodies preserved
    /// verbatim, and every same-module reference to a moved function is
    /// requalified through the class. Known limitation: a LOCAL variable that
    /// shadows a moved function's name inside a function that does not declare
    /// it as a parameter is not scope-analyzed and its uses will be qualified
    /// too; parameter shadowing IS detected and left alone.
    /// </summary>
    public static class PythonOopConverter
    {
        /// <summary>
        /// PascalCase wrapper-class name for a module: http_utils → HttpUtils.
        /// Test modules (test_*.py / *_test.py) get a pytest-collectable Test-prefixed
        /// name. Names already taken in the module are suffixed deterministically.
        /// </summary>
        public static string ClassNameForModule(string moduleName, bool isTestModule, ISet<string> takenNames)
        {
            string pascal = Pascal(moduleName);
            if (pascal.Length == 0) pascal = "Module";
            if (isTestModule && !pascal.StartsWith("Test", StringComparison.Ordinal))
                pascal = "Test" + pascal;

            string candidate = pascal;
            int n = 2;
            while (takenNames != null && takenNames.Contains(candidate))
            {
                candidate = pascal + (n == 2 ? "Module" : "Module" + n);
                n++;
            }
            return candidate;
        }

        private static string Pascal(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            bool upperNext = true;
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(upperNext && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
                    upperNext = char.IsDigit(c);
                }
                else
                {
                    upperNext = true;  // '_', '-', '.' split words
                }
            }
            return sb.ToString();
        }

        public static bool IsTestModuleName(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return false;
            return moduleName.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
                   moduleName.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(moduleName, "test", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Convert one module. <paramref name="moduleName"/> is the file base name
        /// (no extension); <paramref name="isEntryModule"/> preserves the PSM entry
        /// shape (top-level <c>def main()</c> + <c>__main__</c> guard stay).
        /// </summary>
        public static OopConversionResult ConvertModule(string source, string moduleName, bool isEntryModule)
        {
            var unchanged = new OopConversionResult(source, false);
            if (source == null) return unchanged;

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch { module = null; }
            if (module == null)
            {
                unchanged.Notes.Add("not converted (file does not parse)");
                return unchanged;
            }

            // 1) Which top-level functions move.
            //
            // conftest.py is handled function-by-function like any other test module, NOT skipped
            // wholesale as it used to be. The wholesale skip did protect the fixtures — moving one
            // into a class silently disables it — but it also left any ordinary helper beside them
            // unconverted, and PythonOopAuditor then failed the entire import over a function that
            // nothing was willing to fix. The protocol filter below is the finer instrument that
            // skip was standing in for: fixtures, hooks and tests stay put, plain helpers move, and
            // every reference to a moved helper (including the ones inside the kept fixture bodies)
            // is requalified by QualifyBareReferences, which walks the whole module and honours
            // parameter shadowing — so a fixture requested by parameter name is never rewritten.
            bool isTestInfrastructure =
                HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule.IsTestInfrastructureModuleName(moduleName);
            var moved = new List<FunctionDefSyntax>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            int protocolKept = 0;
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is ClassDefSyntax cd) taken.Add(cd.Name);
                if (s is FunctionDefSyntax fn)
                {
                    if (isEntryModule && fn.Name == "main") continue;

                    // In test infrastructure, the functions the runner can only find at module
                    // level. Moving a @pytest.fixture into a class does not make the tests OOP —
                    // it makes the fixture silently stop being applied, and moving a test function
                    // is a rewrite of the user's suite that buys nothing. Same carve-out the
                    // IDE's PY8002 pass applies inside a test project.
                    if (isTestInfrastructure && HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule
                            .IsTestProtocolFunction(fn.Name, PythonOopAuditor.DottedDecorators(fn)))
                    {
                        protocolKept++;
                        continue;
                    }

                    moved.Add(fn);
                }
            }
            if (moved.Count == 0)
            {
                if (protocolKept > 0)
                    unchanged.Notes.Add("not converted (" + protocolKept + " test-runner function(s) "
                        + "must stay module-level for pytest to collect them)");
                return unchanged;
            }

            // 2) A function rebound via `global f` anywhere cannot move safely.
            var globalNames = new HashSet<string>(StringComparer.Ordinal);
            CollectGlobalNames(module, globalNames);
            var kept = moved.Where(f => globalNames.Contains(f.Name)).ToList();
            foreach (FunctionDefSyntax f in kept)
            {
                moved.Remove(f);
            }
            if (moved.Count == 0)
            {
                unchanged.Notes.Add("not converted (every candidate is rebound via 'global')");
                return unchanged;
            }

            var movedNames = new HashSet<string>(moved.Select(f => f.Name), StringComparer.Ordinal);
            // The Test- prefix keys on IsTestModuleName, NOT on isTestInfrastructure: a conftest's
            // helper class must be `Conftest`, never `TestConftest`, or pytest starts trying to
            // collect the generated class as a test class.
            string className = ClassNameForModule(moduleName, IsTestModuleName(moduleName), taken);

            // 3) Qualify same-module references BEFORE moving anything, so sliced
            //    bodies come out already rewritten.
            string qualified = QualifyBareReferences(source, module, movedNames, className);

            // 4) Re-parse the qualified text for fresh spans, then splice the class in.
            ModuleSyntax module2;
            try { module2 = global::HelixPythonNamespace.Python.Python.ParseModule(qualified).Module; }
            catch { module2 = null; }
            if (module2 == null)
            {
                unchanged.Notes.Add("not converted (defensive: reference rewrite produced unparsable text)");
                return unchanged;
            }

            var blocks = new List<(int start, int end, FunctionDefSyntax fn)>();
            foreach (StatementSyntax s in module2.Statements)
            {
                FunctionDefSyntax fn = s as FunctionDefSyntax;
                if (fn == null || !movedNames.Contains(fn.Name)) continue;
                if (module2 != null && isEntryModule && fn.Name == "main") continue;
                int start = fn.Span.Start.Offset;
                foreach (DecoratorSyntax d in fn.Decorators)
                    start = Math.Min(start, d.Span.Start.Offset);
                start = LineStart(qualified, start);
                int end = LineEndInclusive(qualified, fn.Span.End.Offset);
                blocks.Add((start, end, fn));
            }
            if (blocks.Count == 0) return unchanged;
            blocks.Sort((a, b) => a.start.CompareTo(b.start));

            string eol = qualified.Contains("\r\n") ? "\r\n" : "\n";
            string classText = BuildClassText(qualified, blocks, className, eol);

            var sb = new StringBuilder(qualified.Length + classText.Length);
            int cursor = 0;
            bool inserted = false;
            foreach (var b in blocks)
            {
                sb.Append(qualified, cursor, b.start - cursor);
                if (!inserted)
                {
                    sb.Append(classText).Append(eol);
                    inserted = true;
                }
                cursor = b.end;
            }
            if (cursor < qualified.Length) sb.Append(qualified, cursor, qualified.Length - cursor);
            string result = sb.ToString();

            // 5) __all__ entries naming moved functions now export the class instead.
            result = RewriteDunderAll(result, movedNames, className, eol);

            // Defensive gate: never emit text that no longer parses.
            try
            {
                if (global::HelixPythonNamespace.Python.Python.ParseModule(result).Module == null)
                {
                    unchanged.Notes.Add("not converted (defensive: converted text failed to re-parse)");
                    return unchanged;
                }
            }
            catch
            {
                unchanged.Notes.Add("not converted (defensive: converted text failed to re-parse)");
                return unchanged;
            }

            var ok = new OopConversionResult(result, true) { ClassName = className };
            ok.MovedFunctions.AddRange(blocks.Select(b => b.fn.Name));
            foreach (FunctionDefSyntax f in kept)
                ok.Notes.Add("kept 'def " + f.Name + "' module-level (rebound via 'global')");
            ok.Notes.Add("encapsulated " + blocks.Count + " function(s) into class " + className);
            return ok;
        }

        // ---- reference qualification ----

        /// <summary>
        /// Token-splice every bare reference to a moved function into
        /// <c>ClassName.name</c>. Skips: attribute accesses (preceded by '.'),
        /// def/class name positions, global/nonlocal lists, import statements,
        /// keyword-argument names, parameter names, and any position inside a
        /// function whose own parameters shadow the name.
        /// </summary>
        internal static string QualifyBareReferences(string source, ModuleSyntax module,
                                                     ISet<string> names, string className)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string n in names) map[n] = className + "." + n;
            return QualifyBareReferences(source, module, map);
        }

        /// <summary>
        /// Generalized bare-reference rewrite: every qualifying Name token found in
        /// <paramref name="replacements"/> is spliced to its mapped text. Shared by the
        /// same-module conversion and the cross-module consumer rewrite.
        /// </summary>
        internal static string QualifyBareReferences(string source, ModuleSyntax module,
                                                     IDictionary<string, string> replacements)
        {
            List<(int start, int end, string text)> edits =
                ComputeBareReferenceEdits(source, module, replacements);
            return edits.Count == 0 ? source : ApplyEdits(source, edits);
        }

        internal static string ApplyEdits(string source, List<(int start, int end, string text)> edits)
        {
            edits.Sort((a, b) => a.start.CompareTo(b.start));
            var sb = new StringBuilder(source.Length + 64);
            int cursor = 0;
            foreach (var e in edits)
            {
                if (e.start < cursor) continue;  // defensive: overlapping edits
                if (e.start < 0 || e.end > source.Length || e.end < e.start) continue;
                sb.Append(source, cursor, e.start - cursor);
                sb.Append(e.text);
                cursor = e.end;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);
            return sb.ToString();
        }

        internal static List<(int start, int end, string text)> ComputeBareReferenceEdits(
            string source, ModuleSyntax module, IDictionary<string, string> replacements)
        {
            var edits = new List<(int start, int end, string text)>();
            IReadOnlyList<Token> tokens;
            try { tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source); }
            catch { return edits; }

            // Spans to leave alone: import statements; offsets of parameter names.
            var importSpans = new List<(int start, int end)>();
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is ImportStatement || s is FromImportStatement)
                    importSpans.Add((s.Span.Start.Offset, s.Span.End.Offset));
            }
            var paramNameOffsets = new HashSet<int>();
            var shadowScopes = new List<(int start, int end, HashSet<string> parms)>();
            CollectFunctionScopes(module, paramNameOffsets, shadowScopes);

            for (int i = 0; i < tokens.Count; i++)
            {
                Token t = tokens[i];
                if (t.Kind != TokenKind.Name || !replacements.ContainsKey(t.Text)) continue;

                int off = t.Span.Start.Offset;
                int end = t.Span.End.Offset;
                if (end <= off || off < 0 || end > source.Length) continue;

                if (paramNameOffsets.Contains(off)) continue;
                if (InAnySpan(off, importSpans)) continue;

                Token prev = PrevSignificant(tokens, i);
                if (prev != null && (prev.Kind == TokenKind.Dot ||
                                     prev.Kind == TokenKind.KwDef ||
                                     prev.Kind == TokenKind.KwClass ||
                                     prev.Kind == TokenKind.KwGlobal ||
                                     prev.Kind == TokenKind.KwNonlocal))
                    continue;
                // `global a, b` / `nonlocal a, b` continuation names.
                if (prev != null && prev.Kind == TokenKind.Comma && InGlobalList(tokens, i)) continue;

                // Keyword-argument name: '(' or ',' before, '=' (not '==') after.
                Token next = NextSignificant(tokens, i);
                if (prev != null && next != null && next.Kind == TokenKind.Assign &&
                    (prev.Kind == TokenKind.LeftParen || prev.Kind == TokenKind.Comma))
                    continue;

                // Shadowed by an enclosing function's parameter of the same name.
                bool shadowed = false;
                foreach (var scope in shadowScopes)
                {
                    if (off >= scope.start && off < scope.end && scope.parms.Contains(t.Text))
                    {
                        shadowed = true;
                        break;
                    }
                }
                if (shadowed) continue;

                edits.Add((off, end, replacements[t.Text]));
            }
            return edits;
        }

        private static void CollectFunctionScopes(SyntaxNode node, HashSet<int> paramNameOffsets,
                                                  List<(int, int, HashSet<string>)> scopes)
        {
            if (node == null) return;
            FunctionDefSyntax fn = node as FunctionDefSyntax;
            if (fn != null && fn.Parameters.Count > 0)
            {
                var parms = new HashSet<string>(StringComparer.Ordinal);
                foreach (ParameterSyntax p in fn.Parameters)
                {
                    parms.Add(p.Name);
                    paramNameOffsets.Add(p.Span.Start.Offset);
                }
                scopes.Add((fn.Span.Start.Offset, fn.Span.End.Offset, parms));
            }
            LambdaExpression lam = node as LambdaExpression;
            if (lam != null && lam.Parameters.Count > 0)
            {
                var parms = new HashSet<string>(StringComparer.Ordinal);
                foreach (ParameterSyntax p in lam.Parameters)
                {
                    parms.Add(p.Name);
                    paramNameOffsets.Add(p.Span.Start.Offset);
                }
                scopes.Add((lam.Span.Start.Offset, lam.Span.End.Offset, parms));
            }
            foreach (SyntaxNode child in node.Children) CollectFunctionScopes(child, paramNameOffsets, scopes);
        }

        private static void CollectGlobalNames(SyntaxNode node, HashSet<string> names)
        {
            if (node == null) return;
            GlobalStatement g = node as GlobalStatement;
            if (g != null)
            {
                foreach (string n in g.Names) names.Add(n);
            }
            foreach (SyntaxNode child in node.Children) CollectGlobalNames(child, names);
        }

        private static bool InGlobalList(IReadOnlyList<Token> tokens, int i)
        {
            // Walk left over `name ,` pairs; a global/nonlocal keyword starts the list.
            int j = i - 1;
            while (j >= 0)
            {
                Token t = tokens[j];
                if (IsTrivia(t.Kind)) { j--; continue; }
                if (t.Kind == TokenKind.Comma || t.Kind == TokenKind.Name) { j--; continue; }
                return t.Kind == TokenKind.KwGlobal || t.Kind == TokenKind.KwNonlocal;
            }
            return false;
        }

        private static bool InAnySpan(int offset, List<(int start, int end)> spans)
        {
            foreach (var s in spans)
                if (offset >= s.start && offset < s.end) return true;
            return false;
        }

        private static bool IsTrivia(TokenKind k)
        {
            return k == TokenKind.Whitespace || k == TokenKind.Comment ||
                   k == TokenKind.NewLine || k == TokenKind.Indent ||
                   k == TokenKind.Dedent || k == TokenKind.LineContinuation;
        }

        private static Token PrevSignificant(IReadOnlyList<Token> tokens, int i)
        {
            for (int j = i - 1; j >= 0; j--)
                if (!IsTrivia(tokens[j].Kind)) return tokens[j];
            return null;
        }

        private static Token NextSignificant(IReadOnlyList<Token> tokens, int i)
        {
            for (int j = i + 1; j < tokens.Count; j++)
                if (!IsTrivia(tokens[j].Kind)) return tokens[j];
            return null;
        }

        // ---- class assembly ----

        private static string BuildClassText(string source,
            List<(int start, int end, FunctionDefSyntax fn)> blocks, string className, string eol)
        {
            var lines = new List<string> { "class " + className + ":" };
            for (int i = 0; i < blocks.Count; i++)
            {
                if (i > 0) lines.Add(string.Empty);
                lines.Add("    @staticmethod");
                string block = source.Substring(blocks[i].start, blocks[i].end - blocks[i].start);
                foreach (string raw in SplitLines(block))
                {
                    lines.Add(raw.Length == 0 ? string.Empty : "    " + raw);
                }
                // Drop trailing blank method lines so spacing stays canonical.
                while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                    lines.RemoveAt(lines.Count - 1);
            }
            return string.Join(eol, lines);
        }

        private static IEnumerable<string> SplitLines(string block)
        {
            // Normalizes nothing: yields lines without their terminators.
            int start = 0;
            for (int i = 0; i < block.Length; i++)
            {
                if (block[i] == '\n')
                {
                    int len = i - start;
                    if (len > 0 && block[start + len - 1] == '\r') len--;
                    yield return block.Substring(start, len);
                    start = i + 1;
                }
            }
            if (start < block.Length) yield return block.Substring(start);
        }

        private static int LineStart(string text, int offset)
        {
            int i = Math.Min(offset, text.Length);
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        private static int LineEndInclusive(string text, int offset)
        {
            int i = Math.Min(offset, text.Length);
            while (i < text.Length && text[i] != '\n') i++;
            return i < text.Length ? i + 1 : i;
        }

        // ---- __all__ ----

        private static string RewriteDunderAll(string text, ISet<string> movedNames, string className, string eol)
        {
            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(text).Module; }
            catch { return text; }
            if (module == null) return text;

            foreach (StatementSyntax s in module.Statements)
            {
                AssignmentStatement asn = s as AssignmentStatement;
                if (asn == null || asn.Targets.Count != 1) continue;
                NameExpression target = asn.Targets[0] as NameExpression;
                if (target == null || target.Name != "__all__") continue;

                IReadOnlyList<ExpressionSyntax> elements;
                if (asn.Value is ListExpression list) elements = list.Elements;
                else if (asn.Value is TupleExpression tup) elements = tup.Elements;
                else return text;

                var rebuilt = new List<string>();
                bool classAdded = false;
                bool anyMoved = false;
                foreach (ExpressionSyntax e in elements)
                {
                    LiteralExpression lit = e as LiteralExpression;
                    if (lit == null || lit.Token.Kind != TokenKind.String) return text;  // not a plain name list
                    string content = StringLiteralContent(lit.Token.Text);
                    if (content == null) return text;
                    if (movedNames.Contains(content))
                    {
                        anyMoved = true;
                        if (!classAdded)
                        {
                            rebuilt.Add("\"" + className + "\"");
                            classAdded = true;
                        }
                    }
                    else
                    {
                        rebuilt.Add("\"" + content + "\"");
                    }
                }
                if (!anyMoved) return text;

                int start = asn.Value.Span.Start.Offset;
                int end = asn.Value.Span.End.Offset;
                if (start < 0 || end > text.Length || end <= start) return text;
                return text.Substring(0, start) + "[" + string.Join(", ", rebuilt) + "]" +
                       text.Substring(end);
            }
            return text;
        }

        internal static string StringLiteralContent(string tokenText)
        {
            if (string.IsNullOrEmpty(tokenText)) return null;
            int q = tokenText.IndexOfAny(new[] { '\'', '"' });
            if (q < 0) return null;
            char quote = tokenText[q];
            int qlen = (tokenText.Length >= q + 3 && tokenText[q + 1] == quote && tokenText[q + 2] == quote) ? 3 : 1;
            int contentStart = q + qlen;
            int contentEnd = tokenText.Length - qlen;
            if (contentEnd < contentStart) return null;
            return tokenText.Substring(contentStart, contentEnd - contentStart);
        }
    }
}
