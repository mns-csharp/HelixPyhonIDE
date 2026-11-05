namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Pass C (CU4xxx): pointer memory-space analysis. Per function, it assigns each
    /// pointer variable a MemSpace seeded from cudaMalloc / malloc / new / &amp;local,
    /// then flags cross-space dereferences (CU4001/CU4002), cudaMemcpy direction
    /// mismatches (CU4010), leaks / double-free / free-of-host-pointer / use-after-
    /// free (CU4020-CU4023), and host pointers passed to a kernel launch (CU4030).
    /// The flow is a single linear pass (no branch merge). Port of the C++
    /// helix::cuda_vcompiler::analyze::MemorySpaceAnalyzer
    /// (cuda_vcompiler/analyze/memory_space_analyzer.{h,cpp}).
    /// </summary>
    public sealed class MemorySpaceAnalyzer
    {
        private sealed class PtrState
        {
            public MemSpace Space = MemSpace.Unknown;
            public bool Freed;
            public bool DeviceAlloc;            // allocated via cudaMalloc/Managed
            public SourceSpan AllocSite;
        }

        private readonly string filePath_;
        private readonly List<ParseDiagnostic> diagnostics_;
        private SymbolTable table_;

        public MemorySpaceAnalyzer(string filePath)
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
                if (fn.HasBody) AnalyzeFunction(fn);
            });
            table_ = null;
        }

        private MemSpace ClassifyRhs(Expr rhs, Dictionary<string, PtrState> vars)
        {
            if (rhs == null) return MemSpace.Unknown;
            ParenExpr p = rhs as ParenExpr;
            if (p != null) return ClassifyRhs(p.Inner, vars);
            CastExpr c = rhs as CastExpr;
            if (c != null) return ClassifyRhs(c.Operand, vars);
            if (rhs is NewExpr) return MemSpace.Host;
            UnaryExpr u = rhs as UnaryExpr;
            if (u != null)
            {
                if (u.Op == UnaryOpKind.AddrOf) return MemSpace.Host;  // &local / &stack
            }
            CallExpr call = rhs as CallExpr;
            if (call != null)
            {
                NameExpr n2 = call.Callee as NameExpr;
                if (n2 != null)
                {
                    if (n2.Id == "malloc" || n2.Id == "calloc" || n2.Id == "realloc")
                        return MemSpace.Host;
                }
            }
            NameExpr n = rhs as NameExpr;
            if (n != null)
            {
                PtrState st;
                if (vars.TryGetValue(n.Id, out st)) return st.Space;
            }
            return MemSpace.Unknown;
        }

        private void AnalyzeFunction(FunctionDecl fn)
        {
            Dictionary<string, PtrState> vars = new Dictionary<string, PtrState>();

            // Seed pointer parameters. Kernel pointer params are device pointers by
            // convention; host/device function pointer params are unknown (caller-set).
            bool kernel = fn.IsKernel;
            IReadOnlyList<Parameter> parameters = fn.Params;
            for (int i = 0; i < parameters.Count; ++i)
            {
                Parameter p = parameters[i];
                if (string.IsNullOrEmpty(p.Name) || p.Type == null || !p.Type.IsPointer()) continue;
                PtrState st = new PtrState();
                st.Space = kernel ? MemSpace.Device : MemSpace.Unknown;
                vars[p.Name] = st;
            }

            WalkStmt(fn.Body, fn, vars);

            // Leak check: device allocations never freed in this function.
            foreach (KeyValuePair<string, PtrState> kv in vars)
            {
                if (kv.Value.DeviceAlloc && !kv.Value.Freed)
                {
                    Report(CudaDiagnosticCodes.MemLeak, ParseSeverity.Warning, kv.Value.AllocSite,
                           "device allocation '" + kv.Key +
                           "' is never released with cudaFree in this function");
                }
            }
        }

        private void WalkStmt(Stmt stmt, FunctionDecl fn, Dictionary<string, PtrState> vars)
        {
            if (stmt == null) return;

            CompoundStmt c = stmt as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) WalkStmt(c.Body[i], fn, vars);
                return;
            }
            DeclStmt d = stmt as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null)
                {
                    if (v.Type != null && v.Type.IsPointer())
                    {
                        PtrState st = new PtrState();
                        st.Space = ClassifyRhs(v.Initializer, vars);
                        // __device__/__constant__ globals would be device; locals here.
                        vars[v.Name] = st;
                    }
                    // Check derefs inside the initializer.
                    ProcessExpr(v.Initializer, fn, vars);
                }
                return;
            }
            ExprStmt es = stmt as ExprStmt;
            if (es != null)
            {
                ProcessExpr(es.Expr, fn, vars);
                return;
            }
            IfStmt i2 = stmt as IfStmt;
            if (i2 != null)
            {
                ProcessExpr(i2.Cond, fn, vars);
                WalkStmt(i2.Then, fn, vars);
                WalkStmt(i2.Else, fn, vars);
                return;
            }
            ForStmt f = stmt as ForStmt;
            if (f != null)
            {
                WalkStmt(f.Init, fn, vars);
                ProcessExpr(f.Cond, fn, vars);
                ProcessExpr(f.Inc, fn, vars);
                WalkStmt(f.Body, fn, vars);
                return;
            }
            WhileStmt w = stmt as WhileStmt;
            if (w != null)
            {
                ProcessExpr(w.Cond, fn, vars);
                WalkStmt(w.Body, fn, vars);
                return;
            }
            DoStmt dw = stmt as DoStmt;
            if (dw != null)
            {
                WalkStmt(dw.Body, fn, vars);
                ProcessExpr(dw.Cond, fn, vars);
                return;
            }
            SwitchStmt sw = stmt as SwitchStmt;
            if (sw != null)
            {
                ProcessExpr(sw.Cond, fn, vars);
                WalkStmt(sw.Body, fn, vars);
                return;
            }
            ReturnStmt r = stmt as ReturnStmt;
            if (r != null)
            {
                ProcessExpr(r.Value, fn, vars);
                return;
            }
        }

        private void ProcessExpr(Expr root, FunctionDecl fn, Dictionary<string, PtrState> vars)
        {
            if (root == null) return;
            bool deviceCtx = fn.Space == FuncSpace.Device || fn.Space == FuncSpace.Global;

            AstWalk.ForEachExprIn(root, delegate (Expr e)
            {
                // ---- cuda* allocation / free / memcpy ----
                CallExpr call = e as CallExpr;
                if (call != null)
                {
                    NameExpr callee = call.Callee as NameExpr;
                    if (callee != null)
                    {
                        string fnName = callee.Id;
                        IReadOnlyList<Expr> args = call.Args;
                        if ((fnName == "cudaMalloc" || fnName == "cudaMallocManaged" ||
                             fnName == "cudaMallocHost") && args.Count != 0)
                        {
                            string target = AddrOfTarget(args[0]);
                            if (!string.IsNullOrEmpty(target))
                            {
                                PtrState st = GetOrCreate(vars, target);
                                st.Freed = false;
                                if (fnName == "cudaMalloc") { st.Space = MemSpace.Device; st.DeviceAlloc = true; }
                                else if (fnName == "cudaMallocManaged") { st.Space = MemSpace.Managed; st.DeviceAlloc = true; }
                                else { st.Space = MemSpace.Host; }  // pinned host
                                st.AllocSite = call.Span;
                            }
                        }
                        else if ((fnName == "cudaFree" || fnName == "cudaFreeHost") && args.Count != 0)
                        {
                            string target = PointerVarName(args[0]);
                            PtrState st;
                            if (vars.TryGetValue(target, out st))
                            {
                                if (st.Freed)
                                {
                                    Report(CudaDiagnosticCodes.MemDoubleFree, ParseSeverity.Error, call.Span,
                                           "'" + target + "' is freed more than once");
                                }
                                else if (fnName == "cudaFree" && st.Space == MemSpace.Host)
                                {
                                    Report(CudaDiagnosticCodes.MemFreeOfHostPointer, ParseSeverity.Error, call.Span,
                                           "cudaFree on host pointer '" + target +
                                           "'; use free() instead");
                                }
                                st.Freed = true;
                            }
                        }
                        else if (fnName == "cudaMemcpy" && args.Count >= 4)
                        {
                            string dst = PointerVarName(args[0]);
                            string src = PointerVarName(args[1]);
                            string kind = MemcpyKindName(args[3]);
                            if (kind != null)
                            {
                                MemSpace ds = SpaceOf(vars, dst), ss = SpaceOf(vars, src);
                                bool bad = false;
                                if (kind == "cudaMemcpyHostToDevice")
                                    bad = Conflict(ss, MemSpace.Host) || Conflict(ds, MemSpace.Device);
                                else if (kind == "cudaMemcpyDeviceToHost")
                                    bad = Conflict(ss, MemSpace.Device) || Conflict(ds, MemSpace.Host);
                                else if (kind == "cudaMemcpyDeviceToDevice")
                                    bad = Conflict(ss, MemSpace.Device) || Conflict(ds, MemSpace.Device);
                                else if (kind == "cudaMemcpyHostToHost")
                                    bad = Conflict(ss, MemSpace.Host) || Conflict(ds, MemSpace.Host);
                                if (bad)
                                {
                                    Report(CudaDiagnosticCodes.MemMemcpyDirectionWrong, ParseSeverity.Error,
                                           call.Span,
                                           "cudaMemcpy direction '" + kind +
                                           "' does not match the inferred memory spaces of its operands");
                                }
                            }
                        }
                    }
                }

                // ---- assignment: propagate space ----
                BinaryExpr bin = e as BinaryExpr;
                if (bin != null && bin.Op == BinOpKind.Assign)
                {
                    NameExpr lhs = bin.Lhs as NameExpr;
                    if (lhs != null)
                    {
                        PtrState st;
                        if (vars.TryGetValue(lhs.Id, out st))
                        {
                            st.Space = ClassifyRhs(bin.Rhs, vars);
                            st.Freed = false;
                        }
                    }
                }

                // ---- dereference checks ----
                string derefVar = string.Empty;
                UnaryExpr u = e as UnaryExpr;
                if (u != null)
                {
                    if (u.Op == UnaryOpKind.Deref) derefVar = PointerVarName(u.Operand);
                }
                else
                {
                    IndexExpr ix = e as IndexExpr;
                    if (ix != null)
                    {
                        derefVar = PointerVarName(ix.Base);
                    }
                    else
                    {
                        MemberExpr m = e as MemberExpr;
                        if (m != null && m.IsArrow) derefVar = PointerVarName(m.Object);
                    }
                }
                if (!string.IsNullOrEmpty(derefVar))
                {
                    PtrState st;
                    if (vars.TryGetValue(derefVar, out st))
                    {
                        if (st.Freed)
                        {
                            Report(CudaDiagnosticCodes.MemUseAfterFree, ParseSeverity.Error, e.Span,
                                   "'" + derefVar + "' is dereferenced after being freed");
                        }
                        else if (!deviceCtx && st.Space == MemSpace.Device)
                        {
                            Report(CudaDiagnosticCodes.MemDeviceDerefOnHost, ParseSeverity.Error, e.Span,
                                   "device pointer '" + derefVar +
                                   "' is dereferenced in host code");
                        }
                        else if (deviceCtx && st.Space == MemSpace.Host)
                        {
                            Report(CudaDiagnosticCodes.MemHostDerefOnDevice, ParseSeverity.Error, e.Span,
                                   "host pointer '" + derefVar +
                                   "' is dereferenced in device code");
                        }
                    }
                }

                // ---- host pointer passed to a kernel launch ----
                KernelLaunchExpr launch = e as KernelLaunchExpr;
                if (launch != null)
                {
                    Symbol kernelSym = null;
                    NameExpr lcallee = launch.Callee as NameExpr;
                    if (lcallee != null)
                    {
                        List<Symbol> matches = table_.FindFunctions(lcallee.Id);
                        for (int mi = 0; mi < matches.Count; ++mi)
                            if (matches[mi].IsKernel) { kernelSym = matches[mi]; break; }
                    }
                    IReadOnlyList<Expr> args = launch.Args;
                    for (int i = 0; i < args.Count; ++i)
                    {
                        string an = PointerVarName(args[i]);
                        if (string.IsNullOrEmpty(an)) continue;
                        PtrState st;
                        if (!vars.TryGetValue(an, out st) || st.Space != MemSpace.Host) continue;
                        bool paramIsPtr = true;  // default assume yes if signature unknown
                        if (kernelSym != null && kernelSym.FuncDecl != null && i < kernelSym.FuncDecl.Params.Count)
                            paramIsPtr = kernelSym.FuncDecl.Params[i].Type.IsPointer();
                        if (paramIsPtr)
                        {
                            Report(CudaDiagnosticCodes.MemHostPtrToKernel, ParseSeverity.Error, launch.Span,
                                   "host pointer '" + an +
                                   "' is passed to a kernel; the device cannot dereference it");
                        }
                    }
                }
            });
        }

        private static MemSpace SpaceOf(Dictionary<string, PtrState> vars, string n)
        {
            PtrState st;
            return (n != null && vars.TryGetValue(n, out st)) ? st.Space : MemSpace.Unknown;
        }

        private static bool Conflict(MemSpace got, MemSpace want)
        {
            return got != MemSpace.Unknown && got != MemSpace.Managed && got != want;
        }

        private static PtrState GetOrCreate(Dictionary<string, PtrState> vars, string key)
        {
            PtrState st;
            if (!vars.TryGetValue(key, out st))
            {
                st = new PtrState();
                vars[key] = st;
            }
            return st;
        }

        // Unwrap parens/casts and return the underlying NameExpr id of a pointer
        // expression (`p`, `(T*)p`, `(p)`), or "" if not a simple variable.
        private static string PointerVarName(Expr e)
        {
            if (e == null) return "";
            NameExpr n = e as NameExpr;
            if (n != null) return n.Id;
            ParenExpr p = e as ParenExpr;
            if (p != null) return PointerVarName(p.Inner);
            CastExpr c = e as CastExpr;
            if (c != null) return PointerVarName(c.Operand);
            return "";
        }

        // For `&p` / `(void**)&p`, return the pointer variable `p`.
        private static string AddrOfTarget(Expr e)
        {
            if (e == null) return "";
            ParenExpr p = e as ParenExpr;
            if (p != null) return AddrOfTarget(p.Inner);
            CastExpr c = e as CastExpr;
            if (c != null) return AddrOfTarget(c.Operand);
            UnaryExpr u = e as UnaryExpr;
            if (u != null)
            {
                if (u.Op == UnaryOpKind.AddrOf) return PointerVarName(u.Operand);
            }
            return "";
        }

        private static string MemcpyKindName(Expr e)
        {
            NameExpr n = e as NameExpr;
            if (n != null) return n.Id;
            return null;
        }
    }
}
