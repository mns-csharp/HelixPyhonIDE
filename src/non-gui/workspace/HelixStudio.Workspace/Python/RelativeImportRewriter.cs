using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Rewrites single-dot relative imports (<c>from . import X</c> / <c>from .X import Y</c>,
    /// <c>FromImportStatement.RelativeDots == 1</c>) into absolute imports rooted at a new
    /// package name. Used when a file is extracted out of its original package into a new
    /// sibling package (see <see cref="PackageSplitter"/>) — its sibling-relative imports must
    /// now point at wherever those sibling modules ended up. Deeper relative levels
    /// (<c>..</c>, <c>...</c>) are left untouched; they never occur in a flat single-level
    /// package, the only shape <see cref="PackageSplitter"/> extracts from.
    /// </summary>
    public static class RelativeImportRewriter
    {
        public readonly struct RewriteResult
        {
            public readonly string Text;
            public readonly int Count;
            public RewriteResult(string text, int count) { Text = text; Count = count; }
        }

        public static RewriteResult RewriteSameLevelRelativeImports(string source, string newAbsoluteModule)
        {
            if (source == null) return new RewriteResult(null, 0);
            if (string.IsNullOrEmpty(newAbsoluteModule)) return new RewriteResult(source, 0);

            ModuleSyntax module;
            IReadOnlyList<Token> tokens;
            try
            {
                module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module;
                tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source);
            }
            catch { return new RewriteResult(source, 0); }
            if (module == null) return new RewriteResult(source, 0);

            List<(int start, int end, string replacement)> hits = new List<(int, int, string)>();
            foreach (StatementSyntax s in EnumerateAllStatements(module.Statements))
            {
                if (!(s is FromImportStatement fi)) continue;
                if (fi.RelativeDots != 1) continue; // only the single-level "sibling module" case

                string replacement = fi.Module != null
                    ? newAbsoluteModule + "." + fi.Module.AsString
                    : newAbsoluteModule;

                (int start, int end)? span = FindRelativeModuleSpan(tokens, fi.Span, fi.Module);
                if (span == null) continue;
                hits.Add((span.Value.start, span.Value.end, replacement));
            }

            if (hits.Count == 0) return new RewriteResult(source, 0);

            hits.Sort((a, b) => a.start.CompareTo(b.start));
            StringBuilder sb = new StringBuilder();
            int cursor = 0;
            int applied = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue; // defensive: overlap
                if (h.start < 0 || h.end > source.Length || h.end <= h.start) continue;
                sb.Append(source, cursor, h.start - cursor);
                sb.Append(h.replacement);
                cursor = h.end;
                applied++;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);

            return new RewriteResult(sb.ToString(), applied);
        }

        /// <summary>
        /// Yields every statement reachable from <paramref name="statements"/> at ANY nesting
        /// depth — a <c>from . import x</c> deferred/lazy import inside a function body (a
        /// common pattern to avoid circular imports or defer a heavy import) is just as much a
        /// sibling-relative import needing rewriting as one at module level. Missing these was
        /// a real bug: PackageSplitter moved the sibling module out from under a top-level-only
        /// rewrite, and the deferred import kept resolving relative to a now-nonexistent parent
        /// package, raising ImportError at actual run time — the earlier top-level-only walk
        /// (matching module.Statements) never saw it because it only checked immediate children.
        /// </summary>
        private static IEnumerable<StatementSyntax> EnumerateAllStatements(IEnumerable<StatementSyntax> statements)
        {
            foreach (StatementSyntax s in statements)
            {
                if (s == null) continue;
                yield return s;
                foreach (StatementSyntax nested in EnumerateAllStatements(NestedBodyStatements(s)))
                    yield return nested;
            }
        }

        /// <summary>The immediate child statements of every body/handler a compound statement owns.</summary>
        private static IEnumerable<StatementSyntax> NestedBodyStatements(StatementSyntax s)
        {
            switch (s)
            {
                case FunctionDefSyntax fd:
                    if (fd.Body != null) foreach (StatementSyntax x in fd.Body.Statements) yield return x;
                    break;
                case ClassDefSyntax cd:
                    if (cd.Body != null) foreach (StatementSyntax x in cd.Body.Statements) yield return x;
                    break;
                case IfStatement ifs:
                    if (ifs.Body != null) foreach (StatementSyntax x in ifs.Body.Statements) yield return x;
                    foreach (IfStatement elif in ifs.Elifs)
                        if (elif.Body != null) foreach (StatementSyntax x in elif.Body.Statements) yield return x;
                    if (ifs.Else != null) foreach (StatementSyntax x in ifs.Else.Statements) yield return x;
                    break;
                case WhileStatement ws:
                    if (ws.Body != null) foreach (StatementSyntax x in ws.Body.Statements) yield return x;
                    if (ws.Else != null) foreach (StatementSyntax x in ws.Else.Statements) yield return x;
                    break;
                case ForStatement fs:
                    if (fs.Body != null) foreach (StatementSyntax x in fs.Body.Statements) yield return x;
                    if (fs.Else != null) foreach (StatementSyntax x in fs.Else.Statements) yield return x;
                    break;
                case TryStatement ts:
                    if (ts.Body != null) foreach (StatementSyntax x in ts.Body.Statements) yield return x;
                    foreach (ExceptHandler h in ts.Handlers)
                        if (h.Body != null) foreach (StatementSyntax x in h.Body.Statements) yield return x;
                    if (ts.Else != null) foreach (StatementSyntax x in ts.Else.Statements) yield return x;
                    if (ts.Finally != null) foreach (StatementSyntax x in ts.Finally.Statements) yield return x;
                    break;
                case WithStatement wts:
                    if (wts.Body != null) foreach (StatementSyntax x in wts.Body.Statements) yield return x;
                    break;
            }
        }

        /// <summary>
        /// Within a <c>from</c>-statement's token span, find the span covering the leading dot
        /// plus the optional module name that follows it — i.e. everything between <c>from</c>
        /// and <c>import</c>. That whole span is what a relative import replaces wholesale with
        /// an absolute module path.
        /// </summary>
        private static (int start, int end)? FindRelativeModuleSpan(
            IReadOnlyList<Token> tokens, SourceSpan stmtSpan, DottedName moduleName)
        {
            int startOff = stmtSpan.Start.Offset;
            int endOff = stmtSpan.End.Offset;

            bool seenFrom = false;
            int dotStart = -1;
            int dotEnd = -1;
            foreach (Token t in tokens)
            {
                if (t == null || t.IsTrivia) continue;
                int tStart = t.Span.Start.Offset;
                if (tStart < startOff) continue;
                if (tStart >= endOff) break;

                if (!seenFrom)
                {
                    if (t.Kind == TokenKind.KwFrom) seenFrom = true;
                    continue;
                }
                if (t.Kind == TokenKind.Dot)
                {
                    if (dotStart < 0) dotStart = t.Span.Start.Offset;
                    dotEnd = t.Span.End.Offset;
                    continue;
                }
                break; // KwImport, or the module-name token (covered by moduleName.Span already)
            }
            if (dotStart < 0) return null;

            int end = moduleName != null ? moduleName.Span.End.Offset : dotEnd;
            return (dotStart, end);
        }
    }
}
