namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Pass A (CU5xxx): execution-space qualifier and synchronization rules:
    ///   - device code calling a host-only function (CU5001)
    ///   - calling a __global__ kernel as an ordinary function (CU5002)
    ///   - built-in variables (threadIdx, ...) used in host code (CU5003)
    ///   - __shared__ declared in a host function (CU5010)
    ///   - __device__/__constant__ qualifier on a local variable (CU5012)
    ///   - writing a __constant__ global from device code (CU5013)
    ///   - __syncthreads() inside a thread-divergent branch (CU5020) or loop (CU5021)
    /// The divergence checks taint-track variables derived from threadIdx. Port of
    /// the C++ helix::cuda_vcompiler::analyze::QualifierSyncAnalyzer
    /// (cuda_vcompiler/analyze/qualifier_analyzer.{h,cpp}).
    /// </summary>
    public sealed class QualifierSyncAnalyzer
    {
        private readonly string filePath_;
        private readonly List<ParseDiagnostic> diagnostics_;
        private SymbolTable table_;

        public QualifierSyncAnalyzer(string filePath)
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
            AstWalk.ForEachFunction(unit, delegate (FunctionDecl fn)
            {
                if (!fn.HasBody) return;
                AnalyzeFunction(fn);
            });
            table_ = null;
        }

        private void AnalyzeFunction(FunctionDecl fn)
        {
            ScanSpaceRules(fn);
            HashSet<string> tainted = new HashSet<string>();
            WalkSync(fn.Body, fn, false, false, tainted);
        }

        private bool IsThreadDependent(Expr e, HashSet<string> tainted)
        {
            return CudaContext.ExprReadsName(e, delegate (string name)
            {
                // threadIdx varies per thread within a block (drives divergence);
                // blockIdx/blockDim/gridDim are uniform within a block. A variable
                // tainted from threadIdx is also thread-dependent.
                return name == "threadIdx" || tainted.Contains(name);
            });
        }

        private void ScanSpaceRules(FunctionDecl fn)
        {
            bool host = fn.Space == FuncSpace.Host;
            bool device = fn.Space == FuncSpace.Device || fn.Space == FuncSpace.Global;

            // A local variable whose name collides with a CUDA built-in (threadIdx,
            // blockDim, gridDim, ...) shadows the built-in — nvcc resolves the name to
            // the local, so `dim3 gridDim(1,1,1); kernel<<<gridDim, blockDim>>>(...)`
            // in host code is legal. Collect declared local names so CU5003 does not
            // false-positive on such shadowing (CUDA-vs-nvcc parity).
            HashSet<string> shadowed = new HashSet<string>();
            CollectLocalNames(fn.Body, shadowed);

            AstWalk.ForEachExpr(fn.Body, delegate (Expr e)
            {
                // Built-in variable in host code.
                if (host)
                {
                    NameExpr n = e as NameExpr;
                    if (n != null)
                    {
                        if (Keywords.IsCudaBuiltinVar(n.Id) && !shadowed.Contains(n.Id))
                        {
                            Report(CudaDiagnosticCodes.QualBuiltinVarInHost, ParseSeverity.Error, e.Span,
                                   "built-in variable '" + n.Id +
                                   "' is only available in device code");
                        }
                    }
                }

                CallExpr call = e as CallExpr;
                if (call != null)
                {
                    NameExpr callee = call.Callee as NameExpr;
                    if (callee == null) return;
                    List<Symbol> matches = table_.FindFunctions(callee.Id);
                    if (matches.Count == 0) return;  // unknown (e.g. from a header) — no claim

                    // Calling a kernel as an ordinary function.
                    for (int i = 0; i < matches.Count; ++i)
                    {
                        if (matches[i].IsKernel)
                        {
                            Report(CudaDiagnosticCodes.QualKernelCalledOnDevice, ParseSeverity.Error, e.Span,
                                   "__global__ kernel '" + callee.Id +
                                   "' must be launched with <<<...>>>, not called directly");
                            return;
                        }
                    }
                    // Device code calling a host-only function.
                    if (device)
                    {
                        bool callableOnDevice = false;
                        for (int i = 0; i < matches.Count; ++i)
                        {
                            FuncSpace sp = matches[i].Space;
                            if (sp == FuncSpace.Device || sp == FuncSpace.HostDevice || sp == FuncSpace.Global)
                            {
                                callableOnDevice = true;
                                break;
                            }
                        }
                        if (!callableOnDevice)
                        {
                            Report(CudaDiagnosticCodes.QualDeviceCallsHost, ParseSeverity.Error, e.Span,
                                   "host-only function '" + callee.Id +
                                   "' cannot be called from device code");
                        }
                    }
                }

                // Writing a __constant__ global from device code.
                if (device)
                {
                    BinaryExpr bin = e as BinaryExpr;
                    if (bin != null && bin.IsAssignment())
                    {
                        NameExpr lhs = bin.Lhs as NameExpr;
                        if (lhs != null)
                        {
                            Symbol sym = table_.Root.LookupLocal(lhs.Id);
                            if (sym != null && sym.Storage == StorageClass.Constant)
                            {
                                Report(CudaDiagnosticCodes.QualConstantWritten, ParseSeverity.Error,
                                       e.Span,
                                       "'" + lhs.Id +
                                       "' is __constant__ and cannot be written from device code");
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Literal integer value of an expression when trivially known: a plain int
        /// literal or a unary +/- over one (how `[-1]` parses). Null otherwise.
        /// </summary>
        private static long? LiteralIntValue(Expr e)
        {
            UnaryExpr u = e as UnaryExpr;
            if (u != null && (u.Op == UnaryOpKind.Minus || u.Op == UnaryOpKind.Plus))
            {
                long? inner = LiteralIntValue(u.Operand);
                if (inner.HasValue) return u.Op == UnaryOpKind.Minus ? -inner.Value : inner.Value;
                return null;
            }
            LiteralExpr lit = e as LiteralExpr;
            long v;
            if (lit != null && lit.Kind == LiteralKind.Int &&
                long.TryParse(lit.Text, out v))
            {
                return v;
            }
            return null;
        }

        /// <summary>
        /// Collects the names of every local variable declared anywhere in a function
        /// body (regardless of initializer form — plain, copy, or constructor-call).
        /// Used to detect built-in shadowing so CU5003 stays nvcc-accurate.
        /// </summary>
        private static void CollectLocalNames(Stmt s, HashSet<string> outNames)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) CollectLocalNames(c.Body[i], outNames);
                return;
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null && !string.IsNullOrEmpty(v.Name)) outNames.Add(v.Name);
                return;
            }
            IfStmt ifs = s as IfStmt;
            if (ifs != null) { CollectLocalNames(ifs.Then, outNames); CollectLocalNames(ifs.Else, outNames); return; }
            ForStmt f = s as ForStmt;
            if (f != null) { CollectLocalNames(f.Init, outNames); CollectLocalNames(f.Body, outNames); return; }
            WhileStmt w = s as WhileStmt;
            if (w != null) { CollectLocalNames(w.Body, outNames); return; }
            DoStmt dw = s as DoStmt;
            if (dw != null) { CollectLocalNames(dw.Body, outNames); return; }
            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) { CollectLocalNames(sw.Body, outNames); return; }
        }

        private void WalkSync(Stmt stmt, FunctionDecl fn, bool divergent, bool divergentLoop, HashSet<string> tainted)
        {
            if (stmt == null) return;
            bool host = fn.Space == FuncSpace.Host;

            CompoundStmt c = stmt as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) WalkSync(c.Body[i], fn, divergent, divergentLoop, tainted);
                return;
            }
            DeclStmt d = stmt as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null)
                {
                    if (v.Storage == StorageClass.Shared && host)
                    {
                        Report(CudaDiagnosticCodes.QualSharedInHost, ParseSeverity.Error, v.Span,
                               "__shared__ variable '" + v.Name +
                               "' is not allowed in host code");
                    }
                    if (v.Storage == StorageClass.Shared && v.ArraySize != null)
                    {
                        // Only literal sizes are judged — macro-expanded constants
                        // arrive here as literals; anything unprovable stays silent.
                        long? size = LiteralIntValue(v.ArraySize);
                        if (size.HasValue && size.Value <= 0)
                        {
                            Report(CudaDiagnosticCodes.QualSharedBadSize, ParseSeverity.Error, v.Span,
                                   "__shared__ array '" + v.Name + "' has invalid size " +
                                   size.Value + " (must be a positive compile-time constant)");
                        }
                    }
                    if (v.Storage == StorageClass.Device || v.Storage == StorageClass.Constant)
                    {
                        Report(CudaDiagnosticCodes.QualDeviceQualOnLocal, ParseSeverity.Error, v.Span,
                               "__device__/__constant__ qualifier is not allowed on the local " +
                               "variable '" + v.Name + "'");
                    }
                    if (v.Initializer != null && IsThreadDependent(v.Initializer, tainted))
                    {
                        tainted.Add(v.Name);
                    }
                }
                return;
            }
            ExprStmt es = stmt as ExprStmt;
            if (es != null)
            {
                Expr e = es.Expr;
                // Taint propagation through assignment.
                BinaryExpr bin = e as BinaryExpr;
                if (bin != null && bin.IsAssignment())
                {
                    NameExpr lhs = bin.Lhs as NameExpr;
                    if (lhs != null)
                    {
                        if (IsThreadDependent(bin.Rhs, tainted)) tainted.Add(lhs.Id);
                    }
                }
                // __syncthreads()/__syncwarp() under divergence.
                AstWalk.ForEachExprIn(e, delegate (Expr sub)
                {
                    CallExpr call = sub as CallExpr;
                    if (call == null) return;
                    NameExpr callee = call.Callee as NameExpr;
                    if (callee == null) return;
                    if (callee.Id == "__syncthreads" || callee.Id == "__syncwarp")
                    {
                        if (divergent)
                        {
                            Report(CudaDiagnosticCodes.QualSyncInDivergentBranch, ParseSeverity.Error, sub.Span,
                                   "__syncthreads() inside a thread-divergent branch causes a hang: " +
                                   "not all threads in the block reach the barrier");
                        }
                        else if (divergentLoop)
                        {
                            Report(CudaDiagnosticCodes.QualSyncInDivergentLoop, ParseSeverity.Error, sub.Span,
                                   "__syncthreads() inside a loop with a thread-dependent trip count " +
                                   "causes a hang: threads execute the barrier a different number of times");
                        }
                    }
                });
                return;
            }
            IfStmt i2 = stmt as IfStmt;
            if (i2 != null)
            {
                bool branchDivergent = divergent || IsThreadDependent(i2.Cond, tainted);
                WalkSync(i2.Then, fn, branchDivergent, divergentLoop, tainted);
                WalkSync(i2.Else, fn, branchDivergent, divergentLoop, tainted);
                return;
            }
            ForStmt f = stmt as ForStmt;
            if (f != null)
            {
                DeclStmt d2 = f.Init as DeclStmt;
                if (d2 != null)
                {
                    VarDecl v = d2.Decl as VarDecl;
                    if (v != null && v.Initializer != null && IsThreadDependent(v.Initializer, tainted))
                        tainted.Add(v.Name);
                }
                bool loopDiv = divergentLoop || IsThreadDependent(f.Cond, tainted);
                WalkSync(f.Body, fn, divergent, loopDiv, tainted);
                return;
            }
            WhileStmt w = stmt as WhileStmt;
            if (w != null)
            {
                bool loopDiv = divergentLoop || IsThreadDependent(w.Cond, tainted);
                WalkSync(w.Body, fn, divergent, loopDiv, tainted);
                return;
            }
            DoStmt dw = stmt as DoStmt;
            if (dw != null)
            {
                bool loopDiv = divergentLoop || IsThreadDependent(dw.Cond, tainted);
                WalkSync(dw.Body, fn, divergent, loopDiv, tainted);
                return;
            }
            SwitchStmt sw = stmt as SwitchStmt;
            if (sw != null)
            {
                bool branchDivergent = divergent || IsThreadDependent(sw.Cond, tainted);
                WalkSync(sw.Body, fn, branchDivergent, divergentLoop, tainted);
                return;
            }
        }
    }
}
