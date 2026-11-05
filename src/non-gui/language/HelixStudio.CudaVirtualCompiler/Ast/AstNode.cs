namespace HelixPythonNamespace.CudaVirtualCompiler.Ast
{
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Root of the C++/CUDA AST consumed by the CUDA virtual compiler. Mirrors the
    /// Python engine's AST node: every node carries a SourceSpan and exposes
    /// CPython-style line/column accessors so diagnostics and the editor share one
    /// coordinate convention. Port of the C++
    /// helix::cuda_vcompiler::ast::AstNode (cuda_vcompiler/ast/ast_node.h).
    /// </summary>
    public abstract class AstNode
    {
        private readonly SourceSpan span_;

        protected AstNode(SourceSpan span)
        {
            span_ = span;
        }

        public SourceSpan Span { get { return span_; } }
        public int Lineno { get { return span_.Start.Line; } }
        public int ColOffset { get { return span_.Start.Column - 1; } }
        public int EndLineno { get { return span_.End.Line; } }
        public int EndColOffset { get { return span_.End.Column - 1; } }
    }

    /// <summary>
    /// Top-level grammar category: declarations. Port of helix::cuda_vcompiler::ast::Decl.
    /// </summary>
    public abstract class Decl : AstNode
    {
        protected Decl(SourceSpan span) : base(span) { }

        /// <summary>
        /// Access specifier in effect where this declaration appears. Meaningful only for
        /// direct members of a <see cref="RecordDecl"/> (set by <c>CudaParser.ParseRecord</c>
        /// from the nearest preceding public:/private:/protected: label, defaulting per C++
        /// rules — private for `class`, public for `struct` — when no label precedes it);
        /// <see cref="AccessSpec.Default"/> everywhere else (namespace/global scope).
        /// </summary>
        public AccessSpec Access { get; set; }
    }

    /// <summary>
    /// Top-level grammar category: statements. Port of helix::cuda_vcompiler::ast::Stmt.
    /// </summary>
    public abstract class Stmt : AstNode
    {
        protected Stmt(SourceSpan span) : base(span) { }
    }

    /// <summary>
    /// Top-level grammar category: expressions. Port of helix::cuda_vcompiler::ast::Expr.
    /// </summary>
    public abstract class Expr : AstNode
    {
        protected Expr(SourceSpan span) : base(span) { }
    }
}
