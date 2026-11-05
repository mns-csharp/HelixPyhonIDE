using System.IO;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Helpers for building a Call Hierarchy (incoming calls). The key operation is resolving the
    /// FUNCTION that encloses a given source line — i.e. turning a reference site ("where X is
    /// mentioned") into a caller ("which function calls X").
    /// </summary>
    public static class CallHierarchyService
    {
        /// <summary>
        /// The qualified name of the innermost function/method whose body contains <paramref name="line"/>
        /// (1-based), e.g. <c>foo</c>, <c>Class.method</c>, or <c>outer.inner</c> for a nested def.
        /// Returns null when the line is at module scope (a module-level call).
        /// </summary>
        public static string FindEnclosingFunction(string source, int line)
        {
            if (string.IsNullOrEmpty(source) || line < 1) return null;
            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch { return null; }
            if (module == null) return null;
            return Search(module.Statements, line, null);
        }

        /// <summary>File-reading overload used by the call-hierarchy walk over a solution.</summary>
        public static string FindEnclosingFunctionInFile(string filePath, int line)
        {
            string source;
            try { source = File.ReadAllText(filePath); }
            catch { return null; }
            return FindEnclosingFunction(source, line);
        }

        // Walk statements looking for the deepest def/class whose span brackets `line`. `prefix` carries
        // the enclosing class/function qualifier (e.g. "Class" or "outer").
        private static string Search(System.Collections.Generic.IReadOnlyList<StatementSyntax> statements, int line, string prefix)
        {
            if (statements == null) return prefix;
            foreach (StatementSyntax s in statements)
            {
                if (s is FunctionDefSyntax fd && Contains(fd.Span, line))
                {
                    string here = prefix == null ? fd.Name : prefix + "." + fd.Name;
                    // A nested def inside this one wins (innermost); otherwise this function is it.
                    string deeper = fd.Body != null ? Search(fd.Body.Statements, line, here) : null;
                    return deeper ?? here;
                }
                if (s is ClassDefSyntax cd && Contains(cd.Span, line))
                {
                    string here = prefix == null ? cd.Name : prefix + "." + cd.Name;
                    string deeper = cd.Body != null ? Search(cd.Body.Statements, line, here) : null;
                    // A class isn't a "caller"; if the line isn't inside one of its methods, treat it
                    // as module-scope-ish and return the class-qualified name only when a method matched.
                    return deeper;
                }
            }
            return null;
        }

        private static bool Contains(SourceSpan span, int line)
        {
            return line >= span.Start.Line && line <= span.End.Line;
        }
    }
}
