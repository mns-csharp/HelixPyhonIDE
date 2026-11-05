using System;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// "Extract Variable" (a.k.a. Introduce Local) refactoring: lifts a selected expression onto a new
    /// assignment statement inserted immediately above the current statement — with the statement's own
    /// indentation — and replaces the selected occurrence with the new variable name.
    ///
    /// This operates on the common single-line-expression case (the selection lies on one line and does
    /// not span the statement's leading indentation). It is a pure text transform so it is unit-testable
    /// without an editor.
    /// </summary>
    public static class ExtractVariableService
    {
        public struct Result
        {
            public bool Ok;
            public string NewText;
            /// <summary>Offset where the introduced variable name begins in the assignment line (for caret placement).</summary>
            public int VariableOffset;
            public string Error;
        }

        public static Result Extract(string source, int selStart, int selEnd, string variableName)
        {
            if (source == null) return Fail("no source");
            if (selStart < 0 || selEnd > source.Length || selEnd <= selStart) return Fail("no expression selected");
            if (string.IsNullOrWhiteSpace(variableName)) return Fail("no variable name");
            variableName = variableName.Trim();

            string expr = source.Substring(selStart, selEnd - selStart).Trim();
            if (expr.Length == 0) return Fail("selection is blank");
            // Re-anchor to the trimmed expression's real bounds (so leading/trailing spaces in the
            // selection don't end up in the assignment or the replacement).
            int trimStart = selStart;
            while (trimStart < selEnd && char.IsWhiteSpace(source[trimStart])) trimStart++;
            int trimEnd = selEnd;
            while (trimEnd > trimStart && char.IsWhiteSpace(source[trimEnd - 1])) trimEnd--;

            // Line start of the selection, and the statement's indentation (leading whitespace).
            int lineStart = trimStart;
            while (lineStart > 0 && source[lineStart - 1] != '\n') lineStart--;
            // A selection that dips into the leading indentation isn't a clean expression extraction.
            if (trimStart < FirstNonWhitespace(source, lineStart)) return Fail("select an expression, not indentation");

            string indent = source.Substring(lineStart, FirstNonWhitespace(source, lineStart) - lineStart);
            string newline = DetectNewline(source);

            string assignment = indent + variableName + " = " + expr + newline;

            // prefix (up to the statement line) + assignment line + statement-with-expr-replaced.
            string prefix = source.Substring(0, lineStart);
            string stmtBeforeExpr = source.Substring(lineStart, trimStart - lineStart);
            string after = source.Substring(trimEnd);

            string newText = prefix + assignment + stmtBeforeExpr + variableName + after;
            int variableOffset = prefix.Length + indent.Length;

            return new Result { Ok = true, NewText = newText, VariableOffset = variableOffset };
        }

        /// <summary>
        /// "Extract Constant": like <see cref="Extract"/>, but hoists the selected expression to a
        /// MODULE-LEVEL constant inserted after the module docstring and any leading imports, and
        /// replaces the selection with the constant name (conventionally UPPER_CASE).
        /// </summary>
        public static Result ExtractConstant(string source, int selStart, int selEnd, string constName)
        {
            if (source == null) return Fail("no source");
            if (selStart < 0 || selEnd > source.Length || selEnd <= selStart) return Fail("no expression selected");
            if (string.IsNullOrWhiteSpace(constName)) return Fail("no constant name");
            constName = constName.Trim();

            int trimStart = selStart;
            while (trimStart < selEnd && char.IsWhiteSpace(source[trimStart])) trimStart++;
            int trimEnd = selEnd;
            while (trimEnd > trimStart && char.IsWhiteSpace(source[trimEnd - 1])) trimEnd--;
            if (trimEnd <= trimStart) return Fail("selection is blank");
            string expr = source.Substring(trimStart, trimEnd - trimStart);

            int insertOffset = FindModuleConstantInsertOffset(source);
            // The constant must be introduced ABOVE its use. If the selection somehow sits before the
            // computed insertion point, fall back to the selection's own line start.
            if (insertOffset > trimStart)
            {
                insertOffset = trimStart;
                while (insertOffset > 0 && source[insertOffset - 1] != '\n') insertOffset--;
            }

            string newline = DetectNewline(source);
            string constLine = constName + " = " + expr + newline;
            string part1 = source.Substring(0, insertOffset);
            string part2 = source.Substring(insertOffset, trimStart - insertOffset);
            string part4 = source.Substring(trimEnd);
            string newText = part1 + constLine + part2 + constName + part4;

            return new Result { Ok = true, NewText = newText, VariableOffset = insertOffset };
        }

        // Offset (column 0) where a module-level constant should be inserted: after a leading module
        // docstring and any run of top-level import / from-import statements, i.e. just before the first
        // "real" statement. Falls back to 0 on a parse failure and to end-of-file for an all-imports file.
        private static int FindModuleConstantInsertOffset(string source)
        {
            try
            {
                ModuleSyntax module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module;
                if (module == null || module.Statements == null) return 0;
                int idx = 0;
                foreach (StatementSyntax s in module.Statements)
                {
                    // Leading docstring: the very first statement is a bare string expression.
                    if (idx == 0 && s is ExpressionStatement && IsStringLiteralAt(source, s.Span.Start.Offset))
                    { idx++; continue; }
                    if (s is ImportStatement || s is FromImportStatement) { idx++; continue; }

                    int off = s.Span.Start.Offset;
                    if (off < 0) off = 0; if (off > source.Length) off = source.Length;
                    while (off > 0 && source[off - 1] != '\n') off--;   // back up to column 0
                    return off;
                }
                return source.Length; // only a docstring / imports — append at end
            }
            catch { return 0; }
        }

        private static bool IsStringLiteralAt(string source, int offset)
        {
            if (offset < 0 || offset >= source.Length) return false;
            char c = source[offset];
            return c == '"' || c == '\'';
        }

        private static int FirstNonWhitespace(string s, int from)
        {
            int i = from;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return i;
        }

        private static string DetectNewline(string s)
        {
            int nl = s.IndexOf('\n');
            if (nl > 0 && s[nl - 1] == '\r') return "\r\n";
            return "\n";
        }

        private static Result Fail(string why) { return new Result { Ok = false, Error = why }; }
    }
}
