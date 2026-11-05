namespace HelixPythonNamespace.Metrics
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>Per-language lexical profile driving <see cref="LanguageMetricsEngine"/>.</summary>
    public sealed class LanguageProfile
    {
        public string Name;
        public string[] LineComments = { };            // e.g. "//", "#", "--"
        public string BlockCommentOpen;                // "/*" or null
        public string BlockCommentClose;               // "*/" or null
        public bool BraceBlocks = true;                // true: C-family {…}; false: keyword…end
        public Regex FunctionDecl;                     // must capture group "name"
        public Regex ClassDecl;                        // may be null; captures "name"
        public string[] DecisionKeywords = { };        // whole-word: if, for, while, case, catch, match, when
        public string[] BooleanOps = { "&&", "||" };   // each occurrence adds a decision path
        public string[] BlockOpeners = { };            // end-based only: function, for, while, if, begin, do, let
    }

    /// <summary>
    /// FR-METR-02..07: a native, dependency-free metrics engine for the non-Python languages — LOC
    /// (code/comment/blank), function/class counts, and per-function LOC / parameter count / cyclomatic
    /// complexity / max nesting — computed lexically from source per a <see cref="LanguageProfile"/>. Produces the
    /// same <see cref="MetricsReport"/> the Python <see cref="MetricsEngine"/> does, so every language surfaces in
    /// the one Metrics panel. Complexity = 1 + decision points (branch keywords + boolean operators), the standard
    /// approximation.
    /// </summary>
    public static class LanguageMetricsEngine
    {
        public static MetricsReport Analyze(MetricsSource src, LanguageProfile p)
        {
            var report = new MetricsReport();
            if (src.Text == null || p == null) return report;
            string[] lines = src.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            var file = new FileMetric { FilePath = src.FilePath, TotalLines = lines.Length };
            ClassifyLoc(lines, p, file);

            var functions = new List<FunctionMetric>();
            var classes = new List<ClassMetric>();
            if (p.BraceBlocks) ScanBrace(src, lines, p, functions, classes);
            else ScanEnd(src, lines, p, functions, classes);

            file.FunctionCount = functions.Count;
            file.ClassCount = classes.Count;
            report.Files.Add(file);
            report.Functions.AddRange(functions);
            report.Classes.AddRange(classes);
            return report;
        }

        private static void ClassifyLoc(string[] lines, LanguageProfile p, FileMetric file)
        {
            bool inBlock = false;
            foreach (string raw in lines)
            {
                string t = raw.Trim();
                if (inBlock)
                {
                    file.CommentLines++;
                    if (p.BlockCommentClose != null && t.Contains(p.BlockCommentClose)) inBlock = false;
                    continue;
                }
                if (t.Length == 0) { file.BlankLines++; continue; }
                if (p.BlockCommentOpen != null && t.StartsWith(p.BlockCommentOpen))
                {
                    file.CommentLines++;
                    if (!(p.BlockCommentClose != null && t.IndexOf(p.BlockCommentClose, p.BlockCommentOpen.Length, StringComparison.Ordinal) >= 0)) inBlock = true;
                    continue;
                }
                bool isComment = false;
                foreach (string lc in p.LineComments) if (t.StartsWith(lc)) { isComment = true; break; }
                if (isComment) file.CommentLines++; else file.CodeLines++;
            }
        }

        // C-family: a function declaration is followed by a brace block; scan to the matching close brace.
        private static void ScanBrace(MetricsSource src, string[] lines, LanguageProfile p, List<FunctionMetric> funcs, List<ClassMetric> classes)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (p.ClassDecl != null)
                {
                    Match cm = p.ClassDecl.Match(lines[i]);
                    if (cm.Success) classes.Add(new ClassMetric { Name = cm.Groups["name"].Value, FilePath = src.FilePath, Line = i + 1 });
                }
                Match m = p.FunctionDecl != null ? p.FunctionDecl.Match(lines[i]) : Match.Empty;
                if (!m.Success) continue;
                if (IsControlKeyword(m.Groups["name"].Value)) continue; // `while (…) {` etc. are not functions

                int open = FindLineWithBrace(lines, i, '{');
                if (open < 0) continue;
                int endLine = MatchBrace(lines, open);
                if (endLine < 0) endLine = Math.Min(lines.Length - 1, open + 1);

                var fm = new FunctionMetric
                {
                    Name = m.Groups["name"].Value, FilePath = src.FilePath, Line = i + 1,
                    Loc = endLine - i + 1,
                    ParameterCount = CountParams(SpanText(lines, i, open)),
                };
                string body = SpanText(lines, open, endLine);
                fm.CyclomaticComplexity = 1 + CountDecisions(body, p);
                fm.MaxNestingDepth = MaxBraceDepth(body);
                funcs.Add(fm);
                i = endLine;
            }
        }

        // Keyword…end: a `function name(...)` opens a block closed by a matching `end`. No built-in
        // profile uses this today (the shipped profile is CUDA/C++); it stays for custom LanguageProfiles.
        private static void ScanEnd(MetricsSource src, string[] lines, LanguageProfile p, List<FunctionMetric> funcs, List<ClassMetric> classes)
        {
            var openerRx = new Regex(@"(?<![\w.])(" + string.Join("|", p.BlockOpeners) + @")(?![\w])", RegexOptions.Compiled);
            var endRx = new Regex(@"(?<![\w.])end(?![\w])", RegexOptions.Compiled);
            for (int i = 0; i < lines.Length; i++)
            {
                if (p.ClassDecl != null)
                {
                    Match cm = p.ClassDecl.Match(lines[i]);
                    if (cm.Success) classes.Add(new ClassMetric { Name = cm.Groups["name"].Value, FilePath = src.FilePath, Line = i + 1 });
                }
                Match m = p.FunctionDecl != null ? p.FunctionDecl.Match(lines[i]) : Match.Empty;
                if (!m.Success) continue;

                int depth = 0, endLine = i;
                for (int j = i; j < lines.Length; j++)
                {
                    string code = StripComment(lines[j], p);
                    depth += openerRx.Matches(code).Count;
                    depth -= endRx.Matches(code).Count;
                    if (depth <= 0) { endLine = j; break; }
                }
                var fm = new FunctionMetric
                {
                    Name = m.Groups["name"].Value, FilePath = src.FilePath, Line = i + 1,
                    Loc = endLine - i + 1,
                    ParameterCount = CountParams(lines[i]),
                };
                string body = SpanText(lines, i, endLine);
                fm.CyclomaticComplexity = 1 + CountDecisions(body, p);
                fm.MaxNestingDepth = 1;
                funcs.Add(fm);
                i = endLine;
            }
        }

        private static readonly HashSet<string> ControlKeywords = new HashSet<string>(StringComparer.Ordinal)
        { "if", "for", "foreach", "while", "switch", "catch", "return", "else", "do", "using", "lock", "fixed", "sizeof", "typeof", "nameof" };
        private static bool IsControlKeyword(string name) { return name != null && ControlKeywords.Contains(name); }

        private static int CountDecisions(string text, LanguageProfile p)
        {
            int n = 0;
            foreach (string kw in p.DecisionKeywords)
                n += Regex.Matches(text, @"(?<![\w])" + Regex.Escape(kw) + @"(?![\w])").Count;
            foreach (string op in p.BooleanOps)
            {
                int idx = 0; while ((idx = text.IndexOf(op, idx, StringComparison.Ordinal)) >= 0) { n++; idx += op.Length; }
            }
            return n;
        }

        private static int CountParams(string signature)
        {
            int open = signature.IndexOf('(');
            int close = signature.IndexOf(')', open < 0 ? 0 : open);
            if (open < 0 || close < 0 || close <= open + 1) return 0;
            string inner = signature.Substring(open + 1, close - open - 1).Trim();
            if (inner.Length == 0) return 0;
            int depth = 0, count = 1;
            foreach (char c in inner)
            {
                if (c == '(' || c == '<' || c == '[') depth++;
                else if (c == ')' || c == '>' || c == ']') depth--;
                else if (c == ',' && depth == 0) count++;
            }
            return count;
        }

        private static int MaxBraceDepth(string text)
        {
            int depth = 0, max = 0;
            foreach (char c in text) { if (c == '{') { depth++; if (depth > max) max = depth; } else if (c == '}') depth--; }
            return max;
        }

        private static int FindLineWithBrace(string[] lines, int from, char b)
        {
            for (int i = from; i < lines.Length && i <= from + 3; i++) if (lines[i].IndexOf(b) >= 0) return i;
            return -1;
        }

        private static int MatchBrace(string[] lines, int openLine)
        {
            int depth = 0;
            for (int i = openLine; i < lines.Length; i++)
            {
                foreach (char c in lines[i]) { if (c == '{') depth++; else if (c == '}') { depth--; if (depth == 0) return i; } }
            }
            return -1;
        }

        private static string SpanText(string[] lines, int a, int b)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = a; i <= b && i < lines.Length; i++) sb.Append(lines[i]).Append('\n');
            return sb.ToString();
        }

        private static string StripComment(string line, LanguageProfile p)
        {
            foreach (string lc in p.LineComments) { int idx = line.IndexOf(lc, StringComparison.Ordinal); if (idx >= 0) return line.Substring(0, idx); }
            return line;
        }

        // ---- built-in profiles ----
        public static LanguageProfile ForExtension(string ext)
        {
            if (ext == null) return null;
            switch (ext.ToLowerInvariant())
            {
                case ".cu": case ".cuh": case ".cpp": case ".h": case ".hpp": return Cuda;
                default: return null;
            }
        }

        public static readonly LanguageProfile Cuda = new LanguageProfile
        {
            Name = "CUDA", LineComments = new[] { "//" }, BlockCommentOpen = "/*", BlockCommentClose = "*/",
            FunctionDecl = new Regex(@"(__global__|__device__|__host__|void|int|float|double|auto|bool|char|unsigned|static|inline|\s)+\s+(?<name>[A-Za-z_]\w*)\s*\([^;]*\)\s*\{?\s*$", RegexOptions.Compiled),
            ClassDecl = new Regex(@"\b(class|struct)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled),
            DecisionKeywords = new[] { "if", "for", "while", "case", "catch", "?" },
        };
    }
}
