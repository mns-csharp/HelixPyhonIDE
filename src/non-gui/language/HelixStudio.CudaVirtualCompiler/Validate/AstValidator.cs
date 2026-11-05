namespace HelixPythonNamespace.CudaVirtualCompiler.Validate
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Structural AST validation that does not need the full CUDA dataflow analyses:
    /// kernel return-type, and break/continue placement. Produces CU2xxx/CU5030
    /// diagnostics. Port of the C++ helix::cuda_vcompiler::AstValidator
    /// (cuda_vcompiler/validate/ast_validator.{h,cpp}).
    /// </summary>
    public sealed class AstValidator
    {
        private readonly string filePath_;
        private readonly List<ParseDiagnostic> diagnostics_;

        public AstValidator(string filePath)
        {
            filePath_ = filePath ?? string.Empty;
            diagnostics_ = new List<ParseDiagnostic>();
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics { get { return diagnostics_; } }

        public void Validate(TranslationUnit unit)
        {
            IReadOnlyList<Decl> decls = unit.Decls;
            for (int i = 0; i < decls.Count; ++i) ValidateDecl(decls[i]);
        }

        private void Report(string code, ParseSeverity sev, SourceSpan span, string message)
        {
            diagnostics_.Add(new ParseDiagnostic(code, sev, span, message));
        }

        private void ValidateDecl(Decl decl)
        {
            if (decl == null) return;
            FunctionDecl fn = decl as FunctionDecl;
            if (fn != null)
            {
                ValidateFunction(fn);
                return;
            }
            NamespaceDecl ns = decl as NamespaceDecl;
            if (ns != null)
            {
                IReadOnlyList<Decl> nsDecls = ns.Decls;
                for (int i = 0; i < nsDecls.Count; ++i) ValidateDecl(nsDecls[i]);
                return;
            }
            RecordDecl rec = decl as RecordDecl;
            if (rec != null)
            {
                IReadOnlyList<Decl> members = rec.Members;
                for (int i = 0; i < members.Count; ++i) ValidateDecl(members[i]);
                return;
            }
            TemplateDecl tmpl = decl as TemplateDecl;
            if (tmpl != null)
            {
                ValidateDecl(tmpl.Inner);
                return;
            }
        }

        private void ValidateFunction(FunctionDecl fn)
        {
            if (fn.IsKernel && !fn.ReturnType.IsVoid())
            {
                Report(CudaDiagnosticCodes.QualKernelBadReturnType, ParseSeverity.Error,
                       fn.Span,
                       "__global__ kernel '" + fn.Name + "' must return void (declared '" +
                           fn.ReturnType.ToString() + "')");
            }
            if (fn.IsKernel)
            {
                // Kernel arguments are copied host->device by value; a reference
                // parameter would smuggle a host address into device code.
                for (int i = 0; i < fn.Params.Count; ++i)
                {
                    Parameter p = fn.Params[i];
                    if (p.Type != null && p.Type.IsReference)
                    {
                        Report(CudaDiagnosticCodes.QualKernelBadParam, ParseSeverity.Error,
                               p.Span,
                               "__global__ kernel parameter '" + p.Name + "' cannot be a " +
                               "reference ('" + p.Type.ToString() + "'): kernel arguments are " +
                               "copied by value across the host/device boundary");
                    }
                }
            }
            if (fn.HasBody)
            {
                WalkStmt(fn.Body, 0, 0);
            }
        }

        private void WalkStmt(Stmt stmt, int loopDepth, int switchDepth)
        {
            if (stmt == null) return;

            CompoundStmt c = stmt as CompoundStmt;
            if (c != null)
            {
                IReadOnlyList<Stmt> body = c.Body;
                for (int i = 0; i < body.Count; ++i) WalkStmt(body[i], loopDepth, switchDepth);
                return;
            }
            IfStmt ifs = stmt as IfStmt;
            if (ifs != null)
            {
                WalkStmt(ifs.Then, loopDepth, switchDepth);
                WalkStmt(ifs.Else, loopDepth, switchDepth);
                return;
            }
            ForStmt f = stmt as ForStmt;
            if (f != null)
            {
                WalkStmt(f.Body, loopDepth + 1, switchDepth);
                return;
            }
            WhileStmt w = stmt as WhileStmt;
            if (w != null)
            {
                WalkStmt(w.Body, loopDepth + 1, switchDepth);
                return;
            }
            DoStmt dw = stmt as DoStmt;
            if (dw != null)
            {
                WalkStmt(dw.Body, loopDepth + 1, switchDepth);
                return;
            }
            SwitchStmt sw = stmt as SwitchStmt;
            if (sw != null)
            {
                WalkStmt(sw.Body, loopDepth, switchDepth + 1);
                return;
            }
            if (stmt is BreakStmt)
            {
                if (loopDepth == 0 && switchDepth == 0)
                {
                    Report(CudaDiagnosticCodes.SemBreakOutsideLoop, ParseSeverity.Error,
                           stmt.Span, "'break' outside of a loop or switch");
                }
                return;
            }
            if (stmt is ContinueStmt)
            {
                if (loopDepth == 0)
                {
                    Report(CudaDiagnosticCodes.SemContinueOutsideLoop, ParseSeverity.Error,
                           stmt.Span, "'continue' outside of a loop");
                }
                return;
            }
        }
    }
}
