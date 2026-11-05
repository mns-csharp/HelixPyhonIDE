namespace HelixPythonNamespace.Lint.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>L001: Line too long (&gt;100 chars).</summary>
    public sealed class LineTooLongRule : ILintRule
    {
        private const int MaxLineLength = 100;
        public string Code { get { return "L001"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.ForEachLine(sourceText, (lineNo, line) =>
            {
                if (line.Length > MaxLineLength)
                {
                    output.Add(new LintFinding
                    {
                        Code = Code,
                        Severity = AnalysisSeverity.Info,
                        Message = "Line too long (" + line.Length + " > " + MaxLineLength + " chars).",
                        Span = LintHelpers.SpanForLine(lineNo, MaxLineLength + 1, line.Length + 1),
                    });
                }
            });
        }
    }

    /// <summary>L002: Trailing whitespace.</summary>
    public sealed class TrailingWhitespaceRule : ILintRule
    {
        public string Code { get { return "L002"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.ForEachLine(sourceText, (lineNo, line) =>
            {
                if (line.Length == 0)
                {
                    return;
                }
                int end = line.Length;
                while (end > 0 && (line[end - 1] == ' ' || line[end - 1] == '\t'))
                {
                    end--;
                }
                if (end < line.Length)
                {
                    output.Add(new LintFinding
                    {
                        Code = Code,
                        Severity = AnalysisSeverity.Info,
                        Message = "Trailing whitespace.",
                        Span = LintHelpers.SpanForLine(lineNo, end + 1, line.Length + 1),
                    });
                }
            });
        }
    }

    /// <summary>L003: Mixed tabs and spaces in leading indent.</summary>
    public sealed class MixedIndentRule : ILintRule
    {
        public string Code { get { return "L003"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.ForEachLine(sourceText, (lineNo, line) =>
            {
                bool sawTab = false, sawSpace = false;
                int i = 0;
                for (; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c == ' ') sawSpace = true;
                    else if (c == '\t') sawTab = true;
                    else break;
                }
                if (sawTab && sawSpace)
                {
                    output.Add(new LintFinding
                    {
                        Code = Code,
                        Severity = AnalysisSeverity.Warning,
                        Message = "Mixed tabs and spaces in indent.",
                        Span = LintHelpers.SpanForLine(lineNo, 1, i + 1),
                    });
                }
            });
        }
    }

    /// <summary>L004: Multiple statements on one line (semicolon-separated).</summary>
    public sealed class MultipleStatementsRule : ILintRule
    {
        public string Code { get { return "L004"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            // Triple-quoted string state persists across lines. We also track
            // single-line strings, '#' comments, and escapes within a line.
            int tripleKind = 0; // 0 = none; 1 = """ ; 2 = '''
            int[] tripleRef = { tripleKind };
            LintHelpers.ForEachLine(sourceText, (lineNo, line) =>
            {
                bool inSingle = false, inDouble = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];

                    if (tripleRef[0] != 0)
                    {
                        char q = tripleRef[0] == 1 ? '"' : '\'';
                        if (i + 2 < line.Length && line[i] == q && line[i + 1] == q && line[i + 2] == q)
                        {
                            tripleRef[0] = 0;
                            i += 2;
                        }
                        continue;
                    }

                    if ((inSingle || inDouble) && c == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        continue;
                    }

                    if (!inSingle && !inDouble && i + 2 < line.Length && (c == '"' || c == '\'')
                        && line[i + 1] == c && line[i + 2] == c)
                    {
                        tripleRef[0] = c == '"' ? 1 : 2;
                        i += 2;
                        continue;
                    }

                    if (!inDouble && c == '\'') inSingle = !inSingle;
                    else if (!inSingle && c == '"') inDouble = !inDouble;
                    else if (!inSingle && !inDouble && c == '#') break; // comment
                    else if (!inSingle && !inDouble && c == ';')
                    {
                        int j = i + 1;
                        while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
                        if (j < line.Length && line[j] != '#')
                        {
                            output.Add(new LintFinding
                            {
                                Code = Code,
                                Severity = AnalysisSeverity.Warning,
                                Message = "Multiple statements on one line (use separate lines).",
                                Span = LintHelpers.SpanForLine(lineNo, i + 1, i + 2),
                            });
                            return;
                        }
                    }
                }
            });
        }
    }
}
