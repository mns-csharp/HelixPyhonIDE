using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// The PSM entry-point shape of ONE Python module, read off the real parse tree
    /// (<c>HelixPythonNamespace.Python.Python.ParseModule</c>) instead of matched out of the source text.
    ///
    /// This replaced three regular expressions in <see cref="PackageComplianceValidator"/>, and it
    /// exists because those regexes could not tell Python from something that merely looked like
    /// it. The canonical-guard pattern demanded a newline immediately after the colon, so
    ///
    /// <code>
    /// if __name__ == "__main__":   # if this module is run directly...
    ///     main()
    /// </code>
    ///
    /// failed PSM-16 — purely because of the comment — and blocked Run and Debug on three valid
    /// entry modules of a real solution. A parse tree has no opinion about comments, blank lines,
    /// spacing, line continuations, quote style or operand order, so every spelling of the guard
    /// collapses to the same shape and only the shape is judged.
    ///
    /// Everything here is module-level by construction: only <c>module.Statements</c> is walked, so
    /// a method named <c>main</c> inside a class, or a guard nested in a function, is invisible —
    /// the same scope the regexes approximated with a column-0 anchor.
    /// </summary>
    public sealed class PythonEntryPointFacts
    {
        private PythonEntryPointFacts() { }

        /// <summary>False only when the parser threw — i.e. there is no tree at all to read.</summary>
        public bool Parsed { get; private set; }

        /// <summary>True when the parse produced error diagnostics, so the tree is a recovery guess.</summary>
        public bool HasSyntaxErrors { get; private set; }

        /// <summary>
        /// True when these facts may be used to REPORT a rule violation. A file that does not parse
        /// tells us nothing about its entry-point shape, and error recovery routinely drops whole
        /// statements — flagging PSM-09/11/12/16 off such a tree would be inventing findings out of
        /// absence of evidence, on a file the compiler is already refusing for its syntax error.
        /// </summary>
        public bool Trustworthy { get { return Parsed && !HasSyntaxErrors; } }

        /// <summary>A module-level <c>def main(...)</c> (or <c>async def</c>) is present.</summary>
        public bool HasModuleLevelMainFunction { get; private set; }

        /// <summary>1-based line of the first module-level <c>def main</c>; 0 when there is none.</summary>
        public int MainFunctionLine { get; private set; }

        /// <summary>The module-level <c>main</c> is a coroutine (<c>async def main</c>).</summary>
        public bool MainIsAsync { get; private set; }

        /// <summary>
        /// The module-level <c>main</c> declares a parameter the caller MUST supply. A defaulted
        /// parameter (<c>argv=None</c>), <c>*args</c>/<c>**kwargs</c>, and the bare <c>*</c> / <c>/</c>
        /// markers all still allow <c>main()</c> with no arguments, so none of them counts.
        /// </summary>
        public bool MainHasRequiredParameter { get; private set; }

        /// <summary>A module-level <c>if __name__ == "__main__":</c> is present.</summary>
        public bool HasInvocationGuard { get; private set; }

        /// <summary>1-based line of the first such guard; 0 when there is none.</summary>
        public int InvocationGuardLine { get; private set; }

        /// <summary>
        /// Some module-level <c>__main__</c> guard actually calls <c>main()</c> — directly, or
        /// wrapped as <c>raise SystemExit(main())</c> / <c>sys.exit(main())</c> / any other call
        /// that takes <c>main()</c> as an argument. This is PSM-16's real question, and the tree
        /// answers it regardless of how the body is laid out or what else the body does first.
        /// </summary>
        public bool GuardInvokesMain { get; private set; }

        /// <summary>Parse <paramref name="source"/> and read off its entry-point shape. Never throws.</summary>
        public static PythonEntryPointFacts Analyze(string source, string filePath = null)
        {
            PythonEntryPointFacts facts = new PythonEntryPointFacts();
            if (source == null) { facts.Parsed = true; return facts; }

            ModuleSyntax module = null;
            try
            {
                var result = global::HelixPythonNamespace.Python.Python.ParseModule(source, filePath);
                module = result.Module;
                facts.HasSyntaxErrors = result.HasErrors;
            }
            catch
            {
                module = null;   // Parsed stays false: no tree at all.
            }
            if (module == null)
            {
                facts.RecoverModuleLevelMainFunction(source);
                return facts;
            }

            facts.Parsed = true;
            foreach (StatementSyntax statement in module.Statements)
            {
                FunctionDefSyntax function = statement as FunctionDefSyntax;
                if (function != null)
                {
                    if (function.Name == "main" && !facts.HasModuleLevelMainFunction)
                    {
                        facts.HasModuleLevelMainFunction = true;
                        facts.MainFunctionLine = statement.Span.Start.Line;
                        facts.MainIsAsync = function.IsAsync;
                        facts.MainHasRequiredParameter = HasRequiredParameter(function);
                    }
                    continue;
                }

                IfStatement guard = statement as IfStatement;
                if (guard == null || !IsDunderMainTest(guard.Test)) continue;

                if (!facts.HasInvocationGuard)
                {
                    facts.HasInvocationGuard = true;
                    facts.InvocationGuardLine = statement.Span.Start.Line;
                }
                if (!facts.GuardInvokesMain && InvokesMain(guard.Body))
                    facts.GuardInvokesMain = true;
            }

            facts.RecoverModuleLevelMainFunction(source);
            return facts;
        }

        /// <summary>
        /// Degraded path for a file that did not parse cleanly: error recovery can swallow whole
        /// statements, including the <c>def main</c> a plain line scan would still see. Only ever
        /// ADDS a detection, and only when the parse is untrustworthy — a clean tree is never
        /// second-guessed — so a solution carrying a syntax error keeps reporting the entry-point
        /// facts it reported before this class existed.
        /// </summary>
        private void RecoverModuleLevelMainFunction(string source)
        {
            if (Trustworthy || HasModuleLevelMainFunction) return;
            int line = ScanForModuleLevelMainFunction(source);
            if (line <= 0) return;
            HasModuleLevelMainFunction = true;
            MainFunctionLine = line;
        }

        /// <summary>
        /// The condition of a <c>__main__</c> guard: <c>__name__ == "__main__"</c> in either
        /// operand order and either quote style. The single authority on that shape — shared with
        /// <see cref="PythonOopAuditor.IsDunderMainGuard"/>, which adds its own requirement that the
        /// statement carry no elif/else.
        /// </summary>
        public static bool IsDunderMainTest(ExpressionSyntax test)
        {
            ComparisonExpression comparison = test as ComparisonExpression;
            if (comparison == null) return false;
            if (comparison.Operators.Count != 1 || comparison.Operators[0] != ComparisonOperator.Eq)
                return false;
            if (comparison.Comparators.Count != 1) return false;

            return (IsName(comparison.Left, "__name__") &&
                    IsStringLiteral(comparison.Comparators[0], "__main__")) ||
                   (IsName(comparison.Comparators[0], "__name__") &&
                    IsStringLiteral(comparison.Left, "__main__"));
        }

        internal static bool IsName(ExpressionSyntax expression, string name)
        {
            NameExpression n = expression as NameExpression;
            return n != null && string.Equals(n.Name, name, StringComparison.Ordinal);
        }

        internal static bool IsStringLiteral(ExpressionSyntax expression, string content)
        {
            LiteralExpression literal = expression as LiteralExpression;
            if (literal == null || literal.Token.Kind != TokenKind.String) return false;

            string decoded = literal.Token.Value as string;
            if (decoded != null) return string.Equals(decoded, content, StringComparison.Ordinal);

            // Fallback for a lexer that kept only raw text: it still carries the quotes and any
            // prefix, so bound the length to rule out a longer string that merely contains it.
            string text = literal.Token.Text;
            return text != null && text.Contains(content) && text.Length <= content.Length + 8;
        }

        /// <summary>
        /// True when <c>main()</c> is called anywhere inside the guard body. Deliberately a subtree
        /// search rather than "the first statement must be exactly this": a body that sets up an
        /// encoding, or wraps the call in <c>sys.exit(...)</c>, or guards it with an <c>if</c>, is
        /// still invoking main() through the guard, which is all PSM-16 asks.
        /// </summary>
        private static bool InvokesMain(SuiteSyntax body)
        {
            if (body == null) return false;
            foreach (StatementSyntax statement in body.Statements)
            {
                foreach (SyntaxNode node in statement.Descendants())
                    if (IsBareMainCall(node)) return true;
            }
            return false;
        }

        /// <summary><c>main()</c> — the name <c>main</c>, called with no arguments.</summary>
        private static bool IsBareMainCall(SyntaxNode node)
        {
            CallExpression call = node as CallExpression;
            if (call == null || call.Arguments.Count != 0) return false;
            return IsName(call.Callee, "main");
        }

        private static bool HasRequiredParameter(FunctionDefSyntax function)
        {
            foreach (ParameterSyntax parameter in function.Parameters)
            {
                if (string.IsNullOrEmpty(parameter.Name)) continue;   // bare * / positional-only marker
                if (parameter.Kind == ParameterKind.VarPositional ||
                    parameter.Kind == ParameterKind.VarKeyword) continue;
                if (parameter.DefaultValue != null) continue;
                return true;
            }
            return false;
        }

        // ---- unparsable-file fallback ------------------------------------------------------
        // The pre-parser line scanner, kept verbatim in behaviour and reachable ONLY from
        // RecoverModuleLevelMainFunction. It finds a column-0 `def main(` / `async def main(`
        // while tracking triple-quoted strings and skipping comments, so it is not fooled by a
        // docstring or commented-out code — but it cannot see anything the parser sees, which is
        // exactly why it is the fallback and not the rule.

        /// <summary>1-based line of a module-level <c>def main</c>, or 0 when there is none.</summary>
        private static int ScanForModuleLevelMainFunction(string source)
        {
            if (string.IsNullOrEmpty(source)) return 0;

            string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string openTriple = null;   // active """ or ''' delimiter while spanning lines

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];

                if (openTriple != null)
                {
                    int close = line.IndexOf(openTriple, StringComparison.Ordinal);
                    if (close < 0) continue;                 // still inside the string
                    line = line.Substring(close + openTriple.Length);
                    openTriple = null;
                }

                // A def at column 0 has no leading whitespace, so a line that starts with
                // whitespace can never be a module-level def — but it may still *open* a
                // triple-quoted string we must track for subsequent lines.
                string trimmedStart = line.TrimStart();

                if (trimmedStart.Length == 0) { TrackOpeningTriple(line, ref openTriple); continue; }
                if (trimmedStart[0] == '#') { continue; }

                if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                {
                    if (StartsWithDefMain(trimmedStart)) return index + 1;
                    if (trimmedStart.StartsWith("async ", StringComparison.Ordinal))
                    {
                        string after = trimmedStart.Substring("async ".Length).TrimStart();
                        if (StartsWithDefMain(after)) return index + 1;
                    }
                }

                TrackOpeningTriple(line, ref openTriple);
            }
            return 0;
        }

        private static bool StartsWithDefMain(string t)
        {
            if (!t.StartsWith("def", StringComparison.Ordinal)) return false;
            string rest = t.Substring(3);
            if (rest.Length == 0 || !char.IsWhiteSpace(rest[0])) return false; // "define(" is not "def "
            rest = rest.TrimStart();
            if (!rest.StartsWith("main", StringComparison.Ordinal)) return false;
            string afterName = rest.Substring(4).TrimStart();
            return afterName.StartsWith("(", StringComparison.Ordinal); // "main(" — not "maindish("
        }

        /// <summary>
        /// If <paramref name="line"/> opens (and does not close) a triple-quoted string, set
        /// <paramref name="openTriple"/> to the delimiter so following lines are skipped.
        /// Counts occurrences to tell an opened-and-closed one-liner from a genuinely open one.
        /// </summary>
        private static void TrackOpeningTriple(string line, ref string openTriple)
        {
            int i = 0;
            while (i < line.Length)
            {
                if (openTriple == null && line[i] == '#') return; // rest is a comment

                string delim = null;
                if (Match(line, i, "\"\"\"")) delim = "\"\"\"";
                else if (Match(line, i, "'''")) delim = "'''";

                if (delim == null) { i++; continue; }

                if (openTriple == null) { openTriple = delim; i += 3; }
                else if (openTriple == delim) { openTriple = null; i += 3; } // closed on same line
                else { i += 3; }
            }
        }

        private static bool Match(string s, int at, string token)
        {
            if (at + token.Length > s.Length) return false;
            for (int k = 0; k < token.Length; k++)
                if (s[at + k] != token[k]) return false;
            return true;
        }
    }
}
