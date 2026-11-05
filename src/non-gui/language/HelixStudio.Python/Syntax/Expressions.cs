using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Syntax
{
    public enum BinaryOperator
    {
        Or, And,
        BitOr, BitXor, BitAnd,
        LShift, RShift,
        Add, Subtract, Multiply, MatMul, Divide, FloorDivide, Modulo, Power
    }

    public enum UnaryOperator
    {
        Plus, Minus, BitNot, Not, Await
    }

    public enum ComparisonOperator
    {
        Eq, NotEq, Less, LessEq, Greater, GreaterEq, In, NotIn, Is, IsNot
    }

    public sealed class BinaryExpression : ExpressionSyntax
    {
        public BinaryOperator Operator { get; }
        public ExpressionSyntax Left { get; }
        public ExpressionSyntax Right { get; }
        public BinaryExpression(BinaryOperator op, ExpressionSyntax left, ExpressionSyntax right, SourceSpan span) : base(span)
        { Operator = op; Left = left; Right = right; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { Left, Right };
    }

    public sealed class UnaryExpression : ExpressionSyntax
    {
        public UnaryOperator Operator { get; }
        public ExpressionSyntax Operand { get; }
        public UnaryExpression(UnaryOperator op, ExpressionSyntax operand, SourceSpan span) : base(span)
        { Operator = op; Operand = operand; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { Operand };
    }

    public sealed class ComparisonExpression : ExpressionSyntax
    {
        public ExpressionSyntax Left { get; }
        public IReadOnlyList<ComparisonOperator> Operators { get; }
        public IReadOnlyList<ExpressionSyntax> Comparators { get; }
        public ComparisonExpression(ExpressionSyntax left, IReadOnlyList<ComparisonOperator> ops, IReadOnlyList<ExpressionSyntax> comparators, SourceSpan span)
            : base(span)
        { Left = left; Operators = ops; Comparators = comparators; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode> { Left };
                foreach (ExpressionSyntax c in Comparators) list.Add(c);
                return list;
            }
        }
    }

    public sealed class ConditionalExpression : ExpressionSyntax
    {
        public ExpressionSyntax IfTrue { get; }
        public ExpressionSyntax Condition { get; }
        public ExpressionSyntax IfFalse { get; }
        public ConditionalExpression(ExpressionSyntax ifTrue, ExpressionSyntax cond, ExpressionSyntax ifFalse, SourceSpan span)
            : base(span)
        { IfTrue = ifTrue; Condition = cond; IfFalse = ifFalse; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { IfTrue, Condition, IfFalse };
    }

    public sealed class NamedExpression : ExpressionSyntax
    {
        public NameExpression Target { get; }
        public ExpressionSyntax Value { get; }
        public NamedExpression(NameExpression target, ExpressionSyntax value, SourceSpan span) : base(span)
        { Target = target; Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { Target, Value };
    }

    public sealed class LambdaExpression : ExpressionSyntax
    {
        public IReadOnlyList<ParameterSyntax> Parameters { get; }
        public ExpressionSyntax Body { get; }
        public LambdaExpression(IReadOnlyList<ParameterSyntax> parameters, ExpressionSyntax body, SourceSpan span) : base(span)
        {
            Parameters = parameters ?? Array.Empty<ParameterSyntax>();
            Body = body;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                foreach (ParameterSyntax p in Parameters) list.Add(p);
                if (Body != null) list.Add(Body);
                return list;
            }
        }
    }

    public sealed class YieldExpression : ExpressionSyntax
    {
        public bool IsFrom { get; }
        public ExpressionSyntax Value { get; }
        public YieldExpression(bool isFrom, ExpressionSyntax value, SourceSpan span) : base(span)
        { IsFrom = isFrom; Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Value != null ? new SyntaxNode[] { Value } : Array.Empty<SyntaxNode>();
    }

    public sealed class CallExpression : ExpressionSyntax
    {
        public ExpressionSyntax Callee { get; }
        public IReadOnlyList<CallArgument> Arguments { get; }
        public CallExpression(ExpressionSyntax callee, IReadOnlyList<CallArgument> arguments, SourceSpan span) : base(span)
        { Callee = callee; Arguments = arguments ?? Array.Empty<CallArgument>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode> { Callee };
                foreach (CallArgument a in Arguments) list.Add(a);
                return list;
            }
        }
    }

    public sealed class CallArgument : SyntaxNode
    {
        public enum ArgKind { Positional, Keyword, Star, DoubleStar }
        public ArgKind Kind { get; }
        public string Name { get; }  // non-null for Keyword
        public ExpressionSyntax Value { get; }
        public CallArgument(ArgKind kind, string name, ExpressionSyntax value, SourceSpan span) : base(span)
        { Kind = kind; Name = name; Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Value != null ? new SyntaxNode[] { Value } : Array.Empty<SyntaxNode>();
    }

    public sealed class SubscriptExpression : ExpressionSyntax
    {
        public ExpressionSyntax Target { get; }
        public ExpressionSyntax Index { get; }
        public SubscriptExpression(ExpressionSyntax target, ExpressionSyntax index, SourceSpan span) : base(span)
        { Target = target; Index = index; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { Target, Index };
    }

    public sealed class SliceExpression : ExpressionSyntax
    {
        public ExpressionSyntax Lower { get; }
        public ExpressionSyntax Upper { get; }
        public ExpressionSyntax Step { get; }
        public SliceExpression(ExpressionSyntax lower, ExpressionSyntax upper, ExpressionSyntax step, SourceSpan span)
            : base(span)
        { Lower = lower; Upper = upper; Step = step; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (Lower != null) list.Add(Lower);
                if (Upper != null) list.Add(Upper);
                if (Step != null) list.Add(Step);
                return list;
            }
        }
    }

    public sealed class AttributeExpression : ExpressionSyntax
    {
        public ExpressionSyntax Target { get; }
        public string Name { get; }
        public AttributeExpression(ExpressionSyntax target, string name, SourceSpan span) : base(span)
        { Target = target; Name = name ?? string.Empty; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { Target };
    }

    public sealed class StarredExpression : ExpressionSyntax
    {
        public ExpressionSyntax Value { get; }
        public bool IsDouble { get; } // **expr vs *expr
        public StarredExpression(ExpressionSyntax value, bool isDouble, SourceSpan span) : base(span)
        { Value = value; IsDouble = isDouble; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Value != null ? new SyntaxNode[] { Value } : Array.Empty<SyntaxNode>();
    }

    public sealed class TupleExpression : ExpressionSyntax
    {
        public IReadOnlyList<ExpressionSyntax> Elements { get; }
        public TupleExpression(IReadOnlyList<ExpressionSyntax> elements, SourceSpan span) : base(span)
        { Elements = elements ?? Array.Empty<ExpressionSyntax>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Elements;
    }

    public sealed class ListExpression : ExpressionSyntax
    {
        public IReadOnlyList<ExpressionSyntax> Elements { get; }
        public ListExpression(IReadOnlyList<ExpressionSyntax> elements, SourceSpan span) : base(span)
        { Elements = elements ?? Array.Empty<ExpressionSyntax>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Elements;
    }

    public sealed class SetExpression : ExpressionSyntax
    {
        public IReadOnlyList<ExpressionSyntax> Elements { get; }
        public SetExpression(IReadOnlyList<ExpressionSyntax> elements, SourceSpan span) : base(span)
        { Elements = elements ?? Array.Empty<ExpressionSyntax>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Elements;
    }

    public sealed class DictEntry : SyntaxNode
    {
        public ExpressionSyntax Key { get; }   // null for **value
        public ExpressionSyntax Value { get; }
        public DictEntry(ExpressionSyntax key, ExpressionSyntax value, SourceSpan span) : base(span)
        { Key = key; Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (Key != null) list.Add(Key);
                if (Value != null) list.Add(Value);
                return list;
            }
        }
    }

    public sealed class DictExpression : ExpressionSyntax
    {
        public IReadOnlyList<DictEntry> Entries { get; }
        public DictExpression(IReadOnlyList<DictEntry> entries, SourceSpan span) : base(span)
        { Entries = entries ?? Array.Empty<DictEntry>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Entries;
    }

    public sealed class ComprehensionClause : SyntaxNode
    {
        public bool IsAsync { get; }
        public ExpressionSyntax Target { get; }
        public ExpressionSyntax Iter { get; }
        public IReadOnlyList<ExpressionSyntax> Ifs { get; }
        public ComprehensionClause(bool isAsync, ExpressionSyntax target, ExpressionSyntax iter, IReadOnlyList<ExpressionSyntax> ifs, SourceSpan span)
            : base(span)
        {
            IsAsync = isAsync; Target = target; Iter = iter;
            Ifs = ifs ?? Array.Empty<ExpressionSyntax>();
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode> { Target, Iter };
                foreach (ExpressionSyntax e in Ifs) list.Add(e);
                return list;
            }
        }
    }

    public enum ComprehensionKind { List, Set, Dict, Generator }

    public sealed class ComprehensionExpression : ExpressionSyntax
    {
        public ComprehensionKind Kind { get; }
        public ExpressionSyntax Element { get; }    // for Dict this is key
        public ExpressionSyntax ValueElement { get; } // for Dict this is value; null otherwise
        public IReadOnlyList<ComprehensionClause> Clauses { get; }
        public ComprehensionExpression(ComprehensionKind kind, ExpressionSyntax element, ExpressionSyntax valueElement,
            IReadOnlyList<ComprehensionClause> clauses, SourceSpan span) : base(span)
        {
            Kind = kind; Element = element; ValueElement = valueElement;
            Clauses = clauses ?? Array.Empty<ComprehensionClause>();
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (Element != null) list.Add(Element);
                if (ValueElement != null) list.Add(ValueElement);
                foreach (ComprehensionClause c in Clauses) list.Add(c);
                return list;
            }
        }
    }

    public sealed class ParenthesizedExpression : ExpressionSyntax
    {
        public ExpressionSyntax Inner { get; }
        public ParenthesizedExpression(ExpressionSyntax inner, SourceSpan span) : base(span)
        { Inner = inner; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Inner != null ? new SyntaxNode[] { Inner } : Array.Empty<SyntaxNode>();
    }

    public sealed class EllipsisExpression : ExpressionSyntax
    {
        public EllipsisExpression(SourceSpan span) : base(span) { }
    }

    public sealed class FStringExpression : ExpressionSyntax
    {
        public IReadOnlyList<SyntaxNode> Parts { get; } // strings or expressions

        // Parallel to Parts (same length, same index correspondence). Non-null only at an
        // index where Parts[i] is a replacement-field expression that used a conversion
        // (!r/!s/!a) and/or a format spec (:spec); null everywhere Parts[i] is literal text
        // or an expression with neither. Added for the Python->C++ translator, which needs
        // f-string format specs; existing consumers that only read Parts are unaffected.
        public IReadOnlyList<char?> Conversions { get; }
        public IReadOnlyList<string> FormatSpecs { get; }

        public FStringExpression(IReadOnlyList<SyntaxNode> parts, SourceSpan span)
            : this(parts, null, null, span) { }

        public FStringExpression(IReadOnlyList<SyntaxNode> parts, IReadOnlyList<char?> conversions,
            IReadOnlyList<string> formatSpecs, SourceSpan span) : base(span)
        {
            Parts = parts ?? Array.Empty<SyntaxNode>();
            Conversions = conversions ?? Array.Empty<char?>();
            FormatSpecs = formatSpecs ?? Array.Empty<string>();
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children => Parts;
    }
}
