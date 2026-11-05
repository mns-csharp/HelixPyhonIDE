namespace HelixPythonNamespace.CudaVirtualCompiler.Ast
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Statement AST nodes. Port of the C++
    /// helix::cuda_vcompiler::ast statement classes (cuda_vcompiler/ast/statements.h).
    /// </summary>

    /// <summary>{ ... } block.</summary>
    public sealed class CompoundStmt : Stmt
    {
        public IReadOnlyList<Stmt> Body { get; private set; }

        /// <summary>
        /// True when this compound is a parser-synthesized GROUPING (a multi-declarator
        /// statement split into DeclStmts, a structured binding's per-name decls) rather
        /// than real braces. Scope-sensitive passes must NOT open a block for it — the
        /// grouped declarations belong to the ENCLOSING block (treating `double a, b;`
        /// as a nested scope made every later use of a/b a bogus out-of-scope report).
        /// </summary>
        public bool IsSyntheticGroup { get; set; }

        public CompoundStmt(List<Stmt> body, SourceSpan span) : base(span)
        {
            Body = body ?? new List<Stmt>();
        }
    }

    /// <summary>A local declaration used as a statement (wraps a VarDecl / RecordDecl).</summary>
    public sealed class DeclStmt : Stmt
    {
        public Decl Decl { get; private set; }

        public DeclStmt(Decl decl, SourceSpan span) : base(span)
        {
            Decl = decl;
        }
    }

    public sealed class ExprStmt : Stmt
    {
        public Expr Expr { get; private set; }

        public ExprStmt(Expr expr, SourceSpan span) : base(span)
        {
            Expr = expr;
        }
    }

    public sealed class IfStmt : Stmt
    {
        public Expr Cond { get; private set; }
        public Stmt Then { get; private set; }
        public Stmt Else { get; private set; }  // nullable

        public IfStmt(Expr cond, Stmt then, Stmt els, SourceSpan span) : base(span)
        {
            Cond = cond;
            Then = then;
            Else = els;
        }
    }

    public sealed class ForStmt : Stmt
    {
        public Stmt Init { get; private set; }  // nullable (decl or expr stmt)
        public Expr Cond { get; private set; }  // nullable
        public Expr Inc { get; private set; }   // nullable
        public Stmt Body { get; private set; }

        public ForStmt(Stmt init, Expr cond, Expr inc, Stmt body, SourceSpan span) : base(span)
        {
            Init = init;
            Cond = cond;
            Inc = inc;
            Body = body;
        }
    }

    public sealed class WhileStmt : Stmt
    {
        public Expr Cond { get; private set; }
        public Stmt Body { get; private set; }

        public WhileStmt(Expr cond, Stmt body, SourceSpan span) : base(span)
        {
            Cond = cond;
            Body = body;
        }
    }

    public sealed class DoStmt : Stmt
    {
        public Stmt Body { get; private set; }
        public Expr Cond { get; private set; }

        public DoStmt(Stmt body, Expr cond, SourceSpan span) : base(span)
        {
            Body = body;
            Cond = cond;
        }
    }

    public sealed class SwitchStmt : Stmt
    {
        public Expr Cond { get; private set; }
        public Stmt Body { get; private set; }

        public SwitchStmt(Expr cond, Stmt body, SourceSpan span) : base(span)
        {
            Cond = cond;
            Body = body;
        }
    }

    public sealed class ReturnStmt : Stmt
    {
        public Expr Value { get; private set; }  // nullable

        public ReturnStmt(Expr value, SourceSpan span) : base(span)
        {
            Value = value;
        }
    }

    public sealed class BreakStmt : Stmt
    {
        public BreakStmt(SourceSpan span) : base(span) { }
    }

    public sealed class ContinueStmt : Stmt
    {
        public ContinueStmt(SourceSpan span) : base(span) { }
    }

    /// <summary>Empty statement (a stray ';').</summary>
    public sealed class EmptyStmt : Stmt
    {
        public EmptyStmt(SourceSpan span) : base(span) { }
    }
}
