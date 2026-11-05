namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;

    /// <summary>
    /// AST traversal helpers used by the launch / memory / lint scans. Port of the
    /// C++ helix::cuda_vcompiler::analyze walk helpers
    /// (cuda_vcompiler/analyze/ast_walk.{h,cpp}).
    /// </summary>
    public static class AstWalk
    {
        /// <summary>
        /// Invoke <paramref name="fn"/> on every FunctionDecl in the translation unit
        /// (descending through namespaces, records, and templates).
        /// </summary>
        public static void ForEachFunction(TranslationUnit unit, Action<FunctionDecl> fn)
        {
            IReadOnlyList<Decl> decls = unit.Decls;
            for (int i = 0; i < decls.Count; ++i) WalkDeclForFunctions(decls[i], fn);
        }

        private static void WalkDeclForFunctions(Decl d, Action<FunctionDecl> fn)
        {
            if (d == null) return;
            FunctionDecl f = d as FunctionDecl;
            if (f != null)
            {
                fn(f);
                return;
            }
            NamespaceDecl ns = d as NamespaceDecl;
            if (ns != null)
            {
                IReadOnlyList<Decl> decls = ns.Decls;
                for (int i = 0; i < decls.Count; ++i) WalkDeclForFunctions(decls[i], fn);
                return;
            }
            RecordDecl rec = d as RecordDecl;
            if (rec != null)
            {
                IReadOnlyList<Decl> members = rec.Members;
                for (int i = 0; i < members.Count; ++i) WalkDeclForFunctions(members[i], fn);
                return;
            }
            TemplateDecl t = d as TemplateDecl;
            if (t != null)
            {
                WalkDeclForFunctions(t.Inner, fn);
                return;
            }
        }

        /// <summary>Invoke <paramref name="fn"/> on a single expression tree, top-down.</summary>
        public static void ForEachExprIn(Expr e, Action<Expr> fn)
        {
            if (e == null) return;
            fn(e);
            ParenExpr p = e as ParenExpr;
            if (p != null) { ForEachExprIn(p.Inner, fn); return; }
            UnaryExpr u = e as UnaryExpr;
            if (u != null) { ForEachExprIn(u.Operand, fn); return; }
            BinaryExpr b = e as BinaryExpr;
            if (b != null) { ForEachExprIn(b.Lhs, fn); ForEachExprIn(b.Rhs, fn); return; }
            ConditionalExpr c = e as ConditionalExpr;
            if (c != null) { ForEachExprIn(c.Cond, fn); ForEachExprIn(c.Then, fn); ForEachExprIn(c.Else, fn); return; }
            CallExpr call = e as CallExpr;
            if (call != null)
            {
                ForEachExprIn(call.Callee, fn);
                for (int i = 0; i < call.Args.Count; ++i) ForEachExprIn(call.Args[i], fn);
                return;
            }
            KernelLaunchExpr k = e as KernelLaunchExpr;
            if (k != null)
            {
                ForEachExprIn(k.Callee, fn);
                ForEachExprIn(k.Grid, fn); ForEachExprIn(k.Block, fn);
                ForEachExprIn(k.SharedMem, fn); ForEachExprIn(k.Stream, fn);
                for (int i = 0; i < k.Args.Count; ++i) ForEachExprIn(k.Args[i], fn);
                return;
            }
            MemberExpr m = e as MemberExpr;
            if (m != null) { ForEachExprIn(m.Object, fn); return; }
            IndexExpr ix = e as IndexExpr;
            if (ix != null) { ForEachExprIn(ix.Base, fn); ForEachExprIn(ix.Index, fn); return; }
            CastExpr cast = e as CastExpr;
            if (cast != null) { ForEachExprIn(cast.Operand, fn); return; }
            NewExpr ne = e as NewExpr;
            if (ne != null) { ForEachExprIn(ne.ArraySize, fn); return; }
            DeleteExpr del = e as DeleteExpr;
            if (del != null) { ForEachExprIn(del.Operand, fn); return; }
            InitListExpr il = e as InitListExpr;
            if (il != null)
            {
                for (int i = 0; i < il.Elements.Count; ++i) ForEachExprIn(il.Elements[i], fn);
                return;
            }
            // LambdaExpr: deliberately no descent — captured names make resolution
            // inside the body unprovable for the static analyses.
        }

        /// <summary>
        /// Invoke <paramref name="fn"/> on every Expr node reachable from
        /// <paramref name="s"/> (its sub-expressions included), top-down.
        /// </summary>
        public static void ForEachExpr(Stmt s, Action<Expr> fn)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) ForEachExpr(c.Body[i], fn);
                return;
            }
            ExprStmt e = s as ExprStmt;
            if (e != null) { ForEachExprIn(e.Expr, fn); return; }
            IfStmt ifs = s as IfStmt;
            if (ifs != null) { ForEachExprIn(ifs.Cond, fn); ForEachExpr(ifs.Then, fn); ForEachExpr(ifs.Else, fn); return; }
            ForStmt f = s as ForStmt;
            if (f != null)
            {
                ForEachExpr(f.Init, fn); ForEachExprIn(f.Cond, fn);
                ForEachExprIn(f.Inc, fn); ForEachExpr(f.Body, fn);
                return;
            }
            WhileStmt w = s as WhileStmt;
            if (w != null) { ForEachExprIn(w.Cond, fn); ForEachExpr(w.Body, fn); return; }
            DoStmt dw = s as DoStmt;
            if (dw != null) { ForEachExpr(dw.Body, fn); ForEachExprIn(dw.Cond, fn); return; }
            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) { ForEachExprIn(sw.Cond, fn); ForEachExpr(sw.Body, fn); return; }
            ReturnStmt r = s as ReturnStmt;
            if (r != null) { ForEachExprIn(r.Value, fn); return; }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null) ForEachExprIn(v.Initializer, fn);
                return;
            }
        }

        /// <summary>
        /// Collect local-variable initializers (name -&gt; initializer expr) from a
        /// function body, for constant folding of launch dimensions.
        /// </summary>
        public static void CollectVarInits(Stmt s, Dictionary<string, Expr> outMap)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) CollectVarInits(c.Body[i], outMap);
                return;
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null && v.Initializer != null) outMap[v.Name] = v.Initializer;
                return;
            }
            IfStmt ifs = s as IfStmt;
            if (ifs != null) { CollectVarInits(ifs.Then, outMap); CollectVarInits(ifs.Else, outMap); return; }
            ForStmt f = s as ForStmt;
            if (f != null) { CollectVarInits(f.Init, outMap); CollectVarInits(f.Body, outMap); return; }
            WhileStmt w = s as WhileStmt;
            if (w != null) { CollectVarInits(w.Body, outMap); return; }
            DoStmt dw = s as DoStmt;
            if (dw != null) { CollectVarInits(dw.Body, outMap); return; }
            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) { CollectVarInits(sw.Body, outMap); return; }
        }
    }
}
