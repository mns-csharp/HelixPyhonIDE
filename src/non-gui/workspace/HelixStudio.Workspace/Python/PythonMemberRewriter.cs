using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Renames a class member (method/property/constructor/field) WITHIN THE OWNING CLASS BODY
    /// ONLY. Deliberately narrower than <see cref="AstIdentifierRewriter"/>, which is built for
    /// module-scope names (classes/functions) and explicitly never touches attribute-access
    /// positions (<c>obj.name</c>) — a member name is far more collision-prone than a class or
    /// function name, since many unrelated classes can have a method or field of the same name. A
    /// single routine handles every member kind: it rewrites a <c>def &lt;old&gt;</c> declaration
    /// directly on the class (methods/properties/constructors), any bare class-level assignment
    /// target named <c>&lt;old&gt;</c> (a field declaration), and every <c>self.&lt;old&gt;</c>
    /// occurrence anywhere in the class body (reads, writes, and calls, including inside other
    /// methods) — whichever of those actually exist for the member being renamed.
    /// </summary>
    public static class PythonMemberRewriter
    {
        public readonly struct RewriteResult
        {
            public readonly bool Success;
            public readonly string ErrorMessage;
            public readonly string NewSource;
            public readonly int Count;

            public RewriteResult(bool success, string errorMessage, string newSource, int count)
            {
                Success = success;
                ErrorMessage = errorMessage;
                NewSource = newSource;
                Count = count;
            }

            public static RewriteResult Fail(string message) => new RewriteResult(false, message, null, 0);
        }

        public static RewriteResult Rename(string source, string className, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(oldName))
                return RewriteResult.Fail("no class or member name given");
            source = source ?? string.Empty;
            if (string.IsNullOrEmpty(newName) || newName == oldName)
                return new RewriteResult(true, null, source, 0);

            ModuleSyntax module;
            IReadOnlyList<Token> tokens;
            try
            {
                module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module;
                tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source);
            }
            catch (Exception ex) { return RewriteResult.Fail("could not parse source: " + ex.Message); }
            if (module == null) return RewriteResult.Fail("could not parse source");

            ClassDefSyntax cls = null;
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is ClassDefSyntax c && c.Name == className) { cls = c; break; }
            }
            if (cls == null || cls.Body == null)
                return RewriteResult.Fail("'" + className + "' is not a top-level class in this file");

            List<(int start, int end)> hits = new List<(int, int)>();

            // 1. `def <old>` declaration directly on this class.
            foreach (StatementSyntax s in cls.Body.Statements)
            {
                if (s is FunctionDefSyntax fn && fn.Name == oldName)
                    SpliceNameAfterKeyword(tokens, fn.Span, TokenKind.KwDef, hits);
            }

            // 2. Every bare class-level assignment target named <old> — a field declaration.
            foreach (StatementSyntax s in cls.Body.Statements)
            {
                if (s is AssignmentStatement asn)
                {
                    foreach (ExpressionSyntax t in asn.Targets)
                        if (t is NameExpression ne && ne.Name == oldName)
                            hits.Add((ne.Span.Start.Offset, ne.Span.End.Offset));
                }
            }

            // 3. Every `self.<old>` occurrence anywhere within the class body — reads, writes, and
            //    calls, including inside other methods' bodies. Uses AstIdentifierRewriter's own
            //    traversal (not a plain SyntaxNode.Descendants()) because several statement types
            //    under-report Children and a naive walk would silently miss occurrences.
            foreach (SyntaxNode node in AstIdentifierRewriter.EnumerateAll(cls.Body))
            {
                if (node is AttributeExpression ae && ae.Name == oldName &&
                    ae.Target is NameExpression baseName && baseName.Name == "self")
                {
                    (int, int)? span = FindTrailingNameSpan(tokens, ae.Span);
                    if (span.HasValue) hits.Add(span.Value);
                }
            }

            if (hits.Count == 0) return new RewriteResult(true, null, source, 0);

            hits.Sort((a, b) => a.start.CompareTo(b.start));
            StringBuilder sb = new StringBuilder(source.Length + hits.Count * Math.Max(0, newName.Length - oldName.Length));
            int cursor = 0;
            int applied = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue;              // overlap / duplicate
                if (h.start < 0 || h.end > source.Length || h.end <= h.start) continue;
                sb.Append(source, cursor, h.start - cursor);
                sb.Append(newName);
                cursor = h.end;
                applied++;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);

            return new RewriteResult(true, null, sb.ToString(), applied);
        }

        // Within stmtSpan, the Name token immediately following `keyword` — e.g. the function name
        // right after `def`. Mirrors AstIdentifierRewriter.SpliceDeclarationNameSpan.
        private static void SpliceNameAfterKeyword(
            IReadOnlyList<Token> tokens, SourceSpan stmtSpan, TokenKind keyword, List<(int, int)> hits)
        {
            int startOff = stmtSpan.Start.Offset;
            int endOff = stmtSpan.End.Offset;
            bool seenKeyword = false;
            foreach (Token t in tokens)
            {
                if (t == null || t.IsTrivia) continue;
                int tStart = t.Span.Start.Offset;
                if (tStart < startOff) continue;
                if (tStart >= endOff) break;

                if (!seenKeyword)
                {
                    if (t.Kind == keyword) seenKeyword = true;
                    continue;
                }
                if (t.Kind == TokenKind.Name)
                {
                    hits.Add((t.Span.Start.Offset, t.Span.End.Offset));
                    return;
                }
            }
        }

        // Within exprSpan (an AttributeExpression's full span, e.g. "self.name"), the Name token
        // right after the LAST Dot token — the trailing `.name` part, as opposed to the target
        // before the dot. Correct for the simple `self.name` shape this rewriter matches (Target is
        // always a bare NameExpression, never a longer chain), so there is exactly one dot.
        private static (int, int)? FindTrailingNameSpan(IReadOnlyList<Token> tokens, SourceSpan exprSpan)
        {
            int startOff = exprSpan.Start.Offset;
            int endOff = exprSpan.End.Offset;
            Token lastDot = null;
            foreach (Token t in tokens)
            {
                if (t == null || t.IsTrivia) continue;
                int tStart = t.Span.Start.Offset;
                if (tStart < startOff) continue;
                if (tStart >= endOff) break;
                if (t.Kind == TokenKind.Dot) lastDot = t;
            }
            if (lastDot == null) return null;

            bool afterDot = false;
            foreach (Token t in tokens)
            {
                if (t == null || t.IsTrivia) continue;
                if (!afterDot)
                {
                    if (ReferenceEquals(t, lastDot)) afterDot = true;
                    continue;
                }
                if (t.Kind == TokenKind.Name) return (t.Span.Start.Offset, t.Span.End.Offset);
                break;
            }
            return null;
        }
    }
}
