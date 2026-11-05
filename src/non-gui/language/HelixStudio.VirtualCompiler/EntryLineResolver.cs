using System.Collections.Generic;
using Ast = HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler
{
    /// <summary>
    /// Resolves the first executable source line of a Python script — the synthetic stop
    /// point for "stop on entry". debugpy honors <c>stopOnEntry</c> only for <c>launch</c>,
    /// NOT the <c>attach</c> the engine uses (<c>--wait-for-client</c>), so both the CLI
    /// (<c>debug --stop-on-entry</c>) and the GUI (Debug → Break on Entry) synthesize an
    /// entry breakpoint on this line instead of relying on the ignored adapter flag.
    /// Skips a leading module docstring (a bare string-literal expression statement). Returns
    /// 0 when the script can't be parsed or has no executable statement, so the caller can
    /// fall back to the adapter flag.
    /// </summary>
    public static class EntryLineResolver
    {
        public static int FindEntryLine(string script)
        {
            if (string.IsNullOrEmpty(script)) return 0;
            try
            {
                List<WorkspaceFile> files = new List<WorkspaceFile>
                {
                    new WorkspaceFile(script, PythonModuleFqn.Derive(script))
                };
                IReadOnlyList<CompileFileResult> results = VirtualCompiler.CompileSolution(files);
                foreach (CompileFileResult fr in results)
                {
                    Ast.Module mod = fr.Result.AstModule as Ast.Module;
                    if (mod == null) continue;
                    foreach (Ast.Stmt s in mod.Body)
                    {
                        Ast.ExprStmt es = s as Ast.ExprStmt;
                        if (es != null && es.Value is Ast.Constant && ((Ast.Constant)es.Value).Value is string)
                            continue; // skip the module docstring
                        return s.Span.Start.Line;
                    }
                }
            }
            catch { }
            return 0;
        }
    }
}
