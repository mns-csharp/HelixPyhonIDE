using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;

namespace HelixPythonNamespace.VirtualCompiler
{
    /// <summary>
    /// Aggregate result of a virtual-compile run. Holds diagnostics plus the AST
    /// (if lowering succeeded) so callers can feed downstream passes.
    /// </summary>
    public sealed class CompileResult
    {
        public Ast.Mod AstModule { get; }
        public IReadOnlyList<CompileDiagnostic> Diagnostics { get; }
        public DateTime CompiledAt { get; }

        public CompileResult(Ast.Mod astModule, IReadOnlyList<CompileDiagnostic> diagnostics)
        {
            AstModule = astModule;
            Diagnostics = diagnostics ?? Array.Empty<CompileDiagnostic>();
            CompiledAt = DateTime.UtcNow;
        }

        public bool HasErrors
        {
            get
            {
                foreach (CompileDiagnostic d in Diagnostics)
                {
                    if (d.Severity >= ParseSeverity.Error) return true;
                }
                return false;
            }
        }

        public int ErrorCount
        {
            get
            {
                int n = 0;
                foreach (CompileDiagnostic d in Diagnostics)
                {
                    if (d.Severity >= ParseSeverity.Error) n++;
                }
                return n;
            }
        }

        public int WarningCount
        {
            get
            {
                int n = 0;
                foreach (CompileDiagnostic d in Diagnostics)
                {
                    if (d.Severity == ParseSeverity.Warning) n++;
                }
                return n;
            }
        }
    }
}
