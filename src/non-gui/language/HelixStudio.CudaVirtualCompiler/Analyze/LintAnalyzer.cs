namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System.Collections.Generic;
    using System.Globalization;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Pass D (CU6xxx): general lint that does not require dataflow:
    ///   - unchecked cudaError_t return value (CU6001)
    ///   - kernel launch with no following error/sync check (CU6002)
    ///   - unreachable code after return/break/continue (CU6020)
    ///   - unused local variable (CU6021)
    ///   - __global__ kernel never launched in this translation unit (CU6022)
    ///   - static-size array OOB index (CU6030) / negative index (CU6031)
    /// Port of the C++ helix::cuda_vcompiler::analyze::LintAnalyzer
    /// (cuda_vcompiler/analyze/lint_analyzer.{h,cpp}).
    /// </summary>
    public sealed class LintAnalyzer
    {
        private readonly string filePath_;
        private readonly List<ParseDiagnostic> diagnostics_;
        private SymbolTable table_;

        public LintAnalyzer(string filePath)
        {
            filePath_ = filePath ?? string.Empty;
            diagnostics_ = new List<ParseDiagnostic>();
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics { get { return diagnostics_; } }

        private void Report(string code, ParseSeverity sev, SourceSpan span, string message)
        {
            diagnostics_.Add(new ParseDiagnostic(code, sev, span, message));
        }

        public void Analyze(TranslationUnit unit, SymbolTable table)
        {
            table_ = table;

            // Collect launched kernel names across the whole unit.
            HashSet<string> launched = new HashSet<string>();
            AstWalk.ForEachFunction(unit, delegate (FunctionDecl fn)
            {
                if (!fn.HasBody) return;
                AstWalk.ForEachExpr(fn.Body, delegate (Expr e)
                {
                    KernelLaunchExpr k = e as KernelLaunchExpr;
                    if (k != null)
                    {
                        NameExpr n = k.Callee as NameExpr;
                        if (n != null) launched.Add(n.Id);
                    }
                });
            });

            AstWalk.ForEachFunction(unit, delegate (FunctionDecl fn)
            {
                if (!fn.HasBody) return;
                CheckUncheckedErrorsAndDeadCode(fn.Body);
                CheckUnusedLocals(fn);
                CheckArrayBounds(fn);

                // Kernel never launched in this TU (Info — it may be launched elsewhere).
                // Only meaningful when the TU actually launches *some* kernel: a pure
                // device-kernel library (no launches at all) would otherwise flag every
                // kernel, which is pure noise.
                if (launched.Count != 0 && fn.IsKernel && !launched.Contains(fn.Name))
                {
                    Report(CudaDiagnosticCodes.LintKernelNeverLaunched, ParseSeverity.Info, fn.Span,
                           "kernel '" + fn.Name +
                           "' is never launched in this translation unit");
                }
            });

            table_ = null;
        }

        private void CheckUncheckedErrorsAndDeadCode(Stmt stmt)
        {
            if (stmt == null) return;

            CompoundStmt c = stmt as CompoundStmt;
            if (c != null)
            {
                IReadOnlyList<Stmt> body = c.Body;
                bool reportedDead = false;
                for (int i = 0; i < body.Count; ++i)
                {
                    Stmt s = body[i];

                    // Unchecked cudaError_t return: a bare call statement whose callee
                    // returns cudaError_t and whose value is discarded.
                    ExprStmt es = s as ExprStmt;
                    if (es != null)
                    {
                        CallExpr call = es.Expr as CallExpr;
                        if (call != null)
                        {
                            NameExpr n = call.Callee as NameExpr;
                            if (n != null)
                            {
                                List<Symbol> syms = table_.FindFunctions(n.Id);
                                for (int si = 0; si < syms.Count; ++si)
                                {
                                    if (syms[si].ReturnsCudaError())
                                    {
                                        Report(CudaDiagnosticCodes.LintUncheckedCudaError, ParseSeverity.Warning,
                                               call.Span,
                                               "return value of '" + n.Id +
                                               "' (cudaError_t) is ignored");
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Kernel launch with no following error / sync check.
                    if (es != null)
                    {
                        bool isLaunch = false;
                        AstWalk.ForEachExprIn(es.Expr, delegate (Expr e)
                        {
                            if (e is KernelLaunchExpr) isLaunch = true;
                        });
                        if (isLaunch)
                        {
                            bool checkedLaunch = false;
                            for (int j = i + 1; j < body.Count && j <= i + 2; ++j)
                                if (IsErrorCheckCall(body[j])) checkedLaunch = true;
                            if (!checkedLaunch)
                            {
                                Report(CudaDiagnosticCodes.LintMissingPostLaunchCheck, ParseSeverity.Info,
                                       es.Expr.Span,
                                       "no error check (cudaGetLastError / cudaDeviceSynchronize) " +
                                       "after this kernel launch");
                            }
                        }
                    }

                    // Unreachable code after a terminator.
                    if (!reportedDead && IsTerminator(s) && i + 1 < body.Count)
                    {
                        if (!(body[i + 1] is EmptyStmt))
                        {
                            Report(CudaDiagnosticCodes.LintUnreachableCode, ParseSeverity.Warning,
                                   body[i + 1].Span, "unreachable code");
                            reportedDead = true;
                        }
                    }

                    CheckUncheckedErrorsAndDeadCode(s);
                }
                return;
            }
            IfStmt ifs = stmt as IfStmt;
            if (ifs != null)
            {
                CheckUncheckedErrorsAndDeadCode(ifs.Then);
                CheckUncheckedErrorsAndDeadCode(ifs.Else);
                return;
            }
            ForStmt f = stmt as ForStmt;
            if (f != null) { CheckUncheckedErrorsAndDeadCode(f.Body); return; }
            WhileStmt w = stmt as WhileStmt;
            if (w != null) { CheckUncheckedErrorsAndDeadCode(w.Body); return; }
            DoStmt dw = stmt as DoStmt;
            if (dw != null) { CheckUncheckedErrorsAndDeadCode(dw.Body); return; }
            SwitchStmt sw = stmt as SwitchStmt;
            if (sw != null) { CheckUncheckedErrorsAndDeadCode(sw.Body); return; }
        }

        private void CheckUnusedLocals(FunctionDecl fn)
        {
            // Declared local variables (name -> decl span).
            Dictionary<string, SourceSpan> declared = new Dictionary<string, SourceSpan>();
            CollectDeclared(fn.Body, declared);
            if (declared.Count == 0) return;

            // Count name references. A write-only `x = 0;` counts as used (intentional,
            // to avoid noise).
            Dictionary<string, int> uses = new Dictionary<string, int>();
            AstWalk.ForEachExpr(fn.Body, delegate (Expr e)
            {
                NameExpr n = e as NameExpr;
                if (n != null)
                {
                    int count;
                    uses.TryGetValue(n.Id, out count);
                    uses[n.Id] = count + 1;
                }
            });

            foreach (KeyValuePair<string, SourceSpan> kv in declared)
            {
                // A bare `Type name;` with no initializer and zero references is unused.
                if (!uses.ContainsKey(kv.Key))
                {
                    Report(CudaDiagnosticCodes.LintUnusedLocal, ParseSeverity.Info, kv.Value,
                           "local variable '" + kv.Key + "' is declared but never used");
                }
            }
        }

        private static void CollectDeclared(Stmt s, Dictionary<string, SourceSpan> declared)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) CollectDeclared(c.Body[i], declared);
                return;
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null && !string.IsNullOrEmpty(v.Name) && !declared.ContainsKey(v.Name))
                    declared[v.Name] = v.Span;
                return;
            }
            IfStmt ifs = s as IfStmt;
            if (ifs != null) { CollectDeclared(ifs.Then, declared); CollectDeclared(ifs.Else, declared); return; }
            ForStmt f = s as ForStmt;
            if (f != null) { CollectDeclared(f.Init, declared); CollectDeclared(f.Body, declared); return; }
            WhileStmt w = s as WhileStmt;
            if (w != null) { CollectDeclared(w.Body, declared); return; }
            DoStmt dw = s as DoStmt;
            if (dw != null) { CollectDeclared(dw.Body, declared); return; }
            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) { CollectDeclared(sw.Body, declared); return; }
        }

        // CU6030 / CU6031: static-size array out-of-bounds lint. Best-effort, not sound.
        private void CheckArrayBounds(FunctionDecl fn)
        {
            if (!fn.HasBody) return;

            // Step 1: collect local array declarations with known static sizes.
            Dictionary<string, long> arraySizes = new Dictionary<string, long>();

            Dictionary<string, Expr> varInits = new Dictionary<string, Expr>();
            AstWalk.CollectVarInits(fn.Body, varInits);

            CollectArrayDecls(fn.Body, arraySizes, varInits);

            if (arraySizes.Count == 0) return;

            // Step 2: walk all IndexExpr nodes, check constant-foldable indices.
            EvalContext ctx = new EvalContext();
            ctx.VarInits = varInits;

            AstWalk.ForEachExpr(fn.Body, delegate (Expr e)
            {
                IndexExpr idx = e as IndexExpr;
                if (idx == null) return;

                // Is base a known local array?
                NameExpr baseName = idx.Base as NameExpr;
                if (baseName == null) return;
                long declaredSize;
                if (!arraySizes.TryGetValue(baseName.Id, out declaredSize)) return;

                // Can we fold the index to a constant?
                long? idxVal = CudaContext.EvalConstInt(idx.Index, ctx);
                if (!idxVal.HasValue) return;
                long iv = idxVal.Value;

                if (iv < 0)
                {
                    Report(CudaDiagnosticCodes.LintArrayNegIdx, ParseSeverity.Warning, idx.Span,
                           "array index " + Str(iv) + " is negative for '" +
                           baseName.Id + "'");
                }
                else if (iv >= declaredSize)
                {
                    Report(CudaDiagnosticCodes.LintArrayOob, ParseSeverity.Warning, idx.Span,
                           "array index " + Str(iv) + " is out of bounds for '" +
                           baseName.Id + "' (declared size " +
                           Str(declaredSize) + ")");
                }
            });
        }

        private static void CollectArrayDecls(Stmt s, Dictionary<string, long> arraySizes, Dictionary<string, Expr> varInits)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) CollectArrayDecls(c.Body[i], arraySizes, varInits);
                return;
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null && v.ArraySize != null)
                {
                    EvalContext ctx = new EvalContext();
                    ctx.VarInits = varInits;
                    long? sz = CudaContext.EvalConstInt(v.ArraySize, ctx);
                    if (sz.HasValue && sz.Value > 0) arraySizes[v.Name] = sz.Value;
                }
                return;
            }
            IfStmt ifs = s as IfStmt;
            if (ifs != null) { CollectArrayDecls(ifs.Then, arraySizes, varInits); CollectArrayDecls(ifs.Else, arraySizes, varInits); return; }
            ForStmt f = s as ForStmt;
            if (f != null) { CollectArrayDecls(f.Init, arraySizes, varInits); CollectArrayDecls(f.Body, arraySizes, varInits); return; }
            WhileStmt w = s as WhileStmt;
            if (w != null) { CollectArrayDecls(w.Body, arraySizes, varInits); return; }
            DoStmt dw = s as DoStmt;
            if (dw != null) { CollectArrayDecls(dw.Body, arraySizes, varInits); return; }
            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) { CollectArrayDecls(sw.Body, arraySizes, varInits); return; }
        }

        private static bool IsTerminator(Stmt s)
        {
            return s is ReturnStmt || s is BreakStmt || s is ContinueStmt;
        }

        private static bool IsErrorCheckCall(Stmt s)
        {
            ExprStmt es = s as ExprStmt;
            Expr expression = es != null ? es.Expr : null;
            DeclStmt declaration = s as DeclStmt;
            VarDecl variable = declaration != null ? declaration.Decl as VarDecl : null;
            if (variable != null) expression = variable.Initializer;
            IfStmt conditional = s as IfStmt;
            if (conditional != null) expression = conditional.Cond;
            if (expression == null) return false;
            bool[] found = { false };
            AstWalk.ForEachExprIn(expression, delegate (Expr e)
            {
                CallExpr c = e as CallExpr;
                if (c != null)
                {
                    NameExpr n = c.Callee as NameExpr;
                    if (n != null)
                    {
                        if (n.Id == "cudaGetLastError" || n.Id == "cudaPeekAtLastError" ||
                            n.Id == "cudaDeviceSynchronize" || n.Id == "cudaStreamSynchronize")
                            found[0] = true;
                    }
                }
            });
            // An assignment whose rhs is such a call also counts.
            BinaryExpr bin = expression as BinaryExpr;
            if (bin != null && bin.IsAssignment())
            {
                AstWalk.ForEachExprIn(bin.Rhs, delegate (Expr e)
                {
                    CallExpr c = e as CallExpr;
                    if (c != null)
                    {
                        NameExpr n = c.Callee as NameExpr;
                        if (n != null)
                            if (n.Id == "cudaGetLastError" || n.Id == "cudaPeekAtLastError")
                                found[0] = true;
                    }
                });
            }
            return found[0];
        }

        private static string Str(long v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }
    }
}
