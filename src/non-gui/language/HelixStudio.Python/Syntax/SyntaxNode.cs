using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Syntax
{
    /// <summary>
    /// Base class for every node in the Python concrete syntax tree.
    /// Nodes are immutable once constructed.
    /// </summary>
    public abstract class SyntaxNode
    {
        private static readonly SyntaxNode[] NoChildren = new SyntaxNode[0];

        public SourceSpan Span { get; }
        public SyntaxNode Parent { get; internal set; }

        protected SyntaxNode(SourceSpan span)
        {
            Span = span;
        }

        public virtual IReadOnlyList<SyntaxNode> Children => NoChildren;

        internal void AdoptChildren()
        {
            foreach (SyntaxNode child in Children)
            {
                if (child != null) child.Parent = this;
            }
        }

        public IEnumerable<SyntaxNode> Descendants()
        {
            foreach (SyntaxNode child in Children)
            {
                if (child == null) continue;
                yield return child;
                foreach (SyntaxNode d in child.Descendants()) yield return d;
            }
        }

        public T FirstAncestor<T>() where T : SyntaxNode
        {
            SyntaxNode n = Parent;
            while (n != null)
            {
                if (n is T t) return t;
                n = n.Parent;
            }
            return null;
        }

        public override string ToString()
        {
            return GetType().Name + " " + Span;
        }
    }

    public abstract class StatementSyntax : SyntaxNode
    {
        protected StatementSyntax(SourceSpan span) : base(span) { }
    }

    public abstract class ExpressionSyntax : SyntaxNode
    {
        protected ExpressionSyntax(SourceSpan span) : base(span) { }
    }

    public abstract class PatternSyntax : SyntaxNode
    {
        protected PatternSyntax(SourceSpan span) : base(span) { }
    }
}
