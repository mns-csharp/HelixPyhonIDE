namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System.Collections.Generic;
    using System.Globalization;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Pass B (CU3xxx): validates every kernel-launch expression — that the callee
    /// is a kernel, that block/grid dimensions are within device limits, that
    /// dynamic shared memory is sane, and that the argument count matches the
    /// kernel's signature. Constant folding uses #define macros and local const-ish
    /// variable initializers. Port of the C++
    /// helix::cuda_vcompiler::analyze::LaunchAnalyzer
    /// (cuda_vcompiler/analyze/launch_analyzer.{h,cpp}).
    /// </summary>
    public sealed class LaunchAnalyzer
    {
        private readonly string filePath_;
        private readonly List<ParseDiagnostic> diagnostics_;

        public LaunchAnalyzer(string filePath)
        {
            filePath_ = filePath ?? string.Empty;
            diagnostics_ = new List<ParseDiagnostic>();
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics { get { return diagnostics_; } }

        private void Report(string code, ParseSeverity sev, SourceSpan span, string message)
        {
            diagnostics_.Add(new ParseDiagnostic(code, sev, span, message));
        }

        public void Analyze(TranslationUnit unit, SymbolTable table, Preprocessor pp)
        {
            AstWalk.ForEachFunction(unit, delegate (FunctionDecl fn)
            {
                if (!fn.HasBody) return;
                Dictionary<string, Expr> varInits = new Dictionary<string, Expr>();
                AstWalk.CollectVarInits(fn.Body, varInits);
                EvalContext ctx = new EvalContext();
                ctx.Pp = pp;
                ctx.VarInits = varInits;
                bool inDeviceCode = fn.Space == FuncSpace.Device || fn.Space == FuncSpace.Global;

                AstWalk.ForEachExpr(fn.Body, delegate (Expr e)
                {
                    KernelLaunchExpr launch = e as KernelLaunchExpr;
                    if (launch == null) return;

                    // Callee kernel resolution.
                    Symbol kernelSym = null;
                    NameExpr name = launch.Callee as NameExpr;
                    if (name != null)
                    {
                        List<Symbol> matches = table.FindFunctions(name.Id);
                        if (matches.Count != 0)
                        {
                            bool anyKernel = false;
                            for (int i = 0; i < matches.Count; ++i)
                            {
                                if (matches[i].IsKernel) { anyKernel = true; kernelSym = matches[i]; break; }
                            }
                            if (!anyKernel)
                            {
                                Report(CudaDiagnosticCodes.LaunchOfNonKernel, ParseSeverity.Error, launch.Span,
                                       "'" + name.Id + "' is launched with <<<>>> but is not a " +
                                       "__global__ kernel");
                            }
                        }
                    }

                    if (inDeviceCode)
                    {
                        Report(CudaDiagnosticCodes.LaunchFromDeviceNoCdp, ParseSeverity.Warning, launch.Span,
                               "kernel launch from device code requires dynamic parallelism " +
                               "(compile with relocatable device code and sm_35+)");
                    }

                    // Block dimensions.
                    Dim3Value block = CudaContext.EvalDim(launch.Block, ctx);
                    if (block.Known)
                    {
                        if (block.X == 0 || block.Y == 0 || block.Z == 0)
                        {
                            Report(CudaDiagnosticCodes.LaunchDimIsZero, ParseSeverity.Error, launch.Span,
                                   "launch block dimension must not be zero");
                        }
                        else
                        {
                            if (block.Product() > Limits.MaxThreadsPerBlock)
                            {
                                Report(CudaDiagnosticCodes.LaunchBlockTooLarge, ParseSeverity.Error, launch.Span,
                                       "block has " + Str(block.Product()) +
                                       " threads, exceeding the device limit of " +
                                       Str(Limits.MaxThreadsPerBlock) + " threads per block");
                            }
                            if (block.X > Limits.MaxBlockDimXY || block.Y > Limits.MaxBlockDimXY)
                            {
                                Report(CudaDiagnosticCodes.LaunchBlockDimTooLarge, ParseSeverity.Error, launch.Span,
                                       "block X/Y dimension exceeds " + Str(Limits.MaxBlockDimXY));
                            }
                            if (block.Z > Limits.MaxBlockDimZ)
                            {
                                Report(CudaDiagnosticCodes.LaunchBlockDimTooLarge, ParseSeverity.Error, launch.Span,
                                       "block Z dimension exceeds " + Str(Limits.MaxBlockDimZ));
                            }
                        }
                    }

                    // Grid dimensions.
                    Dim3Value grid = CudaContext.EvalDim(launch.Grid, ctx);
                    if (grid.Known)
                    {
                        if (grid.X == 0 || grid.Y == 0 || grid.Z == 0)
                        {
                            Report(CudaDiagnosticCodes.LaunchDimIsZero, ParseSeverity.Error, launch.Span,
                                   "launch grid dimension must not be zero");
                        }
                        else if (grid.Y > Limits.MaxGridDimYZ || grid.Z > Limits.MaxGridDimYZ)
                        {
                            Report(CudaDiagnosticCodes.LaunchGridDimTooLarge, ParseSeverity.Error, launch.Span,
                                   "grid Y/Z dimension exceeds " + Str(Limits.MaxGridDimYZ));
                        }
                    }

                    // Dynamic shared memory size.
                    long? sharedBytes = null;
                    if (launch.SharedMem != null)
                    {
                        long? sm = CudaContext.EvalConstInt(launch.SharedMem, ctx);
                        sharedBytes = sm;
                        if (sm.HasValue)
                        {
                            if (sm.Value > Limits.MaxStaticSharedMem)
                            {
                                Report(CudaDiagnosticCodes.LaunchSharedMemTooLarge, ParseSeverity.Warning,
                                       launch.Span,
                                       "dynamic shared memory request of " + Str(sm.Value) +
                                       " bytes exceeds the typical 48 KB static limit; opt-in is " +
                                       "required for larger allocations");
                            }
                        }
                    }

                    // Dynamic shared memory <-> extern __shared__ pairing. Only when
                    // the kernel's body is in this TU: dynamic shared memory is only
                    // reachable through an UNSIZED __shared__ array declaration
                    // (`extern __shared__ T buf[];`), so the two must appear together.
                    if (kernelSym != null && kernelSym.FuncDecl != null && kernelSym.FuncDecl.HasBody)
                    {
                        bool usesDynamicShared = HasUnsizedSharedArray(kernelSym.FuncDecl.Body);
                        if (sharedBytes.HasValue && sharedBytes.Value > 0 && !usesDynamicShared)
                        {
                            Report(CudaDiagnosticCodes.LaunchSharedMemUnused, ParseSeverity.Warning, launch.Span,
                                   "launch passes " + Str(sharedBytes.Value) + " bytes of dynamic " +
                                   "shared memory but kernel '" + kernelSym.Name + "' declares no " +
                                   "extern __shared__ buffer to receive it");
                        }
                        if (usesDynamicShared &&
                            (launch.SharedMem == null || (sharedBytes.HasValue && sharedBytes.Value == 0)))
                        {
                            Report(CudaDiagnosticCodes.LaunchSharedMemMissing, ParseSeverity.Warning, launch.Span,
                                   "kernel '" + kernelSym.Name + "' declares an extern __shared__ " +
                                   "buffer but the launch passes no dynamic shared memory size " +
                                   "(third <<<>>> argument)");
                        }
                    }

                    // Argument count vs the kernel signature (only when the kernel is
                    // defined in this translation unit).
                    if (kernelSym != null && kernelSym.FuncDecl != null)
                    {
                        int expected = kernelSym.FuncDecl.Params.Count;
                        int actual = launch.Args.Count;
                        if (expected != actual)
                        {
                            Report(CudaDiagnosticCodes.LaunchArgCountMismatch, ParseSeverity.Error, launch.Span,
                                   "kernel '" + kernelSym.Name + "' expects " +
                                   Str(expected) + " argument(s) but is launched with " +
                                   Str(actual));
                        }
                    }
                });
            });
        }

        private static string Str(long v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// True when the body declares an unsized __shared__ array — the
        /// `extern __shared__ T buf[];` idiom that receives the launch's dynamic
        /// shared-memory allocation.
        /// </summary>
        private static bool HasUnsizedSharedArray(Stmt s)
        {
            if (s == null) return false;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i)
                    if (HasUnsizedSharedArray(c.Body[i])) return true;
                return false;
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                return v != null && v.Storage == StorageClass.Shared &&
                       v.Type != null && v.Type.IsArray && v.ArraySize == null;
            }
            IfStmt i2 = s as IfStmt;
            if (i2 != null) return HasUnsizedSharedArray(i2.Then) || HasUnsizedSharedArray(i2.Else);
            ForStmt f = s as ForStmt;
            if (f != null) return HasUnsizedSharedArray(f.Init) || HasUnsizedSharedArray(f.Body);
            WhileStmt w = s as WhileStmt;
            if (w != null) return HasUnsizedSharedArray(w.Body);
            DoStmt dw = s as DoStmt;
            if (dw != null) return HasUnsizedSharedArray(dw.Body);
            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) return HasUnsizedSharedArray(sw.Body);
            return false;
        }
    }
}
