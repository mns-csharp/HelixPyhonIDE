using System;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    /// <summary>
    /// Root of the CPython-shaped AST used by the virtual compiler.
    /// Nodes are immutable; every node carries a <see cref="SourceSpan"/>.
    /// Field names mirror CPython's <c>Python/Python-ast.h</c> so the
    /// ported symtable / validator code reads like its C counterpart.
    /// </summary>
    public abstract class AstNode
    {
        public SourceSpan Span { get; }

        protected AstNode(SourceSpan span)
        {
            Span = span;
        }

        public int Lineno { get { return Span.Start.Line; } }
        public int ColOffset { get { return Span.Start.Column - 1; } }
        public int EndLineno { get { return Span.End.Line; } }
        public int EndColOffset { get { return Span.End.Column - 1; } }

        public override string ToString()
        {
            return GetType().Name + " " + Span;
        }
    }

    // Root production kinds from CPython's grammar (mod_ty).
    public abstract class Mod : AstNode
    {
        protected Mod(SourceSpan span) : base(span) { }
    }

    public abstract class Stmt : AstNode
    {
        protected Stmt(SourceSpan span) : base(span) { }
    }

    public abstract class Expr : AstNode
    {
        protected Expr(SourceSpan span) : base(span) { }
    }

    public abstract class Pattern : AstNode
    {
        protected Pattern(SourceSpan span) : base(span) { }
    }

    /// <summary>Helper nodes that aren't stmt/expr/pattern (arguments, keyword, alias, comprehension, ...).</summary>
    public abstract class AstHelper : AstNode
    {
        protected AstHelper(SourceSpan span) : base(span) { }
    }
}
