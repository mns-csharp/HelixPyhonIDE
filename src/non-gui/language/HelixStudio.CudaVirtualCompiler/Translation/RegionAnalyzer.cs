using System;
using System.Collections.Generic;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;

namespace HelixPythonNamespace.CudaVirtualCompiler.Translation
{
    /// <summary>One indexed access: `a[i + 1]` → Array "a", offset +1 relative to the index var.</summary>
    internal sealed class Access
    {
        public string Array;
        public Expr Index;
        public bool IsWrite;
        public bool IndexIsLoopVar;      // index is exactly the loop variable
        public int ConstantOffset;       // i+k → k, when IndexIsLoopVar
        /// <summary>
        /// The index is an AFFINE function of the index variables and loop-invariant values —
        /// `row * W + col`, `i * 3 + c`, `(y + dy) * W + (x + dx)`. Distinct iterations produce
        /// distinct elements, so writes stay private: this is a linearisation, not a scatter.
        /// </summary>
        public bool IndexIsAffine;
        /// <summary>
        /// The index READS DATA — `perm[i]`, `bin[v]`, `f(i)`. Two iterations may land on the
        /// same element, so a write through it is a genuine collision risk.
        /// </summary>
        public bool IndexIsDataDependent;
        public bool IndexIsComputed;     // index does not mention any index variable at all
        public override string ToString() { return Array + "[…]" + (IsWrite ? " (write)" : " (read)"); }
    }

    /// <summary>
    /// Reach-then-collision analysis of a loop body or kernel body. Answers the two questions the
    /// CUDA decision model asks — what does each output read, and who else writes where it writes —
    /// and turns the answers into a <see cref="KernelShape"/>.
    /// </summary>
    internal sealed class RegionAnalyzer
    {
        public string LoopVar;
        /// <summary>Every index variable of the region (`i`, or `x`/`y` for a 2-D kernel).</summary>
        public readonly List<string> IndexVars = new List<string>();
        public readonly List<Access> Accesses = new List<Access>();
        public readonly HashSet<string> ScalarReads = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> ScalarWrites = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Hazards = new List<string>();
        public readonly HashSet<string> Locals = new HashSet<string>(StringComparer.Ordinal);
        public bool HasCall;
        public bool HasBreakOrContinue;
        /// <summary>`break` ends the WHOLE loop — no per-thread equivalent exists.</summary>
        public bool HasBreak;
        /// <summary>`continue` skips ONE iteration — the mirror of a thread returning early.</summary>
        public bool HasContinue;
        public bool HasNestedLoop;
        public int ConstantBoundInnerLoops;
        public int RuntimeBoundInnerLoops;
        public bool HasAtomic;
        public bool HasSyncthreads;
        public bool HasSharedMemory;
        public bool HasEarlyReturn;

        public void Analyze(Stmt body, string loopVar)
        {
            LoopVar = loopVar;
            if (!string.IsNullOrEmpty(loopVar)) IndexVars.Add(loopVar);
            Walk(body, false);
        }

        public void Analyze(Stmt body, IEnumerable<string> indexVars)
        {
            foreach (string v in indexVars) if (!string.IsNullOrEmpty(v)) IndexVars.Add(v);
            LoopVar = IndexVars.Count > 0 ? IndexVars[0] : null;
            Walk(body, false);
        }

        private void Walk(Stmt s, bool nested)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null) { foreach (Stmt x in c.Body) Walk(x, nested); return; }

            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null)
                {
                    Locals.Add(v.Name);
                    if (v.Storage == StorageClass.Shared) HasSharedMemory = true;
                    if (v.Initializer != null)
                    {
                        // A local derived from an index — `int xx = clampi(x + dx, …);` — is
                        // itself an index expression, so accesses through it are affine in the
                        // thread's own coordinates rather than data-dependent.
                        if (MentionsLoopVar(v.Initializer) && !IndexVars.Contains(v.Name))
                            IndexVars.Add(v.Name);
                        WalkExpr(v.Initializer, false);
                    }
                    if (v.ArraySize != null) WalkExpr(v.ArraySize, false);
                }
                return;
            }

            ExprStmt e = s as ExprStmt;
            if (e != null) { WalkExpr(e.Expr, false); return; }

            IfStmt i = s as IfStmt;
            if (i != null) { WalkExpr(i.Cond, false); Walk(i.Then, nested); Walk(i.Else, nested); return; }

            ForStmt f = s as ForStmt;
            if (f != null)
            {
                HasNestedLoop = true;
                // A neighbourhood loop has a CONSTANT trip count (`for (int dy = -1; dy <= 1; …)`);
                // a reduction/product loop is bounded by a runtime dimension (`k < N`).
                if (HasConstantBound(f.Cond)) ConstantBoundInnerLoops++;
                else RuntimeBoundInnerLoops++;
                Walk(f.Init, true); WalkExpr(f.Cond, false); WalkExpr(f.Inc, false); Walk(f.Body, true);
                return;
            }

            WhileStmt w = s as WhileStmt;
            if (w != null) { HasNestedLoop = true; WalkExpr(w.Cond, false); Walk(w.Body, true); return; }

            DoStmt dowhile = s as DoStmt;
            if (dowhile != null) { HasNestedLoop = true; Walk(dowhile.Body, true); WalkExpr(dowhile.Cond, false); return; }

            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) { WalkExpr(sw.Cond, false); Walk(sw.Body, nested); return; }

            ReturnStmt r = s as ReturnStmt;
            if (r != null) { HasEarlyReturn = true; WalkExpr(r.Value, false); return; }

            if (s is BreakStmt) { HasBreakOrContinue = true; HasBreak = true; return; }
            if (s is ContinueStmt) { HasBreakOrContinue = true; HasContinue = true; return; }
        }

        private void WalkExpr(Expr x, bool isWriteTarget)
        {
            if (x == null) return;

            BinaryExpr b = x as BinaryExpr;
            if (b != null)
            {
                if (IsAssignment(b.Op))
                {
                    // A COMPOUND assignment (`+=`, `|=`, …) reads its target before writing it.
                    // Missing that read is how an accumulator stops looking like an accumulator:
                    // `total += v[i]` would then show no read of `total`, no indexed write, and
                    // the reduction would be handed out as a kernel in which every thread updates
                    // the same location. Walk the target both ways.
                    if (b.Op != BinOpKind.Assign) WalkExpr(b.Lhs, false);
                    WalkExpr(b.Lhs, true);
                    WalkExpr(b.Rhs, false);
                }
                else { WalkExpr(b.Lhs, false); WalkExpr(b.Rhs, false); }
                return;
            }

            IndexExpr ix = x as IndexExpr;
            if (ix != null)
            {
                string arr = BaseName(ix.Base);
                Access a = new Access { Array = arr ?? "<expr>", Index = ix.Index, IsWrite = isWriteTarget };
                ClassifyIndex(a);
                Accesses.Add(a);
                WalkExpr(ix.Index, false);
                return;
            }

            UnaryExpr u = x as UnaryExpr;
            if (u != null)
            {
                bool w = u.Op == UnaryOpKind.PreInc || u.Op == UnaryOpKind.PreDec
                      || u.Op == UnaryOpKind.PostInc || u.Op == UnaryOpKind.PostDec;
                // `count++` is `count = count + 1`: a read as well as a write, for the same
                // reason a compound assignment is.
                if (w) WalkExpr(u.Operand, false);
                WalkExpr(u.Operand, w || isWriteTarget);
                return;
            }

            CallExpr call = x as CallExpr;
            if (call != null)
            {
                string callee = BaseName(call.Callee) ?? string.Empty;
                if (callee.StartsWith("atomic", StringComparison.Ordinal)) HasAtomic = true;
                else if (callee == "__syncthreads" || callee == "__syncwarp") HasSyncthreads = true;
                else HasCall = true;
                foreach (Expr arg in call.Args) WalkExpr(arg, false);
                return;
            }

            NameExpr n = x as NameExpr;
            if (n != null)
            {
                if (isWriteTarget) ScalarWrites.Add(n.Id); else ScalarReads.Add(n.Id);
                return;
            }

            ParenExpr p = x as ParenExpr;
            if (p != null) { WalkExpr(p.Inner, isWriteTarget); return; }

            ConditionalExpr cond = x as ConditionalExpr;
            if (cond != null) { WalkExpr(cond.Cond, false); WalkExpr(cond.Then, false); WalkExpr(cond.Else, false); return; }

            CastExpr cast = x as CastExpr;
            if (cast != null) { WalkExpr(cast.Operand, isWriteTarget); return; }

            MemberExpr m = x as MemberExpr;
            if (m != null) { WalkExpr(m.Object, isWriteTarget); return; }
        }

        private void ClassifyIndex(Access a)
        {
            if (IndexVars.Count == 0) { a.IndexIsComputed = true; return; }
            NameExpr n = Unwrap(a.Index) as NameExpr;
            if (n != null && n.Id == LoopVar) { a.IndexIsLoopVar = true; a.ConstantOffset = 0; return; }

            // i ± constant  → a stencil offset.
            BinaryExpr b = Unwrap(a.Index) as BinaryExpr;
            if (b != null && (b.Op == BinOpKind.Add || b.Op == BinOpKind.Sub))
            {
                NameExpr ln = Unwrap(b.Lhs) as NameExpr;
                LiteralExpr rl = Unwrap(b.Rhs) as LiteralExpr;
                if (ln != null && ln.Id == LoopVar && rl != null && rl.Kind == LiteralKind.Int)
                {
                    int k;
                    if (int.TryParse(rl.Text, out k))
                    {
                        a.IndexIsLoopVar = true;
                        a.ConstantOffset = b.Op == BinOpKind.Sub ? -k : k;
                        return;
                    }
                }
            }
            // `perm[i]`, `bins[v]`, `f(i)` — the index reads data, so two iterations may collide.
            if (ContainsDataRead(a.Index)) { a.IndexIsDataDependent = true; return; }
            // `row * W + col`, `i * 3 + c`, `(y+dy)*W + (x+dx)` — an affine linearisation of the
            // index space: distinct iterations give distinct elements, so writes stay private.
            if (MentionsLoopVar(a.Index)) { a.IndexIsAffine = true; return; }
            a.IndexIsComputed = true;
        }

        /// <summary>True when the expression reads an array element or calls a function.</summary>
        private static bool ContainsDataRead(Expr x)
        {
            if (x == null) return false;
            if (x is IndexExpr) return true;
            CallExpr call = x as CallExpr;
            if (call != null)
            {
                string callee = BaseName(call.Callee) ?? string.Empty;
                // min/max/abs and the CUDA index intrinsics are pure arithmetic on indices.
                if (callee != "min" && callee != "max" && callee != "abs"
                    && callee != "fminf" && callee != "fmaxf") return true;
                foreach (Expr arg in call.Args) if (ContainsDataRead(arg)) return true;
                return false;
            }
            BinaryExpr b = x as BinaryExpr;
            if (b != null) return ContainsDataRead(b.Lhs) || ContainsDataRead(b.Rhs);
            UnaryExpr u = x as UnaryExpr; if (u != null) return ContainsDataRead(u.Operand);
            ParenExpr p = x as ParenExpr; if (p != null) return ContainsDataRead(p.Inner);
            CastExpr c = x as CastExpr; if (c != null) return ContainsDataRead(c.Operand);
            ConditionalExpr cond = x as ConditionalExpr;
            if (cond != null) return ContainsDataRead(cond.Cond) || ContainsDataRead(cond.Then) || ContainsDataRead(cond.Else);
            MemberExpr m = x as MemberExpr;
            if (m != null)
            {
                // threadIdx.x / blockIdx.y are index intrinsics, not data.
                string root = BaseName(m.Object) ?? string.Empty;
                if (root == "threadIdx" || root == "blockIdx" || root == "blockDim" || root == "gridDim") return false;
                return ContainsDataRead(m.Object);
            }
            return false;
        }

        private bool MentionsLoopVar(Expr x)
        {
            if (x == null) return false;
            NameExpr n = x as NameExpr;
            if (n != null) return IndexVars.Contains(n.Id);
            BinaryExpr b = x as BinaryExpr;
            if (b != null) return MentionsLoopVar(b.Lhs) || MentionsLoopVar(b.Rhs);
            UnaryExpr u = x as UnaryExpr; if (u != null) return MentionsLoopVar(u.Operand);
            ParenExpr p = x as ParenExpr; if (p != null) return MentionsLoopVar(p.Inner);
            IndexExpr ix = x as IndexExpr; if (ix != null) return MentionsLoopVar(ix.Base) || MentionsLoopVar(ix.Index);
            CastExpr c = x as CastExpr; if (c != null) return MentionsLoopVar(c.Operand);
            CallExpr call = x as CallExpr;
            if (call != null) { foreach (Expr arg in call.Args) if (MentionsLoopVar(arg)) return true; return false; }
            ConditionalExpr cond = x as ConditionalExpr;
            if (cond != null) return MentionsLoopVar(cond.Cond) || MentionsLoopVar(cond.Then) || MentionsLoopVar(cond.Else);
            return false;
        }

        /// <summary>Reach, then collision → shape. Also fills <see cref="Hazards"/>.</summary>
        public KernelShape Classify(out string reason)
        {
            var writes = new List<Access>();
            var reads = new List<Access>();
            foreach (Access a in Accesses) { if (a.IsWrite) writes.Add(a); else reads.Add(a); }

            // A scalar that is both read and written across iterations accumulates: reduction.
            var accumulators = new List<string>();
            foreach (string w in ScalarWrites)
                if (ScalarReads.Contains(w) && !Locals.Contains(w)) accumulators.Add(w);

            if (HasSyncthreads) Hazards.Add("contains __syncthreads(): barrier convergence must hold for every thread");
            if (HasAtomic) Hazards.Add("uses atomics: collisions are resolved at run time, order is not deterministic");
            if (HasSharedMemory) Hazards.Add("uses __shared__ memory: tile size and barriers are part of the contract");

            // A write whose index is READ FROM DATA — the classic collision risk. An affine
            // linearisation (`out[row*W+col]`) is NOT one: distinct iterations, distinct elements.
            foreach (Access w in writes)
            {
                if (w.IndexIsDataDependent)
                {
                    Hazards.Add("writes " + w.Array + " at a data-dependent index: collisions are possible");
                    reason = "a write index is computed from data (" + w.Array + "), so two iterations may target the same element";
                    return HasAtomic ? KernelShape.Histogram : KernelShape.Scatter;
                }
            }

            // Cross-iteration dependence: reads the SAME array it writes, at a shifted index.
            foreach (Access w in writes)
            {
                foreach (Access r in reads)
                {
                    if (r.Array != w.Array) continue;
                    if (r.IndexIsLoopVar && w.IndexIsLoopVar && r.ConstantOffset != w.ConstantOffset)
                    {
                        Hazards.Add("reads " + r.Array + "[i" + Sign(r.ConstantOffset) + "] while writing "
                                    + w.Array + "[i" + Sign(w.ConstantOffset) + "]");
                        reason = "iteration i reads " + r.Array + "[i" + Sign(r.ConstantOffset) + "], which iteration "
                                 + (r.ConstantOffset < w.ConstantOffset ? "i-1" : "i+1") + " writes — a loop-carried dependence";
                        return KernelShape.Scan;
                    }
                }
            }

            if (accumulators.Count > 0 && writes.Count == 0)
            {
                reason = "every iteration accumulates into the scalar '" + accumulators[0] + "'";
                return KernelShape.Reduce;
            }

            if (HasNestedLoop && writes.Count > 0)
            {
                // A CONSTANT-trip inner loop over index-derived neighbours is a stencil
                // (`for dy in -1..1` around `in[(y+dy)*W + (x+dx)]`); a RUNTIME-bounded inner
                // loop accumulating products is a matrix multiply; anything else is all-pairs.
                if (ConstantBoundInnerLoops > 0 && RuntimeBoundInnerLoops == 0)
                {
                    reason = "each output reads a fixed neighbourhood of its input";
                    return KernelShape.Stencil;
                }
                bool product = false;
                foreach (Access r in reads) if (r.IndexIsAffine) product = true;
                reason = product
                    ? "an inner loop accumulates a sum of products into each output element"
                    : "an inner loop reaches many inputs per output";
                return product ? KernelShape.MatrixMultiply : KernelShape.AllPairs;
            }

            if (writes.Count == 0)
            {
                reason = "no indexed write was found";
                return accumulators.Count > 0 ? KernelShape.Reduce : KernelShape.Unknown;
            }

            // Every write is private (output[i]); classify by how far the reads reach.
            bool anyOffset = false, anyGatherRead = false, anyAffineRead = false;
            foreach (Access r in reads)
            {
                if (r.IndexIsLoopVar && r.ConstantOffset != 0) anyOffset = true;
                if (r.IndexIsDataDependent) anyGatherRead = true;
                if (r.IndexIsAffine) anyAffineRead = true;
            }
            if (anyGatherRead)
            {
                reason = "outputs are private but inputs are read at data-dependent indices";
                return KernelShape.Gather;
            }
            if (anyAffineRead && !anyOffset)
            {
                // `out[row*W+col] = in[row*W+col]` — still one input element per output, just
                // addressed through a linearised 2-D index.
                reason = "each output reads its own input element through a linearised index";
                return KernelShape.Map;
            }
            if (anyOffset)
            {
                reason = "each output reads a fixed neighbourhood of its input";
                return KernelShape.Stencil;
            }
            reason = "each output reads only its own input element";
            return KernelShape.Map;
        }

        private static string Sign(int k) { return k == 0 ? "" : (k > 0 ? "+" + k : k.ToString()); }

        /// <summary>True when a loop condition compares against an integer literal.</summary>
        private static bool HasConstantBound(Expr cond)
        {
            BinaryExpr b = Unwrap(cond) as BinaryExpr;
            if (b == null) return false;
            if (b.Op != BinOpKind.Lt && b.Op != BinOpKind.Le && b.Op != BinOpKind.Gt && b.Op != BinOpKind.Ge) return false;
            Expr rhs = Unwrap(b.Rhs);
            LiteralExpr lit = rhs as LiteralExpr;
            if (lit != null && lit.Kind == LiteralKind.Int) return true;
            UnaryExpr u = rhs as UnaryExpr;      // `<= -1` / `>= -1`
            if (u != null && (u.Op == UnaryOpKind.Minus || u.Op == UnaryOpKind.Plus))
            {
                LiteralExpr inner = Unwrap(u.Operand) as LiteralExpr;
                return inner != null && inner.Kind == LiteralKind.Int;
            }
            return false;
        }

        internal static bool IsAssignment(BinOpKind op)
        {
            switch (op)
            {
                case BinOpKind.Assign:
                case BinOpKind.AddAssign:
                case BinOpKind.SubAssign:
                case BinOpKind.MulAssign:
                case BinOpKind.DivAssign:
                case BinOpKind.ModAssign:
                case BinOpKind.AndAssign:
                case BinOpKind.OrAssign:
                case BinOpKind.XorAssign:
                case BinOpKind.ShlAssign:
                case BinOpKind.ShrAssign:
                    return true;
                default: return false;
            }
        }

        internal static Expr Unwrap(Expr e)
        {
            ParenExpr p = e as ParenExpr;
            while (p != null) { e = p.Inner; p = e as ParenExpr; }
            return e;
        }

        internal static string BaseName(Expr e)
        {
            e = Unwrap(e);
            NameExpr n = e as NameExpr;
            if (n != null) return n.Id;
            MemberExpr m = e as MemberExpr;
            if (m != null) return m.Member;
            IndexExpr ix = e as IndexExpr;
            if (ix != null) return BaseName(ix.Base);
            CastExpr c = e as CastExpr;
            if (c != null) return BaseName(c.Operand);
            return null;
        }
    }
}
