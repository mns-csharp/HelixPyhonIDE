namespace HelixPythonNamespace.Refactoring.Vectorization
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Python;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>Result of parsing a free-standing editor selection. Port of the C++ <c>ParsedSelection</c>.</summary>
    public sealed class ParsedSelection
    {
        public Module Module;
        public string CommonIndent = "";
        public string LineEnding = "\n";
        public bool HasErrors;
    }

    /// <summary>
    /// Strips the common leading indent of an editor selection so the snippet parses
    /// as a top-level module, recording what was stripped for re-indentation. Port of
    /// the C++ <c>SelectionParser</c>.
    /// </summary>
    public static class SelectionParser
    {
        public static ParsedSelection Parse(string source)
        {
            ParsedSelection r = new ParsedSelection();
            r.LineEnding = DetectLineEnding(source);
            r.CommonIndent = ComputeCommonIndent(source);

            string normalized = StripCommonIndent(source, r.CommonIndent);
            if (normalized.Length == 0 || normalized[normalized.Length - 1] != '\n')
            {
                normalized += "\n";
            }

            ParseResult pr = Python.ParseModule(normalized);
            foreach (ParseDiagnostic d in pr.Diagnostics)
            {
                if (d.Severity >= ParseSeverity.Error) { r.HasErrors = true; break; }
            }
            r.Module = CstToAst.Lower(pr.Module);
            return r;
        }

        public static string DetectLineEnding(string source)
        {
            return source.IndexOf("\r\n", System.StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }

        public static string ComputeCommonIndent(string source)
        {
            List<string> lines = SplitLines(source);
            string common = null;
            foreach (string rawIn in lines)
            {
                string raw = rawIn;
                if (raw.Length > 0 && raw[raw.Length - 1] == '\r') raw = raw.Substring(0, raw.Length - 1);
                if (IsBlankOrCommentOnly(raw)) continue;
                string ind = LeadingWhitespace(raw);
                if (common == null) { common = ind; continue; }
                int k = 0;
                while (k < common.Length && k < ind.Length && common[k] == ind[k]) k++;
                common = common.Substring(0, k);
                if (common.Length == 0) break;
            }
            return common ?? "";
        }

        public static string StripCommonIndent(string source, string indent)
        {
            if (string.IsNullOrEmpty(indent)) return source;
            StringBuilder outSb = new StringBuilder();
            List<string> lines = SplitLines(source);
            for (int li = 0; li < lines.Count; li++)
            {
                string line = lines[li];
                bool hadCr = line.Length > 0 && line[line.Length - 1] == '\r';
                if (hadCr) line = line.Substring(0, line.Length - 1);
                if (line.Length >= indent.Length && line.Substring(0, indent.Length) == indent)
                {
                    line = line.Substring(indent.Length);
                }
                if (hadCr) line += "\r";
                outSb.Append(line);
                if (li + 1 < lines.Count) outSb.Append('\n');
            }
            return outSb.ToString();
        }

        public static string ReapplyIndent(string body, string indent, string lineEnding)
        {
            if (string.IsNullOrEmpty(indent) && lineEnding == "\n") return body;
            StringBuilder outSb = new StringBuilder();
            List<string> lines = SplitLines(body);
            for (int li = 0; li < lines.Count; li++)
            {
                string line = lines[li];
                if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
                if (line.Length > 0) outSb.Append(indent);
                outSb.Append(line);
                if (li + 1 < lines.Count) outSb.Append(lineEnding);
            }
            return outSb.ToString();
        }

        private static bool IsBlankOrCommentOnly(string line)
        {
            foreach (char c in line)
            {
                if (c == ' ' || c == '\t' || c == '\r') continue;
                if (c == '#') return true;
                return false;
            }
            return true;
        }

        private static string LeadingWhitespace(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return line.Substring(0, i);
        }

        private static List<string> SplitLines(string source)
        {
            List<string> outList = new List<string>();
            StringBuilder cur = new StringBuilder();
            foreach (char c in source)
            {
                if (c == '\n') { outList.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            outList.Add(cur.ToString());
            return outList;
        }
    }
}
