namespace HelixPythonNamespace.Lint
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;
    using HelixPythonNamespace.Python.Text;

    /// <summary>Shared traversal + span helpers used by the lint rules.</summary>
    internal static class LintHelpers
    {
        /// <summary>Invoke <paramref name="cb"/> on <paramref name="node"/> and every descendant.</summary>
        public static void WalkAll(SyntaxNode node, Action<SyntaxNode> cb)
        {
            if (node == null)
            {
                return;
            }
            cb(node);
            foreach (SyntaxNode d in node.Descendants())
            {
                cb(d);
            }
        }

        /// <summary>
        /// Statement-only walk that descends into the bodies of compound statements
        /// (def/class/if/while/for/try/with) — mirrors the C++ WalkStatements.
        /// </summary>
        public static void WalkStatements(IReadOnlyList<StatementSyntax> stmts, Action<StatementSyntax> cb)
        {
            if (stmts == null)
            {
                return;
            }
            foreach (StatementSyntax stmt in stmts)
            {
                cb(stmt);
                if (stmt is FunctionDefSyntax fn)
                {
                    if (fn.Body != null) WalkStatements(fn.Body.Statements, cb);
                }
                else if (stmt is ClassDefSyntax cls)
                {
                    if (cls.Body != null) WalkStatements(cls.Body.Statements, cb);
                }
                else if (stmt is IfStatement iff)
                {
                    if (iff.Body != null) WalkStatements(iff.Body.Statements, cb);
                    foreach (IfStatement elf in iff.Elifs)
                    {
                        if (elf.Body != null) WalkStatements(elf.Body.Statements, cb);
                    }
                    if (iff.Else != null) WalkStatements(iff.Else.Statements, cb);
                }
                else if (stmt is WhileStatement w)
                {
                    if (w.Body != null) WalkStatements(w.Body.Statements, cb);
                    if (w.Else != null) WalkStatements(w.Else.Statements, cb);
                }
                else if (stmt is ForStatement fo)
                {
                    if (fo.Body != null) WalkStatements(fo.Body.Statements, cb);
                    if (fo.Else != null) WalkStatements(fo.Else.Statements, cb);
                }
                else if (stmt is TryStatement tr)
                {
                    if (tr.Body != null) WalkStatements(tr.Body.Statements, cb);
                    foreach (ExceptHandler h in tr.Handlers)
                    {
                        if (h.Body != null) WalkStatements(h.Body.Statements, cb);
                    }
                    if (tr.Else != null) WalkStatements(tr.Else.Statements, cb);
                    if (tr.Finally != null) WalkStatements(tr.Finally.Statements, cb);
                }
                else if (stmt is WithStatement wi)
                {
                    if (wi.Body != null) WalkStatements(wi.Body.Statements, cb);
                }
            }
        }

        /// <summary>
        /// Iterate <paramref name="text"/> line by line, invoking
        /// <c>cb(lineNumber /*1-based*/, lineText)</c>. Lines exclude the trailing
        /// newline; a final empty line after a trailing '\n' is skipped.
        /// </summary>
        public static void ForEachLine(string text, Action<int, string> cb)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            int lineNo = 1;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    int end = i;
                    if (end > start && text[end - 1] == '\r') end--;
                    cb(lineNo, text.Substring(start, end - start));
                    lineNo++;
                    start = i + 1;
                }
            }
            if (start < text.Length)
            {
                int end = text.Length;
                if (end > start && text[end - 1] == '\r') end--;
                cb(lineNo, text.Substring(start, end - start));
            }
        }

        public static SourceSpan SpanForLine(int lineNo, int startCol, int endCol)
        {
            int sCol = Math.Max(1, startCol);
            int eCol = Math.Max(sCol + 1, endCol);
            return new SourceSpan(
                new SourcePosition(0, lineNo, sCol),
                new SourcePosition(0, lineNo, eCol));
        }
    }
}
