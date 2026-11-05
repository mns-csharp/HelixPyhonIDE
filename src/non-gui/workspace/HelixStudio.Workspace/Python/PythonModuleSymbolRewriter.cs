using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Binding-aware rewrite for a module-exported variable. It changes the declaration and bare
    /// references only in the declaring module or in modules that import that exact export, and it
    /// changes qualified uses through aliases bound to that exact module (for example
    /// <c>from . import invariants as _inv</c> followed by <c>_inv.GAMMA</c>).
    /// </summary>
    public static class PythonModuleSymbolRewriter
    {
        public static AstIdentifierRewriter.RewriteResult Rewrite(string source,
            string currentPackage, string declaringModule, string oldName, string newName,
            bool isDeclaringModule)
        {
            source = source ?? string.Empty;
            ModuleSyntax module;
            IReadOnlyList<Token> tokens;
            try
            {
                module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module;
                tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source);
            }
            catch { return new AstIdentifierRewriter.RewriteResult(source, 0); }

            bool importsBareName = false;
            var moduleAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (StatementSyntax statement in module.Statements)
            {
                FromImportStatement fromImport = statement as FromImportStatement;
                if (fromImport != null)
                {
                    string from = ResolveImportedModule(fromImport, currentPackage);
                    if (from == null) continue;
                    if (fromImport.IsStar && from == declaringModule) importsBareName = true;
                    foreach (KeyValuePair<string, string> imported in fromImport.Names)
                    {
                        string importedFqn = string.IsNullOrEmpty(from)
                            ? imported.Key : from + "." + imported.Key;
                        if (from == declaringModule && imported.Key == oldName) importsBareName = true;
                        if (importedFqn == declaringModule)
                            moduleAliases.Add(imported.Value ?? imported.Key);
                    }
                    continue;
                }

                ImportStatement import = statement as ImportStatement;
                if (import == null) continue;
                foreach (ImportStatement.ImportedName imported in import.Names)
                {
                    if (imported.Name == null || imported.Name.AsString != declaringModule) continue;
                    moduleAliases.Add(imported.Alias ?? imported.Name.AsString);
                }
            }

            int count = 0;
            string rewritten = RewriteQualifiedAttributes(source, module, tokens, moduleAliases,
                oldName, newName, out int qualifiedCount);
            count += qualifiedCount;
            if (isDeclaringModule || importsBareName)
            {
                AstIdentifierRewriter.RewriteResult bare = AstIdentifierRewriter.Rewrite(rewritten, oldName, newName);
                rewritten = bare.Text;
                count += bare.Count;
            }
            return new AstIdentifierRewriter.RewriteResult(rewritten, count);
        }

        private static string RewriteQualifiedAttributes(string source, ModuleSyntax module,
            IReadOnlyList<Token> tokens, HashSet<string> aliases, string oldName, string newName,
            out int count)
        {
            count = 0;
            if (aliases.Count == 0) return source;
            var hits = new List<(int start, int end)>();
            foreach (SyntaxNode node in AstIdentifierRewriter.EnumerateAll(module))
            {
                AttributeExpression attribute = node as AttributeExpression;
                if (attribute == null || attribute.Name != oldName) continue;
                string receiver = Flatten(attribute.Target);
                if (!aliases.Contains(receiver)) continue;
                (int start, int end)? span = TrailingNameSpan(tokens, attribute.Span);
                if (span.HasValue) hits.Add(span.Value);
            }
            if (hits.Count == 0) return source;
            hits.Sort((a, b) => a.start.CompareTo(b.start));
            var builder = new StringBuilder(source.Length);
            int cursor = 0;
            foreach (var hit in hits)
            {
                if (hit.start < cursor) continue;
                builder.Append(source, cursor, hit.start - cursor);
                builder.Append(newName);
                cursor = hit.end;
                count++;
            }
            builder.Append(source, cursor, source.Length - cursor);
            return builder.ToString();
        }

        private static string Flatten(ExpressionSyntax expression)
        {
            NameExpression name = expression as NameExpression;
            if (name != null) return name.Name;
            AttributeExpression attribute = expression as AttributeExpression;
            if (attribute == null) return null;
            string target = Flatten(attribute.Target);
            return string.IsNullOrEmpty(target) ? null : target + "." + attribute.Name;
        }

        private static (int start, int end)? TrailingNameSpan(IReadOnlyList<Token> tokens, SourceSpan span)
        {
            Token candidate = null;
            foreach (Token token in tokens)
            {
                if (token == null || token.IsTrivia || token.Kind != TokenKind.Name) continue;
                if (token.Span.Start.Offset < span.Start.Offset) continue;
                if (token.Span.End.Offset > span.End.Offset) break;
                candidate = token;
            }
            return candidate == null ? ((int, int)?)null
                : (candidate.Span.Start.Offset, candidate.Span.End.Offset);
        }

        private static string ResolveImportedModule(FromImportStatement import, string package)
        {
            string tail = import.Module != null ? import.Module.AsString : string.Empty;
            if (import.RelativeDots <= 0) return tail;
            string anchor = package ?? string.Empty;
            for (int i = 1; i < import.RelativeDots; i++)
            {
                int dot = anchor.LastIndexOf('.');
                if (string.IsNullOrEmpty(anchor)) return null;
                anchor = dot < 0 ? string.Empty : anchor.Substring(0, dot);
            }
            if (string.IsNullOrEmpty(anchor)) return tail;
            return string.IsNullOrEmpty(tail) ? anchor : anchor + "." + tail;
        }
    }
}
