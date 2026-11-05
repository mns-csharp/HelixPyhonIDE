using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.CudaVirtualCompiler.Translation
{
    /// <summary>
    /// Converts plain C/C++ loops into CUDA kernels and CUDA kernels back into sequential C/C++.
    ///
    /// The engine is AST-GUIDED but TEXT-PRESERVING: the parser locates the loop head, the thread
    /// index, the bounds guard and the body, and the rewrite splices the author's own body text
    /// into the other form. Nothing is pretty-printed, so a CUDA→CPU→CUDA round trip returns the
    /// original body character-for-character (modulo indentation), which is what makes the round
    /// trip a real test rather than a comparison of two pretty-printers.
    ///
    /// Per the CUDA decision model the shape is classified FIRST (reach, then collision), and a
    /// conversion that cannot be proved safe is REFUSED with a concrete dependence explanation
    /// instead of emitting plausible-looking wrong CUDA.
    /// </summary>
    public static class CudaTranslator
    {
        private const string IndexLine = "int {0} = blockIdx.x * blockDim.x + threadIdx.x;";

        // ===================== C/C++ → CUDA =====================

        /// <summary>
        /// Converts the first convertible <c>for</c> loop of <paramref name="functionName"/> (or the
        /// first function found) into a <c>__global__</c> kernel plus its launch scaffold.
        /// </summary>
        /// <param name="maxGridDimensions">
        /// How many levels of a perfect loop nest may become grid dimensions (1–3). The default
        /// lifts as much as the hardware allows; a round-trip check passes the dimensionality of
        /// the kernel it started from, so the comparison stays an identity rather than rewarding
        /// the engine for finding EXTRA parallelism the original author did not expose.
        /// </param>
        public static TranslationResult CpuToCuda(string source, string functionName = null,
            bool emitLaunchScaffold = true, int maxGridDimensions = 3)
        {
            if (string.IsNullOrEmpty(source)) return TranslationResult.Refuse(KernelShape.Unknown, "", "empty source");
            TranslationUnit tu = Parse(source);
            if (tu == null) return TranslationResult.Refuse(KernelShape.Unknown, "", "source did not parse");

            FunctionDecl fn = FindFunction(tu, functionName, requireHost: true);
            if (fn == null || fn.Body == null)
                return TranslationResult.Refuse(KernelShape.Unknown, "", "no host function with a body was found");

            ForStmt loop = FindFirstFor(fn.Body);
            if (loop == null)
                return TranslationResult.Refuse(KernelShape.Sequential, "no loop",
                    "function '" + fn.Name + "' has no for-loop to parallelise");

            // A chain of directly-nested loops maps onto the thread grid's dimensions: the
            // OUTERMOST loop becomes the slowest-varying axis (y), the innermost becomes x.
            if (maxGridDimensions < 1) maxGridDimensions = 1;
            if (maxGridDimensions > 3) maxGridDimensions = 3;
            List<ForStmt> nest = CollectPerfectNest(loop, maxGridDimensions);
            var loopVars = new List<string>();
            var bounds = new List<string>();
            var types = new List<string>();
            foreach (ForStmt lv in nest)
            {
                string v = LoopVariable(lv);
                string b = LoopUpperBound(lv, source);
                if (v == null || b == null) break;
                loopVars.Add(v);
                bounds.Add(b);
                // Keep the induction variable's own type. A `size_t` rewritten as `int` caps the
                // array at 2^31 elements and the failure appears only on data big enough to
                // matter, which is exactly when it is hardest to diagnose.
                types.Add(LoopVariableType(lv, source) ?? "int");
            }
            if (loopVars.Count == 0)
                return TranslationResult.Refuse(KernelShape.Unknown, "",
                    "the loop induction variable or its `" + LoopVariable(loop) + " < N` bound could not be identified");

            ForStmt innermost = nest[loopVars.Count - 1];
            var analyzer = new RegionAnalyzer();
            analyzer.Analyze(innermost.Body, loopVars);
            string reason;
            KernelShape shape = analyzer.Classify(out reason);

            TranslationResult refusal = RefuseIfUnsafe(shape, reason, analyzer, loopVars);
            if (refusal != null) return refusal;

            // The mirror of the CUDA→CPU rewrite: `continue` skips one element, which is a
            // thread returning early.
            int earlyExits;
            string bodyText = InnerTextWithContinuesAsReturn(innermost.Body, source, out earlyExits);
            string kernelName = fn.Name + "_kernel";
            string[] axes = { "x", "y", "z" };

            var sb = new StringBuilder();
            sb.Append("// shape: ").Append(shape).Append(" — ").Append(reason).Append('\n');
            sb.Append("__global__ void ").Append(kernelName).Append('(').Append(ParamText(fn, source)).Append(") {\n");
            // Innermost loop → x (fastest varying), so consecutive threads touch consecutive
            // elements and the loads coalesce.
            for (int d = 0; d < loopVars.Count; d++)
            {
                string v = loopVars[loopVars.Count - 1 - d];
                string a = axes[d];
                string t = types[loopVars.Count - 1 - d];
                sb.Append("    ").Append(t).Append(' ').Append(v).Append(" = blockIdx.").Append(a)
                  .Append(" * blockDim.").Append(a).Append(" + threadIdx.").Append(a).Append(";\n");
            }
            sb.Append("    if (");
            for (int d = 0; d < loopVars.Count; d++)
            {
                if (d > 0) sb.Append(" && ");
                string v = loopVars[loopVars.Count - 1 - d];
                string b = bounds[loopVars.Count - 1 - d];
                sb.Append(v).Append(" < ").Append(b);
            }
            sb.Append(") {\n");
            sb.Append(Reindent(bodyText, "        ")).Append('\n');
            sb.Append("    }\n");
            sb.Append("}\n");
            string bound = bounds[0];

            if (emitLaunchScaffold)
            {
                sb.Append('\n');
                sb.Append("// Launch: one thread per element, rounded up to whole blocks.\n");
                sb.Append("//   int threads = 256;\n");
                sb.Append("//   int blocks  = (").Append(bound).Append(" + threads - 1) / threads;\n");
                sb.Append("//   ").Append(kernelName).Append("<<<blocks, threads>>>(").Append(ArgText(fn)).Append(");\n");
                sb.Append("//   cudaDeviceSynchronize();   // and check cudaGetLastError()\n");
            }

            TranslationResult ok = TranslationResult.Ok(sb.ToString(), shape, reason);
            FillSets(ok, analyzer);
            if (earlyExits > 0)
                ok.Notes.Add(earlyExits + " 'continue' statement(s) became 'return': skipping an element "
                             + "is a thread doing nothing");
            ok.Notes.Add("verification oracle: keep '" + fn.Name + "' as the CPU reference and compare outputs");
            return ok;
        }

        // ===================== CUDA → C/C++ =====================

        /// <summary>
        /// Converts a <c>__global__</c> kernel back into an equivalent sequential C/C++ function:
        /// the thread index becomes the loop variable and the bounds guard becomes the loop bound.
        /// </summary>
        public static TranslationResult CudaToCpu(string source, string kernelName = null)
        {
            if (string.IsNullOrEmpty(source)) return TranslationResult.Refuse(KernelShape.Unknown, "", "empty source");
            TranslationUnit tu = Parse(source);
            if (tu == null) return TranslationResult.Refuse(KernelShape.Unknown, "", "source did not parse");

            FunctionDecl kernel = FindKernel(tu, kernelName);
            if (kernel == null || kernel.Body == null)
                return TranslationResult.Refuse(KernelShape.Unknown, "", "no __global__ kernel with a body was found");

            // The thread index/indices: `int i = blockIdx.x * blockDim.x + threadIdx.x;`
            List<string> indexVars = FindThreadIndices(kernel.Body, source);
            if (indexVars.Count == 0)
                return TranslationResult.Refuse(KernelShape.Unknown, "",
                    "kernel '" + kernel.Name + "' does not compute a thread index, so no loop bound can be derived");

            var analyzer = new RegionAnalyzer();
            analyzer.Analyze(kernel.Body, indexVars);
            string reason;
            KernelShape shape = analyzer.Classify(out reason);

            if (analyzer.HasSharedMemory || analyzer.HasSyncthreads)
                return TranslationResult.Refuse(shape, reason,
                    "kernel '" + kernel.Name + "' cooperates through __shared__ memory and barriers; "
                    + "serialising it would change the algorithm, not just its schedule — port the tiled form by hand");

            // The bounds guard, positive or negative form; its body is the per-element work.
            var bounds = new Dictionary<string, string>(StringComparer.Ordinal);
            Stmt work;
            List<Stmt> prologue;
            bool hasEpilogue;
            if (!FindGuard(kernel.Body, indexVars, source, bounds, out work, out prologue, out hasEpilogue))
                return TranslationResult.Refuse(shape, reason,
                    "kernel '" + kernel.Name + "' has no bounds guard on its thread index, so the "
                    + "element count cannot be recovered — the CPU loop bound would be a guess");

            // Statements AFTER the guarded block run for every thread, in-range or not. Folding
            // them into a bounded loop would change who executes them, so they are refused rather
            // than quietly dropped — dropping them is how a translation loses half a kernel.
            if (hasEpilogue)
                return TranslationResult.Refuse(shape, reason,
                    "kernel '" + kernel.Name + "' has statements after its bounds guard: those run for "
                    + "every thread, including the out-of-range ones, and have no place in a bounded loop");

            // Statements BEFORE the guard are per-thread setup. They can move inside the loop as
            // long as they are plain local declarations: no side effects, and nothing that reads
            // the thread geometry (a `blockDim.x * gridDim.x` stride has no sequential meaning).
            string prologueText = string.Empty;
            foreach (Stmt p in prologue)
            {
                string bad = UnliftablePrologue(p, source);
                if (bad != null)
                    return TranslationResult.Refuse(shape, reason,
                        "kernel '" + kernel.Name + "' computes '" + bad + "' before its bounds guard; "
                        + "that statement has no place in a sequential loop, and silently dropping it "
                        + "would change what the code does");
                string t = Slice(source, p.Span);
                if (!string.IsNullOrEmpty(t)) prologueText += t.Trim() + "\n";
            }

            // A kernel's bare `return;` means "this THREAD contributes nothing". Spliced
            // unchanged into a loop it would mean "abandon every REMAINING element" — a silent
            // wrong answer. The sequential equivalent of a per-thread early exit is `continue`.
            int earlyExits;
            string bodyText = InnerTextWithReturnsAsContinue(work, source, out earlyExits);

            // A kernel that inspects its own parallel identity — printing threadIdx, using
            // warpSize or blockDim inside the WORK rather than just to compute an index — has no
            // sequential equivalent: those values only exist because the code is running on a
            // grid. Refuse rather than emit a CPU function that quietly means something else.
            string intrinsic = FindExecutionIntrinsic(bodyText);
            if (intrinsic != null)
                return TranslationResult.Refuse(shape, reason,
                    "kernel '" + kernel.Name + "' uses '" + intrinsic + "' in its body, not merely to compute "
                    + "an index: it observes the thread geometry itself, which a sequential loop does not have");

            string cpuName = kernel.Name + "_cpu";

            // One loop per index variable, ordered by the AXIS each one rides — z outermost,
            // x innermost — so the sequential form walks memory the way the fastest-varying
            // thread dimension does.
            //
            // Declaration order is NOT a proxy for this. Plenty of real kernels declare the row
            // first (`int i = …threadIdx.y; int j = …threadIdx.x;`), and ordering by declaration
            // then puts the row on x when the code is lifted back, which silently un-coalesces
            // the kernel and breaks the round-trip identity. Found by sweeping cuda_lab /
            // cudaMMC / CUDALibrarySamples; see docs/cpu-cuda-translation.md.
            var loopVars = new List<string>();
            foreach (string v in indexVars) if (bounds.ContainsKey(v)) loopVars.Add(v);
            if (loopVars.Count == 0)
                return TranslationResult.Refuse(shape, reason,
                    "kernel '" + kernel.Name + "' guards an index this pass could not match to a loop bound");
            OrderByAxisOutermostFirst(loopVars, kernel.Body, source);

            var sb = new StringBuilder();
            sb.Append("// shape: ").Append(shape).Append(" — ").Append(reason).Append('\n');
            sb.Append("// sequential reference for kernel '").Append(kernel.Name).Append("'\n");
            sb.Append("void ").Append(cpuName).Append('(').Append(ParamText(kernel, source)).Append(") {\n");
            Dictionary<string, string> indexTypes = ThreadIndexTypes(kernel.Body, source);
            string indent = "    ";
            foreach (string v in loopVars)
            {
                string t;
                if (!indexTypes.TryGetValue(v, out t) || string.IsNullOrEmpty(t) || t == "auto") t = "int";
                sb.Append(indent).Append("for (").Append(t).Append(' ').Append(v).Append(" = 0; ").Append(v).Append(" < ")
                  .Append(bounds[v]).Append("; ++").Append(v).Append(") {\n");
                indent += "    ";
            }
            if (prologueText.Length > 0) sb.Append(Reindent(prologueText.TrimEnd(), indent)).Append('\n');
            sb.Append(Reindent(bodyText, indent)).Append('\n');
            for (int d = loopVars.Count - 1; d >= 0; d--)
            {
                indent = indent.Substring(4);
                sb.Append(indent).Append("}\n");
            }
            sb.Append("}\n");

            TranslationResult ok = TranslationResult.Ok(sb.ToString(), shape, reason);
            FillSets(ok, analyzer);
            if (earlyExits > 0)
                ok.Notes.Add(earlyExits + " per-thread early exit(s) became 'continue': a thread that "
                             + "returns contributes nothing, which is one skipped iteration here");
            if (analyzer.HasAtomic)
                ok.Notes.Add("atomics become plain accumulation in the sequential form — that is exact, "
                             + "but the GPU's floating-point summation order will differ");
            return ok;
        }

        /// <summary>
        /// How many grid dimensions a kernel uses — the count of its thread-index variables.
        /// Zero when the kernel computes no thread index.
        /// </summary>
        public static int KernelGridDimensions(string source, string kernelName)
        {
            TranslationUnit tu = Parse(source);
            if (tu == null) return 0;
            FunctionDecl k = FindKernel(tu, kernelName);
            if (k == null || k.Body == null) return 0;
            return FindThreadIndices(k.Body, source).Count;
        }

        /// <summary>Shape classification alone, for the refactoring UI.</summary>
        public static KernelShape ClassifyKernel(string source, string kernelName, out string reason,
            out IList<string> hazards)
        {
            reason = string.Empty;
            hazards = new List<string>();
            TranslationUnit tu = Parse(source);
            if (tu == null) return KernelShape.Unknown;
            FunctionDecl fn = FindKernel(tu, kernelName) ?? FindFunction(tu, kernelName, requireHost: false);
            if (fn == null || fn.Body == null) return KernelShape.Unknown;
            string idx;
            string init;
            string loopVar = FindThreadIndex(fn.Body, source, out idx, out init) ? idx : LoopVariableOfFirstFor(fn.Body);
            var an = new RegionAnalyzer();
            an.Analyze(fn.Body, loopVar);
            KernelShape shape = an.Classify(out reason);
            hazards = an.Hazards;
            return shape;
        }

        // ===================== refusal policy =====================

        /// <summary>
        /// The chain of directly-nested `for` loops starting at <paramref name="outer"/> — a
        /// perfect nest, i.e. each level's body is nothing but the next loop. An imperfect nest
        /// (statements between the levels) stops the chain, because those statements would have
        /// to run once per outer iteration, not once per thread.
        /// </summary>
        private static List<ForStmt> CollectPerfectNest(ForStmt outer, int maxDepth)
        {
            var nest = new List<ForStmt> { outer };
            ForStmt cur = outer;
            while (nest.Count < maxDepth)
            {
                Stmt body = cur.Body;
                CompoundStmt c = body as CompoundStmt;
                if (c != null)
                {
                    if (c.Body.Count != 1) break;
                    body = c.Body[0];
                }
                ForStmt inner = body as ForStmt;
                if (inner == null) break;
                nest.Add(inner);
                cur = inner;
            }
            return nest;
        }

        private static TranslationResult RefuseIfUnsafe(KernelShape shape, string reason,
            RegionAnalyzer an, IList<string> loopVars)
        {
            switch (shape)
            {
                case KernelShape.Scan:
                    return TranslationResult.Refuse(shape, reason,
                        "loop-carried dependence: " + reason
                        + ". A prefix/recurrence cannot become a one-thread-per-element kernel; "
                        + "use a parallel scan (or thrust::inclusive_scan) instead");
                case KernelShape.Scatter:
                    return TranslationResult.Refuse(shape, reason,
                        "write collisions: " + reason
                        + ". Convert only with atomics, privatisation, or by restructuring the write pattern");
                case KernelShape.Reduce:
                    // One thread per element is exactly the wrong shape here: every thread would
                    // read-modify-write the same accumulator, so the answer depends on the
                    // schedule. A reduction needs a tree (or an atomic), which is a different
                    // algorithm — not a re-scheduling of this one.
                    return TranslationResult.Refuse(shape, reason,
                        "accumulation into one location: " + reason
                        + ". Every thread would update the same accumulator, so the result would depend on "
                        + "the order the threads happened to run. Use a parallel reduction — gpu::reduce, "
                        + "thrust::reduce, or cub::DeviceReduce");
                case KernelShape.Sequential:
                    return TranslationResult.Refuse(shape, reason, "no parallel structure was found");
            }
            // `continue` is fine — it skips ONE iteration, which is a thread returning early,
            // and CpuToCuda rewrites it to `return;`. `break` ends the whole loop, and no thread
            // can end the other threads.
            if (an.HasBreak)
                return TranslationResult.Refuse(shape, reason,
                    "the loop body contains 'break': ending the whole loop has no one-thread-per-element "
                    + "equivalent, because a thread cannot stop the other threads");
            if (an.HasEarlyReturn)
                return TranslationResult.Refuse(shape, reason,
                    "the loop body returns from the enclosing function, which a kernel cannot do");
            foreach (string v in loopVars)
                if (an.ScalarWrites.Contains(v))
                    return TranslationResult.Refuse(shape, reason,
                        "the loop variable '" + v + "' is written inside the body, so iterations are not independent");
            return null;
        }

        private static void FillSets(TranslationResult r, RegionAnalyzer an)
        {
            var seenR = new HashSet<string>(StringComparer.Ordinal);
            var seenW = new HashSet<string>(StringComparer.Ordinal);
            foreach (Access a in an.Accesses)
            {
                if (a.IsWrite) { if (seenW.Add(a.Array)) r.Writes.Add(a.Array); }
                else { if (seenR.Add(a.Array)) r.Reads.Add(a.Array); }
            }
            foreach (string h in an.Hazards) r.Hazards.Add(h);
        }

        // ===================== AST helpers =====================

        private static TranslationUnit Parse(string source)
        {
            var opts = new CudaCompileOptions
            {
                EnableLexParse = true,
                EnableAstValidator = false,
                EnableSymtablePass = false,
                EnableQualifierSync = false,
                EnableLaunchConfig = false,
                EnableMemorySpace = false,
                EnableLint = false,
                EnableLanguageStandard = false,
                EnableNameResolution = false,
                EnableIncludeResolution = false,
            };
            CudaCompileResult r = CudaVirtualCompiler.CompileSource(source, "translate.cu", opts);
            return r == null ? null : r.Unit;
        }

        private static FunctionDecl FindKernel(TranslationUnit tu, string name)
        {
            foreach (FunctionDecl f in AllFunctions(tu))
            {
                if (f.Space != FuncSpace.Global) continue;
                if (name == null || f.Name == name) return f;
            }
            return null;
        }

        private static FunctionDecl FindFunction(TranslationUnit tu, string name, bool requireHost)
        {
            foreach (FunctionDecl f in AllFunctions(tu))
            {
                if (requireHost && f.Space == FuncSpace.Global) continue;
                if (f.Body == null) continue;
                if (name == null || f.Name == name) return f;
            }
            return null;
        }

        private static IEnumerable<FunctionDecl> AllFunctions(TranslationUnit tu)
        {
            var stack = new Stack<Decl>();
            foreach (Decl d in tu.Decls) stack.Push(d);
            while (stack.Count > 0)
            {
                Decl d = stack.Pop();
                FunctionDecl f = d as FunctionDecl;
                if (f != null) { yield return f; continue; }
                NamespaceDecl ns = d as NamespaceDecl;
                if (ns != null) { foreach (Decl c in ns.Decls) stack.Push(c); continue; }
                TemplateDecl t = d as TemplateDecl;
                if (t != null && t.Inner != null) { stack.Push(t.Inner); continue; }
                RecordDecl r = d as RecordDecl;
                if (r != null) { foreach (Decl c in r.Members) stack.Push(c); }
            }
        }

        private static ForStmt FindFirstFor(Stmt s)
        {
            if (s == null) return null;
            ForStmt f = s as ForStmt;
            if (f != null) return f;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                foreach (Stmt x in c.Body) { ForStmt r = FindFirstFor(x); if (r != null) return r; }
                return null;
            }
            IfStmt i = s as IfStmt;
            if (i != null) return FindFirstFor(i.Then) ?? FindFirstFor(i.Else);
            return null;
        }

        private static string LoopVariableOfFirstFor(Stmt body)
        {
            ForStmt f = FindFirstFor(body);
            return f == null ? null : LoopVariable(f);
        }

        /// <summary>
        /// The induction variable's declared type, sliced from the source so a spelling like
        /// `std::size_t` or `unsigned long long` survives verbatim. Null when the loop reuses a
        /// variable declared elsewhere (`for (i = 0; …)`), where there is no type to read.
        /// </summary>
        private static string LoopVariableType(ForStmt loop, string source)
        {
            DeclStmt d = loop.Init as DeclStmt;
            if (d == null)
            {
                CompoundStmt c = loop.Init as CompoundStmt;
                if (c != null && c.Body.Count > 0) d = c.Body[0] as DeclStmt;
            }
            if (d == null) return null;
            VarDecl v = d.Decl as VarDecl;
            if (v == null || string.IsNullOrEmpty(v.Name)) return null;
            return DeclaredTypeText(source, v);
        }

        /// <summary>The text between a declaration's start and its name — i.e. its type.</summary>
        private static string DeclaredTypeText(string source, VarDecl v)
        {
            string text = Slice(source, v.Span);
            if (string.IsNullOrEmpty(text)) return null;
            // The name must be matched as a WHOLE WORD followed by `=`, `[`, `,` or `;`.
            // A plain IndexOf finds the `i` inside `size_t` and reports the type as "s" —
            // which then compiles as nothing at all. Caught on nvCOMP's `initialize`.
            Match at2 = Regex.Match(text, @"\b" + Regex.Escape(v.Name) + @"\b\s*(?=[=\[,;]|$)");
            if (!at2.Success || at2.Index <= 0) return null;
            string type = text.Substring(0, at2.Index).Trim();
            // A loop counter is incremented, so `const` cannot come along; `auto` is kept because
            // the initializer still deduces it.
            type = Regex.Replace(type, @"\bconst\b", " ").Trim();
            type = Regex.Replace(type, @"\s+", " ");
            if (type.Length == 0) return null;
            // In `int x = …, y = …;` every declarator shares the STATEMENT's span, so slicing up
            // to the second name yields "int x = …," — not a type. Accept only something that
            // actually looks like one; anything else falls back to the caller's default.
            return Regex.IsMatch(type, @"^(?:(?:unsigned|signed|long|short|static|auto|std::)\s+)*[A-Za-z_][\w:]*$")
                ? type : null;
        }

        private static string LoopVariable(ForStmt loop)
        {
            DeclStmt d = loop.Init as DeclStmt;
            if (d != null) { VarDecl v = d.Decl as VarDecl; if (v != null) return v.Name; }
            CompoundStmt c = loop.Init as CompoundStmt;
            if (c != null && c.Body.Count > 0)
            {
                DeclStmt d2 = c.Body[0] as DeclStmt;
                if (d2 != null) { VarDecl v2 = d2.Decl as VarDecl; if (v2 != null) return v2.Name; }
            }
            ExprStmt e = loop.Init as ExprStmt;
            if (e != null)
            {
                BinaryExpr b = RegionAnalyzer.Unwrap(e.Expr) as BinaryExpr;
                if (b != null && b.Op == BinOpKind.Assign)
                {
                    NameExpr n = RegionAnalyzer.Unwrap(b.Lhs) as NameExpr;
                    if (n != null) return n.Id;
                }
            }
            return null;
        }

        /// <summary>The `N` of a `i &lt; N` loop condition, as source text.</summary>
        private static string LoopUpperBound(ForStmt loop, string source)
        {
            BinaryExpr b = RegionAnalyzer.Unwrap(loop.Cond) as BinaryExpr;
            if (b == null) return null;
            if (b.Op != BinOpKind.Lt && b.Op != BinOpKind.Le) return null;
            string rhs = Slice(source, b.Rhs.Span);
            if (string.IsNullOrEmpty(rhs)) return null;
            return b.Op == BinOpKind.Le ? "(" + rhs.Trim() + ") + 1" : rhs.Trim();
        }

        /// <summary>Finds `int i = blockIdx.x * blockDim.x + threadIdx.x;` (any spelling).</summary>
        private static bool FindThreadIndex(Stmt body, string source, out string indexVar, out string initText)
        {
            indexVar = null; initText = null;
            List<string> vars = FindThreadIndices(body, source);
            if (vars.Count == 0) return false;
            indexVar = vars[0];
            return true;
        }

        /// <summary>
        /// Every thread-index variable of the kernel, in declaration order. A 2-D kernel declares
        /// two (`int x = …threadIdx.x, y = …threadIdx.y;`), which become nested loops on the CPU.
        /// Declarations are found at any depth, so `const int i = …` inside a block still counts.
        /// </summary>
        private static List<string> FindThreadIndices(Stmt body, string source)
        {
            var vars = new List<string>();
            CollectThreadIndices(body, source, vars);
            return vars;
        }

        /// <summary>
        /// Sorts index variables so the slowest-varying axis is the outermost loop (z, then y,
        /// then x). Variables whose axis cannot be read keep their relative order and sit
        /// outside the ones that can — and when NO axis is readable this falls back to the old
        /// last-declared-outermost rule, which is what a 1-D kernel needs anyway.
        /// </summary>
        private static void OrderByAxisOutermostFirst(List<string> loopVars, Stmt body, string source)
        {
            Dictionary<string, int> rank = ThreadIndexAxes(body, source);
            bool anyKnown = false;
            foreach (string v in loopVars) if (rank.ContainsKey(v)) { anyKnown = true; break; }
            if (!anyKnown) { loopVars.Reverse(); return; }

            // Stable insertion sort: descending axis rank (z=2 outermost … x=0 innermost),
            // unknown axes treated as outermost so they never end up on the fast dimension.
            var ordered = new List<string>(loopVars);
            ordered.Sort(delegate (string a, string b)
            {
                int ra, rb;
                if (!rank.TryGetValue(a, out ra)) ra = 3;
                if (!rank.TryGetValue(b, out rb)) rb = 3;
                if (ra != rb) return rb.CompareTo(ra);
                return loopVars.IndexOf(a).CompareTo(loopVars.IndexOf(b));
            });
            loopVars.Clear();
            loopVars.AddRange(ordered);
        }

        /// <summary>Index variable → axis rank (x=0, y=1, z=2), read from its own initializer.</summary>
        private static Dictionary<string, int> ThreadIndexAxes(Stmt body, string source)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            CollectThreadIndexAxes(body, source, map);
            return map;
        }

        private static void CollectThreadIndexAxes(Stmt s, string source, Dictionary<string, int> into)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null) { foreach (Stmt x in c.Body) CollectThreadIndexAxes(x, source, into); return; }
            DeclStmt d = s as DeclStmt;
            if (d == null) return;
            VarDecl v = d.Decl as VarDecl;
            if (v == null || v.Initializer == null || into.ContainsKey(v.Name)) return;
            string init = Slice(source, v.Initializer.Span);
            if (init == null) return;
            int at = init.IndexOf("threadIdx.", StringComparison.Ordinal);
            if (at < 0) return;
            char axis = at + 10 < init.Length ? init[at + 10] : '\0';
            if (axis == 'x') into[v.Name] = 0;
            else if (axis == 'y') into[v.Name] = 1;
            else if (axis == 'z') into[v.Name] = 2;
        }

        private static void CollectThreadIndices(Stmt s, string source, List<string> into)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null) { foreach (Stmt x in c.Body) CollectThreadIndices(x, source, into); return; }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null && v.Initializer != null)
                {
                    string init = Slice(source, v.Initializer.Span);
                    if (init != null && init.IndexOf("threadIdx", StringComparison.Ordinal) >= 0
                        && !into.Contains(v.Name))
                        into.Add(v.Name);
                }
                return;
            }
            // A guard's body may hold further index maths, but the index itself is declared
            // before any guard — no deeper walk is needed for correctness here.
        }

        /// <summary>
        /// The kernel's bounds guard, in either idiom:
        ///   positive — `if (i &lt; n) { … }`, the work is the guarded block;
        ///   negative — `if (i &gt;= n) return;`, the work is everything after it.
        /// For a 2-D kernel the negative form usually tests both axes at once
        /// (`if (x &gt;= W || y &gt;= H) return;`), which yields one bound per index variable.
        /// </summary>
        private static bool FindGuard(Stmt body, IList<string> indexVars, string source,
            Dictionary<string, string> bounds, out Stmt work,
            out List<Stmt> prologue, out bool hasEpilogue)
        {
            work = null;
            prologue = new List<Stmt>();
            hasEpilogue = false;
            CompoundStmt c = body as CompoundStmt;
            if (c == null) return false;

            for (int k = 0; k < c.Body.Count; k++)
            {
                IfStmt i = c.Body[k] as IfStmt;
                if (i == null || i.Else != null) continue;

                // Positive form: the guarded block is the work.
                if (CollectBounds(i.Cond, indexVars, source, bounds, positive: true))
                {
                    work = i.Then;
                    CollectPrologue(c, k, indexVars, source, prologue);
                    hasEpilogue = k + 1 < c.Body.Count;
                    return true;
                }

                // Negative form: `if (out of range) return;` — the work is the remaining
                // statements of the kernel body.
                if (IsReturnOnly(i.Then) && CollectBounds(i.Cond, indexVars, source, bounds, positive: false))
                {
                    var rest = new List<Stmt>();
                    for (int j = k + 1; j < c.Body.Count; j++) rest.Add(c.Body[j]);
                    if (rest.Count == 0) return false;
                    work = new CompoundStmt(rest,
                        new SourceSpan(rest[0].Span.Start, rest[rest.Count - 1].Span.End));
                    CollectPrologue(c, k, indexVars, source, prologue);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Statements before the guard, minus the thread-index declarations themselves.</summary>
        private static void CollectPrologue(CompoundStmt c, int guardAt, IList<string> indexVars,
            string source, List<Stmt> into)
        {
            for (int j = 0; j < guardAt; j++)
            {
                Stmt s = c.Body[j];

                // `int x = …threadIdx.x, y = …threadIdx.y;` parses into a SYNTHETIC GROUP of
                // DeclStmts, not one. Treat its members individually, or a perfectly ordinary
                // two-index kernel looks like it has an unliftable statement before its guard.
                CompoundStmt group = s as CompoundStmt;
                if (group != null && group.IsSyntheticGroup)
                {
                    var rest = new List<Stmt>();
                    bool sawIndex = false;
                    foreach (Stmt inner in group.Body)
                    {
                        DeclStmt id = inner as DeclStmt;
                        VarDecl iv = id != null ? id.Decl as VarDecl : null;
                        if (iv != null && indexVars.Contains(iv.Name)) { sawIndex = true; continue; }
                        rest.Add(inner);
                    }
                    // Every member shares the whole statement's span, so a MIXED group cannot be
                    // sliced into "keep this declarator, drop that one". Hand the group over
                    // whole and let it be refused rather than emit a duplicated declaration.
                    if (sawIndex && rest.Count > 0) into.Add(group);
                    else into.AddRange(rest);
                    continue;
                }

                DeclStmt d = s as DeclStmt;
                VarDecl v = d != null ? d.Decl as VarDecl : null;
                if (v != null && indexVars.Contains(v.Name)) continue;   // the index itself
                into.Add(s);
            }
        }

        /// <summary>
        /// Null when a pre-guard statement can safely move inside the loop; otherwise the offending
        /// text. Only side-effect-free local declarations qualify: a call, an assignment or a read
        /// of the thread geometry means something a sequential loop cannot reproduce.
        /// </summary>
        private static string UnliftablePrologue(Stmt s, string source)
        {
            string text = Slice(source, s.Span);
            string shown = string.IsNullOrEmpty(text) ? s.GetType().Name : Regex.Replace(text.Trim(), @"\s+", " ");
            if (shown.Length > 60) shown = shown.Substring(0, 60) + "…";

            DeclStmt d = s as DeclStmt;
            if (d == null) return shown;
            VarDecl v = d.Decl as VarDecl;
            if (v == null) return shown;
            if (v.Storage == StorageClass.Shared) return shown;
            if (v.Initializer == null) return null;   // `float acc;` — harmless

            string init = Slice(source, v.Initializer.Span) ?? string.Empty;
            foreach (string intrinsic in ExecutionIntrinsics)
                if (init.IndexOf(intrinsic, StringComparison.Ordinal) >= 0) return shown;
            if (init.IndexOf('(') >= 0 && Regex.IsMatch(init, @"\w\s*\(")) return shown;  // a call
            if (Regex.IsMatch(init, @"(?<![=!<>+\-*/%&|^])=(?!=)")) return shown;          // an assignment
            return null;
        }

        /// <summary>Each thread-index variable's declared type text (`size_t`, `unsigned`, …).</summary>
        private static Dictionary<string, string> ThreadIndexTypes(Stmt body, string source)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectThreadIndexTypes(body, source, map);
            return map;
        }

        private static void CollectThreadIndexTypes(Stmt s, string source, Dictionary<string, string> into)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null) { foreach (Stmt x in c.Body) CollectThreadIndexTypes(x, source, into); return; }
            DeclStmt d = s as DeclStmt;
            if (d == null) return;
            VarDecl v = d.Decl as VarDecl;
            if (v == null || v.Initializer == null || into.ContainsKey(v.Name)) return;
            string init = Slice(source, v.Initializer.Span);
            if (init == null || init.IndexOf("threadIdx", StringComparison.Ordinal) < 0) return;
            string t = DeclaredTypeText(source, v);
            if (t != null) into[v.Name] = t;
        }

        private static bool IsReturnOnly(Stmt s)
        {
            ReturnStmt r = s as ReturnStmt;
            if (r != null) return r.Value == null;
            CompoundStmt c = s as CompoundStmt;
            return c != null && c.Body.Count == 1 && IsReturnOnly(c.Body[0]);
        }

        /// <summary>
        /// Reads `i &lt; n` (positive) or `i &gt;= n` (negative) comparisons out of a guard
        /// condition, including `||`/`&amp;&amp;` chains, into <paramref name="bounds"/>.
        /// </summary>
        private static bool CollectBounds(Expr cond, IList<string> indexVars, string source,
            Dictionary<string, string> bounds, bool positive)
        {
            BinaryExpr b = RegionAnalyzer.Unwrap(cond) as BinaryExpr;
            if (b == null) return false;

            if (b.Op == BinOpKind.LogOr || b.Op == BinOpKind.LogAnd)
            {
                bool left = CollectBounds(b.Lhs, indexVars, source, bounds, positive);
                bool right = CollectBounds(b.Rhs, indexVars, source, bounds, positive);
                return left || right;
            }

            bool wanted = positive
                ? (b.Op == BinOpKind.Lt || b.Op == BinOpKind.Le)
                : (b.Op == BinOpKind.Ge || b.Op == BinOpKind.Gt);
            if (!wanted) return false;

            NameExpr lhs = RegionAnalyzer.Unwrap(b.Lhs) as NameExpr;
            if (lhs == null || !indexVars.Contains(lhs.Id)) return false;
            string rhs = Slice(source, b.Rhs.Span);
            if (string.IsNullOrEmpty(rhs)) return false;
            rhs = rhs.Trim();
            // `i <= n` and `i > n` both mean the loop runs to n+1.
            if (b.Op == BinOpKind.Le || b.Op == BinOpKind.Gt) rhs = "(" + rhs + ") + 1";
            if (!bounds.ContainsKey(lhs.Id)) bounds[lhs.Id] = rhs;
            return true;
        }

        /// <summary>The kernel body minus its thread-index declaration (when there is no guard).</summary>
        private static Stmt StripIndexDecl(Stmt body)
        {
            CompoundStmt c = body as CompoundStmt;
            if (c == null) return body;
            var kept = new List<Stmt>();
            foreach (Stmt s in c.Body)
            {
                DeclStmt d = s as DeclStmt;
                if (d != null)
                {
                    VarDecl v = d.Decl as VarDecl;
                    if (v != null && v.Initializer != null && v.Initializer.Span.Start.Offset >= 0)
                    {
                        // keep — the caller slices text, so filtering happens textually below
                    }
                }
                kept.Add(s);
            }
            return c;
        }

        private static readonly string[] ExecutionIntrinsics =
        {
            "threadIdx", "blockIdx", "blockDim", "gridDim", "warpSize",
            "__syncthreads", "__syncwarp", "__shfl", "__ballot", "__activemask", "atomicAdd",
            "atomicSub", "atomicExch", "atomicCAS", "atomicMin", "atomicMax", "__laneid",
        };

        /// <summary>The first CUDA execution intrinsic used inside a kernel's WORK, or null.</summary>
        private static string FindExecutionIntrinsic(string bodyText)
        {
            if (string.IsNullOrEmpty(bodyText)) return null;
            foreach (string s in ExecutionIntrinsics)
                if (bodyText.IndexOf(s, StringComparison.Ordinal) >= 0) return s;
            return null;
        }

        private static string ParamText(FunctionDecl fn, string source)
        {
            var parts = new List<string>();
            foreach (Parameter p in fn.Params)
            {
                string text = Slice(source, p.Span);
                parts.Add(string.IsNullOrEmpty(text) ? (p.Type.ToString() + " " + p.Name) : text.Trim());
            }
            return string.Join(", ", parts.ToArray());
        }

        private static string ArgText(FunctionDecl fn)
        {
            var parts = new List<string>();
            foreach (Parameter p in fn.Params) parts.Add(p.Name);
            return string.Join(", ", parts.ToArray());
        }

        // ===================== text helpers =====================

        private static string Slice(string source, SourceSpan span)
        {
            int a = span.Start.Offset, b = span.End.Offset;
            if (a < 0 || b > source.Length || b <= a) return null;
            return source.Substring(a, b - a);
        }

        /// <summary>
        /// As <see cref="InnerText"/>, but every statement-level bare <c>return;</c> is rewritten
        /// to <c>continue;</c> — the sequential equivalent of a thread exiting early. Returns are
        /// located on the AST (so returns inside a nested lambda body are left alone) and the
        /// replacement is applied to their exact source spans, keeping the rest verbatim.
        /// </summary>
        private static string InnerTextWithReturnsAsContinue(Stmt work, string source, out int count)
        {
            count = 0;
            if (work == null) return string.Empty;
            var returns = new List<SourceSpan>();
            CollectBareReturns(work, returns);
            if (returns.Count == 0) return InnerText(work, source);

            int lo = work.Span.Start.Offset, hi = work.Span.End.Offset;
            if (lo < 0 || hi > source.Length || hi <= lo) return InnerText(work, source);

            returns.Sort((a, b) => b.Start.Offset.CompareTo(a.Start.Offset));   // last first
            var sb = new StringBuilder(source.Substring(lo, hi - lo));
            foreach (SourceSpan s in returns)
            {
                int start = s.Start.Offset - lo, end = s.End.Offset - lo;
                if (start < 0 || end > sb.Length || end <= start) continue;
                sb.Remove(start, end - start);
                sb.Insert(start, "continue;");
                count++;
            }

            string text = sb.ToString().Trim();
            if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                text = text.Substring(1, text.Length - 2);
            return Dedent(text);
        }

        /// <summary>
        /// The mirror of <see cref="InnerTextWithReturnsAsContinue"/>: statement-level
        /// <c>continue;</c> becomes <c>return;</c>, because skipping one iteration is exactly a
        /// thread doing nothing. A `continue` belonging to a NESTED loop is left alone — it means
        /// "next inner iteration" there, which the kernel keeps verbatim.
        /// </summary>
        private static string InnerTextWithContinuesAsReturn(Stmt work, string source, out int count)
        {
            count = 0;
            if (work == null) return string.Empty;
            var spans = new List<SourceSpan>();
            CollectBareContinues(work, spans);
            if (spans.Count == 0) return InnerText(work, source);

            int lo = work.Span.Start.Offset, hi = work.Span.End.Offset;
            if (lo < 0 || hi > source.Length || hi <= lo) return InnerText(work, source);

            spans.Sort((a, b) => b.Start.Offset.CompareTo(a.Start.Offset));
            var sb = new StringBuilder(source.Substring(lo, hi - lo));
            foreach (SourceSpan s in spans)
            {
                int start = s.Start.Offset - lo, end = s.End.Offset - lo;
                if (start < 0 || end > sb.Length || end <= start) continue;
                sb.Remove(start, end - start);
                sb.Insert(start, "return;");
                count++;
            }

            string text = sb.ToString().Trim();
            if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                text = text.Substring(1, text.Length - 2);
            return Dedent(text);
        }

        /// <summary>`continue;` bound to THIS loop level (nested loops own their own).</summary>
        private static void CollectBareContinues(Stmt s, List<SourceSpan> into)
        {
            if (s == null) return;
            if (s is ContinueStmt) { into.Add(s.Span); return; }
            CompoundStmt c = s as CompoundStmt;
            if (c != null) { foreach (Stmt x in c.Body) CollectBareContinues(x, into); return; }
            IfStmt i = s as IfStmt;
            if (i != null) { CollectBareContinues(i.Then, into); CollectBareContinues(i.Else, into); return; }
            // for/while/do/switch bind their own continue/break — do not descend.
        }

        /// <summary>Statement-level `return;` nodes (no value), not descending into expressions.</summary>
        private static void CollectBareReturns(Stmt s, List<SourceSpan> into)
        {
            if (s == null) return;
            ReturnStmt r = s as ReturnStmt;
            if (r != null) { if (r.Value == null) into.Add(r.Span); return; }
            CompoundStmt c = s as CompoundStmt;
            if (c != null) { foreach (Stmt x in c.Body) CollectBareReturns(x, into); return; }
            IfStmt i = s as IfStmt;
            if (i != null) { CollectBareReturns(i.Then, into); CollectBareReturns(i.Else, into); return; }
            ForStmt f = s as ForStmt;
            if (f != null) { CollectBareReturns(f.Body, into); return; }
            WhileStmt w = s as WhileStmt;
            if (w != null) { CollectBareReturns(w.Body, into); return; }
            DoStmt d = s as DoStmt;
            if (d != null) { CollectBareReturns(d.Body, into); return; }
            SwitchStmt sw = s as SwitchStmt;
            // A `return` inside a switch would become `continue`, which changes which construct
            // it leaves — refuse to touch those and let the caller's checks handle it.
            if (sw != null) return;
        }

        /// <summary>The text INSIDE a compound statement's braces (or the statement itself).</summary>
        private static string InnerText(Stmt s, string source)
        {
            if (s == null) return string.Empty;
            string text = Slice(source, s.Span);
            if (text == null) return string.Empty;
            text = text.Trim();
            if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                text = text.Substring(1, text.Length - 2);
            return Dedent(text);
        }

        private static string Dedent(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int min = int.MaxValue;
            foreach (string l in lines)
            {
                if (l.Trim().Length == 0) continue;
                int n = 0;
                while (n < l.Length && (l[n] == ' ' || l[n] == '\t')) n++;
                if (n < min) min = n;
            }
            if (min == int.MaxValue || min == 0) return TrimBlankEdges(lines);
            var sb = new List<string>();
            foreach (string l in lines) sb.Add(l.Length >= min ? l.Substring(min) : l.TrimStart());
            return TrimBlankEdges(sb.ToArray());
        }

        private static string TrimBlankEdges(string[] lines)
        {
            int s = 0, e = lines.Length - 1;
            while (s <= e && lines[s].Trim().Length == 0) s++;
            while (e >= s && lines[e].Trim().Length == 0) e--;
            var kept = new List<string>();
            for (int i = s; i <= e; i++) kept.Add(lines[i]);
            return string.Join("\n", kept.ToArray());
        }

        private static string Reindent(string text, string indent)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var sb = new List<string>();
            foreach (string l in lines) sb.Add(l.Trim().Length == 0 ? string.Empty : indent + l);
            return string.Join("\n", sb.ToArray());
        }

        /// <summary>
        /// Canonical form for comparing two pieces of code: comments and layout removed, so a
        /// round trip is judged on the CODE it produces, not on trivia. Trailing comments sit
        /// outside a statement's span and are legitimately lost by an AST-guided rewrite.
        /// </summary>
        public static string Normalize(string code)
        {
            if (code == null) return string.Empty;
            var stripped = new StringBuilder(code.Length);
            for (int i = 0; i < code.Length; i++)
            {
                char ch = code[i];
                if (ch == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    while (i < code.Length && code[i] != '\n') i++;
                    stripped.Append('\n');
                    continue;
                }
                if (ch == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++;
                    i++;
                    stripped.Append(' ');
                    continue;
                }
                if (ch == '"' || ch == '\'')
                {
                    char quote = ch;
                    stripped.Append(ch);
                    i++;
                    while (i < code.Length)
                    {
                        stripped.Append(code[i]);
                        if (code[i] == '\\' && i + 1 < code.Length) { stripped.Append(code[i + 1]); i += 2; continue; }
                        if (code[i] == quote) break;
                        i++;
                    }
                    continue;
                }
                stripped.Append(ch);
            }

            var sb = new StringBuilder();
            bool lastWasSpace = true;
            foreach (char ch in stripped.ToString())
            {
                if (char.IsWhiteSpace(ch)) { if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; } continue; }
                sb.Append(ch);
                lastWasSpace = false;
            }
            return sb.ToString().Trim();
        }
    }
}
