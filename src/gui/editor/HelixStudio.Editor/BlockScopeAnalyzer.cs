using System.Collections.Generic;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Editor
{
    public enum BlockKind
    {
        Class,
        Def,
        If,
        For,
        While,
        Try,
        With,
        Match,
    }

    public struct BlockRange
    {
        public BlockKind Kind;
        // 1-based; the LAST physical line of the header — the line carrying the `:` that opens
        // the suite. For a wrapped signature (`def f(\n a,\n) -> R:`) that is the `) -> R:` line,
        // which is where the virtual `{` belongs, exactly as a C# programmer would place it.
        public int HeaderLine;
        // 1-based; the FIRST physical line of the same logical header (the `def`/`class`/`if`
        // line). Equals HeaderLine unless the header wraps across lines. The header tint spans
        // HeaderStartLine..HeaderLine, and ParentIndentColumn is measured from this line.
        public int HeaderStartLine;
        public int LastContentLine;     // 1-based; last non-blank line inside the body
        public int IndentColumn;        // 0-based; column of the suite body's first statement
        public int ParentIndentColumn;  // 0-based; column of the header's own first line
    }

    /// <summary>
    /// Pure AST walker that extracts every Python block (class, def, if/elif/else, for/else,
    /// while/else, try/except/else/finally, with, match/case) as a <see cref="BlockRange"/>.
    /// Previously lived inside VirtualBracesDecorator; pulled out so the scope-shading
    /// renderer and unit tests can share the computation without depending on HelixTextEditorNET.
    /// </summary>
    public static class BlockScopeAnalyzer
    {
        public static List<BlockRange> Analyze(ModuleSyntax module, string sourceText)
        {
            var results = new List<BlockRange>();
            if (module == null) return results;

            string[] sourceLines = sourceText != null ? sourceText.Split('\n') : null;

            foreach (StatementSyntax s in module.Statements) Visit(s, results);

            if (sourceLines != null)
            {
                bool[] continuations = ComputeContinuationFlags(sourceLines);
                for (int i = 0; i < results.Count; i++)
                {
                    BlockRange r = results[i];
                    // AddSuite assumes the header sits exactly one line above the body's
                    // first AST statement — wrong whenever a comment (or blank line) sits
                    // between the header and the first real statement, e.g.:
                    //   def f():
                    //       # explains the body below
                    //       x = 1
                    // Comments aren't AST nodes, so `firstLine - 1` lands ON the comment
                    // instead of the `def` line. Walk back past any blank/comment-only
                    // lines to the real header.
                    r.HeaderLine = SkipBackPastCommentsAndBlanks(r.HeaderLine, sourceLines);
                    // A header can span several physical lines — a wrapped parameter list, a
                    // parenthesized `if` condition, a backslash-continued `with`. HeaderLine is
                    // then the CLOSING line (`) -> R:`), whose indent is the continuation indent,
                    // not the block's own. Walking back over the continuation gives the `def`/
                    // `class`/`if` line the braces and the tint must align to.
                    r.HeaderStartLine = LogicalHeaderStartLine(r.HeaderLine, continuations);
                    r.LastContentLine = ClipToLastContentLine(r.HeaderLine, r.LastContentLine, sourceLines);
                    // For else/elif/except/finally, AddSuite's `ownerSpan` is the bare
                    // SuiteSyntax body (there's no separate AST node for the "else:"/
                    // "finally:" keyword itself) — its .Span starts at the suite's FIRST
                    // STATEMENT, not the keyword, so ParentIndentColumn silently picks up
                    // the body's indent instead of the header's own. Re-derive it from the
                    // actual header line's leading whitespace instead of trusting the AST
                    // span for this value; this is correct for every block kind uniformly.
                    r.ParentIndentColumn = HeaderLineIndent(r.HeaderStartLine, sourceLines, r.ParentIndentColumn);
                    results[i] = r;
                }
            }

            results.Sort((a, b) => a.HeaderLine.CompareTo(b.HeaderLine));
            return results;
        }

        // `headerLine` (1-based) may actually be pointing at a comment or blank line that
        // sits between the true header and the body's first AST statement. Walk backward
        // while the current line is blank/comment-only; stop at the first real content line.
        private static int SkipBackPastCommentsAndBlanks(int headerLine, string[] sourceLines)
        {
            int line = headerLine;
            while (line > 1)
            {
                int idx = line - 1; // 0-based index of `line`
                if (idx < 0 || idx >= sourceLines.Length) break;
                string text = sourceLines[idx];
                if (text.Length > 0 && text[text.Length - 1] == '\r') text = text.Substring(0, text.Length - 1);
                string trimmed = text.Trim();
                bool isCommentOrBlank = trimmed.Length == 0 || trimmed[0] == '#';
                if (!isCommentOrBlank) break;
                line--;
            }
            return line;
        }

        // The first physical line of the logical line that `headerLine` (1-based) ends: walk
        // backwards while each line is a continuation of its predecessor.
        private static int LogicalHeaderStartLine(int headerLine, bool[] isContinuation)
        {
            int line = headerLine;
            while (line > 1 && line - 1 < isContinuation.Length && isContinuation[line - 1]) line--;
            return line;
        }

        /// <summary>
        /// For each 0-based line index, whether that line CONTINUES the previous physical line —
        /// i.e. the previous line left an unclosed <c>( [ {</c>, an open triple-quoted string, or
        /// a trailing backslash. Computed by one forward scan over the file rather than from the
        /// AST: the parser's spans describe statements, and a statement's own Start.Line is not a
        /// usable substitute here (it covers decorators for `def`/`class`, sits at `def` rather
        /// than `async` for coroutines, and for an `else:`/`finally:` suite it points at the
        /// suite's first statement instead of the keyword).
        /// </summary>
        private static bool[] ComputeContinuationFlags(string[] sourceLines)
        {
            bool[] flags = new bool[sourceLines.Length];
            int bracketDepth = 0;
            char tripleQuote = '\0';   // '\0' = not inside a triple-quoted string
            bool backslashContinued = false;

            for (int i = 0; i < sourceLines.Length; i++)
            {
                flags[i] = bracketDepth > 0 || tripleQuote != '\0' || backslashContinued;

                string line = sourceLines[i];
                if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);

                // Single-quoted strings never survive a newline (an unterminated one is a syntax
                // error, not a continuation), so this resets every line; only tripleQuote and
                // bracketDepth carry over.
                char singleQuote = '\0';
                bool sawComment = false;

                int j = 0;
                while (j < line.Length)
                {
                    char c = line[j];
                    if (tripleQuote != '\0')
                    {
                        if (c == '\\') { j += 2; continue; }
                        if (c == tripleQuote && j + 2 < line.Length
                            && line[j + 1] == tripleQuote && line[j + 2] == tripleQuote)
                        {
                            tripleQuote = '\0';
                            j += 3;
                            continue;
                        }
                        j++;
                        continue;
                    }
                    if (singleQuote != '\0')
                    {
                        if (c == '\\') { j += 2; continue; }
                        if (c == singleQuote) singleQuote = '\0';
                        j++;
                        continue;
                    }
                    if (c == '#') { sawComment = true; break; }   // comment runs to end of line
                    if (c == '"' || c == '\'')
                    {
                        if (j + 2 < line.Length && line[j + 1] == c && line[j + 2] == c)
                        {
                            tripleQuote = c;
                            j += 3;
                        }
                        else
                        {
                            singleQuote = c;
                            j++;
                        }
                        continue;
                    }
                    if (c == '(' || c == '[' || c == '{') bracketDepth++;
                    else if (c == ')' || c == ']' || c == '}') { if (bracketDepth > 0) bracketDepth--; }
                    j++;
                }

                // A trailing backslash only continues the line when it is real code — not when it
                // is the last character of a comment or of an unterminated string.
                backslashContinued = !sawComment && singleQuote == '\0' && tripleQuote == '\0'
                    && line.Length > 0 && line[line.Length - 1] == '\\';
            }
            return flags;
        }

        private static int HeaderLineIndent(int headerLine, string[] sourceLines, int fallback)
        {
            int idx = headerLine - 1;
            if (idx < 0 || idx >= sourceLines.Length) return fallback;
            string line = sourceLines[idx];
            int n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            if (n >= line.Length) return fallback; // blank/whitespace-only line — keep the AST value
            return n;
        }

        // Parser's Span.End.Line overshoots past trailing blank lines (DEDENT tokens sit on the
        // next less-indented statement). Clip to the last non-blank source line so the closer
        // tick lands on the block's actual last line of content.
        private static int ClipToLastContentLine(int firstLine, int reportedLast, string[] sourceLines)
        {
            int upper = reportedLast;
            if (upper > sourceLines.Length) upper = sourceLines.Length;
            for (int i = upper - 1; i >= firstLine - 1 && i >= 0; i--)
            {
                string line = sourceLines[i];
                if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
                if (!string.IsNullOrWhiteSpace(line)) return i + 1;
            }
            return firstLine;
        }

        private static void Visit(StatementSyntax s, List<BlockRange> results)
        {
            if (s == null) return;
            switch (s)
            {
                case ClassDefSyntax cd:
                    AddSuite(results, BlockKind.Class, cd.Span, cd.Body);
                    VisitSuite(cd.Body, results);
                    break;
                case FunctionDefSyntax fd:
                    AddSuite(results, BlockKind.Def, fd.Span, fd.Body);
                    VisitSuite(fd.Body, results);
                    break;
                case WhileStatement ws:
                    AddSuite(results, BlockKind.While, ws.Span, ws.Body); VisitSuite(ws.Body, results);
                    if (ws.Else != null) { AddSuite(results, BlockKind.While, ws.Else.Span, ws.Else); VisitSuite(ws.Else, results); }
                    break;
                case ForStatement fs:
                    AddSuite(results, BlockKind.For, fs.Span, fs.Body); VisitSuite(fs.Body, results);
                    if (fs.Else != null) { AddSuite(results, BlockKind.For, fs.Else.Span, fs.Else); VisitSuite(fs.Else, results); }
                    break;
                case IfStatement ifs:
                    AddSuite(results, BlockKind.If, ifs.Span, ifs.Body); VisitSuite(ifs.Body, results);
                    foreach (IfStatement elif in ifs.Elifs)
                    {
                        AddSuite(results, BlockKind.If, elif.Span, elif.Body);
                        VisitSuite(elif.Body, results);
                    }
                    if (ifs.Else != null) { AddSuite(results, BlockKind.If, ifs.Else.Span, ifs.Else); VisitSuite(ifs.Else, results); }
                    break;
                case TryStatement ts:
                    AddSuite(results, BlockKind.Try, ts.Span, ts.Body); VisitSuite(ts.Body, results);
                    foreach (ExceptHandler h in ts.Handlers)
                    {
                        AddSuite(results, BlockKind.Try, h.Span, h.Body);
                        VisitSuite(h.Body, results);
                    }
                    if (ts.Else != null) { AddSuite(results, BlockKind.Try, ts.Else.Span, ts.Else); VisitSuite(ts.Else, results); }
                    if (ts.Finally != null) { AddSuite(results, BlockKind.Try, ts.Finally.Span, ts.Finally); VisitSuite(ts.Finally, results); }
                    break;
                case WithStatement wts:
                    AddSuite(results, BlockKind.With, wts.Span, wts.Body);
                    VisitSuite(wts.Body, results);
                    break;
                case MatchStatement ms:
                    foreach (CaseBlock cb in ms.Cases)
                    {
                        AddSuite(results, BlockKind.Match, cb.Span, cb.Body);
                        VisitSuite(cb.Body, results);
                    }
                    break;
            }
        }

        private static void VisitSuite(SuiteSyntax suite, List<BlockRange> results)
        {
            if (suite == null) return;
            foreach (StatementSyntax s in suite.Statements) Visit(s, results);
        }

        private static void AddSuite(
            List<BlockRange> results,
            BlockKind kind,
            HelixPythonNamespace.Python.Text.SourceSpan ownerSpan,
            SuiteSyntax body)
        {
            if (body == null || body.Statements == null || body.Statements.Count == 0) return;
            int firstLine = body.Statements[0].Span.Start.Line;
            int lastLine = LastLineOf(body.Statements[body.Statements.Count - 1]);
            if (firstLine == lastLine && firstLine == ownerSpan.Start.Line) return; // single-line suite

            int indent = body.Statements[0].Span.Start.Column - 1; // 1-based → 0-based
            if (indent < 0) indent = 0;
            int parentIndent = ownerSpan.Start.Column - 1;
            if (parentIndent < 0) parentIndent = 0;

            int headerLine = firstLine - 1;
            if (headerLine < 1) headerLine = ownerSpan.Start.Line;

            results.Add(new BlockRange
            {
                Kind = kind,
                HeaderLine = headerLine,
                // Refined in Analyze() once the source text is available; without it the header
                // is assumed to occupy a single line.
                HeaderStartLine = headerLine,
                LastContentLine = lastLine,
                IndentColumn = indent,
                ParentIndentColumn = parentIndent,
            });
        }

        // The parser's Span.End for a compound statement (if/while/for/try/with/match/def/
        // class) covers only its own header, not its nested body — so when a suite's LAST
        // statement is itself compound, `stmt.Span.End.Line` under-reports (it points at the
        // nested header, not the nested body's real end). Recurse into the last statement's
        // own last-nested-suite until reaching a genuinely simple (non-compound) statement,
        // whose Span.End.Line is trustworthy even when it spans multiple physical lines.
        private static int LastLineOf(StatementSyntax s)
        {
            switch (s)
            {
                case ClassDefSyntax cd: return LastLineOfSuite(cd.Body, s.Span.End.Line);
                case FunctionDefSyntax fd: return LastLineOfSuite(fd.Body, s.Span.End.Line);
                case WhileStatement ws: return LastLineOfSuite(ws.Else ?? ws.Body, s.Span.End.Line);
                case ForStatement fs: return LastLineOfSuite(fs.Else ?? fs.Body, s.Span.End.Line);
                case IfStatement ifs:
                    if (ifs.Else != null) return LastLineOfSuite(ifs.Else, s.Span.End.Line);
                    if (ifs.Elifs.Count > 0) return LastLineOfSuite(ifs.Elifs[ifs.Elifs.Count - 1].Body, s.Span.End.Line);
                    return LastLineOfSuite(ifs.Body, s.Span.End.Line);
                case TryStatement ts:
                    if (ts.Finally != null) return LastLineOfSuite(ts.Finally, s.Span.End.Line);
                    if (ts.Else != null) return LastLineOfSuite(ts.Else, s.Span.End.Line);
                    if (ts.Handlers.Count > 0) return LastLineOfSuite(ts.Handlers[ts.Handlers.Count - 1].Body, s.Span.End.Line);
                    return LastLineOfSuite(ts.Body, s.Span.End.Line);
                case WithStatement wts: return LastLineOfSuite(wts.Body, s.Span.End.Line);
                case MatchStatement ms:
                    return ms.Cases.Count > 0
                        ? LastLineOfSuite(ms.Cases[ms.Cases.Count - 1].Body, s.Span.End.Line)
                        : s.Span.End.Line;
                default:
                    return s.Span.End.Line;
            }
        }

        private static int LastLineOfSuite(SuiteSyntax suite, int fallback)
        {
            if (suite == null || suite.Statements == null || suite.Statements.Count == 0) return fallback;
            return LastLineOf(suite.Statements[suite.Statements.Count - 1]);
        }
    }
}
