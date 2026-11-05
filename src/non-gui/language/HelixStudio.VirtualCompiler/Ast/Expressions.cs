using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    public sealed class BoolOpExpr : Expr
    {
        public BoolOp Op { get; }
        public IReadOnlyList<Expr> Values { get; }
        public BoolOpExpr(BoolOp op, IReadOnlyList<Expr> values, SourceSpan span) : base(span)
        {
            Op = op;
            Values = values ?? Array.Empty<Expr>();
        }
    }

    public sealed class NamedExpr : Expr
    {
        public Expr Target { get; }
        public Expr Value { get; }
        public NamedExpr(Expr target, Expr value, SourceSpan span) : base(span)
        {
            Target = target; Value = value;
        }
    }

    public sealed class BinOpExpr : Expr
    {
        public Expr Left { get; }
        public BinOp Op { get; }
        public Expr Right { get; }
        public BinOpExpr(Expr left, BinOp op, Expr right, SourceSpan span) : base(span)
        {
            Left = left; Op = op; Right = right;
        }
    }

    public sealed class UnaryOpExpr : Expr
    {
        public UnaryOp Op { get; }
        public Expr Operand { get; }
        public UnaryOpExpr(UnaryOp op, Expr operand, SourceSpan span) : base(span)
        {
            Op = op; Operand = operand;
        }
    }

    public sealed class Lambda : Expr
    {
        public Arguments Args { get; }
        public Expr Body { get; }
        public Lambda(Arguments args, Expr body, SourceSpan span) : base(span)
        {
            Args = args; Body = body;
        }
    }

    public sealed class IfExp : Expr
    {
        public Expr Test { get; }
        public Expr Body { get; }
        public Expr OrElse { get; }
        public IfExp(Expr test, Expr body, Expr orElse, SourceSpan span) : base(span)
        {
            Test = test; Body = body; OrElse = orElse;
        }
    }

    public sealed class Dict : Expr
    {
        public IReadOnlyList<Expr> Keys { get; }   // nulls represent **d splats aligned with Values
        public IReadOnlyList<Expr> Values { get; }
        public Dict(IReadOnlyList<Expr> keys, IReadOnlyList<Expr> values, SourceSpan span) : base(span)
        {
            Keys = keys ?? Array.Empty<Expr>();
            Values = values ?? Array.Empty<Expr>();
        }
    }

    public sealed class SetExpr : Expr
    {
        public IReadOnlyList<Expr> Elts { get; }
        public SetExpr(IReadOnlyList<Expr> elts, SourceSpan span) : base(span)
        {
            Elts = elts ?? Array.Empty<Expr>();
        }
    }

    public sealed class ListComp : Expr
    {
        public Expr Elt { get; }
        public IReadOnlyList<Comprehension> Generators { get; }
        public ListComp(Expr elt, IReadOnlyList<Comprehension> generators, SourceSpan span) : base(span)
        {
            Elt = elt;
            Generators = generators ?? Array.Empty<Comprehension>();
        }
    }

    public sealed class SetComp : Expr
    {
        public Expr Elt { get; }
        public IReadOnlyList<Comprehension> Generators { get; }
        public SetComp(Expr elt, IReadOnlyList<Comprehension> generators, SourceSpan span) : base(span)
        {
            Elt = elt;
            Generators = generators ?? Array.Empty<Comprehension>();
        }
    }

    public sealed class DictComp : Expr
    {
        public Expr Key { get; }
        public Expr Value { get; }
        public IReadOnlyList<Comprehension> Generators { get; }
        public DictComp(Expr key, Expr value, IReadOnlyList<Comprehension> generators, SourceSpan span) : base(span)
        {
            Key = key; Value = value;
            Generators = generators ?? Array.Empty<Comprehension>();
        }
    }

    public sealed class GeneratorExp : Expr
    {
        public Expr Elt { get; }
        public IReadOnlyList<Comprehension> Generators { get; }
        public GeneratorExp(Expr elt, IReadOnlyList<Comprehension> generators, SourceSpan span) : base(span)
        {
            Elt = elt;
            Generators = generators ?? Array.Empty<Comprehension>();
        }
    }

    public sealed class Await : Expr
    {
        public Expr Value { get; }
        public Await(Expr value, SourceSpan span) : base(span) { Value = value; }
    }

    public sealed class Yield : Expr
    {
        public Expr Value { get; }  // nullable
        public Yield(Expr value, SourceSpan span) : base(span) { Value = value; }
    }

    public sealed class YieldFrom : Expr
    {
        public Expr Value { get; }
        public YieldFrom(Expr value, SourceSpan span) : base(span) { Value = value; }
    }

    public sealed class Compare : Expr
    {
        public Expr Left { get; }
        public IReadOnlyList<CmpOp> Ops { get; }
        public IReadOnlyList<Expr> Comparators { get; }
        public Compare(Expr left, IReadOnlyList<CmpOp> ops, IReadOnlyList<Expr> comparators, SourceSpan span) : base(span)
        {
            Left = left;
            Ops = ops ?? Array.Empty<CmpOp>();
            Comparators = comparators ?? Array.Empty<Expr>();
        }
    }

    public sealed class Call : Expr
    {
        public Expr Func { get; }
        public IReadOnlyList<Expr> Args { get; }
        public IReadOnlyList<Keyword> Keywords { get; }
        public Call(Expr func, IReadOnlyList<Expr> args, IReadOnlyList<Keyword> keywords, SourceSpan span) : base(span)
        {
            Func = func;
            Args = args ?? Array.Empty<Expr>();
            Keywords = keywords ?? Array.Empty<Keyword>();
        }
    }

    public sealed class FormattedValue : Expr
    {
        public Expr Value { get; }
        public int Conversion { get; }    // -1, 's', 'r', 'a'
        public Expr FormatSpec { get; }
        public FormattedValue(Expr value, int conversion, Expr formatSpec, SourceSpan span) : base(span)
        {
            Value = value; Conversion = conversion; FormatSpec = formatSpec;
        }
    }

    public sealed class JoinedStr : Expr
    {
        public IReadOnlyList<Expr> Values { get; }
        public JoinedStr(IReadOnlyList<Expr> values, SourceSpan span) : base(span)
        {
            Values = values ?? Array.Empty<Expr>();
        }
    }

    /// <summary>CPython: <c>Constant(constant value, string? kind)</c>. Value holds the runtime-type literal.</summary>
    public sealed class Constant : Expr
    {
        public object Value { get; }
        public string Kind { get; }       // "u" for u-prefixed strings, else null
        public Constant(object value, string kind, SourceSpan span) : base(span)
        {
            Value = value; Kind = kind;
        }
    }

    public sealed class AttributeExpr : Expr
    {
        public Expr Value { get; }
        public string Attr { get; }
        public ExprContext Ctx { get; }
        public AttributeExpr(Expr value, string attr, ExprContext ctx, SourceSpan span) : base(span)
        {
            Value = value; Attr = attr ?? string.Empty; Ctx = ctx;
        }
    }

    public sealed class Subscript : Expr
    {
        public Expr Value { get; }
        public Expr Slice { get; }
        public ExprContext Ctx { get; }
        public Subscript(Expr value, Expr slice, ExprContext ctx, SourceSpan span) : base(span)
        {
            Value = value; Slice = slice; Ctx = ctx;
        }
    }

    public sealed class Starred : Expr
    {
        public Expr Value { get; }
        public ExprContext Ctx { get; }
        public Starred(Expr value, ExprContext ctx, SourceSpan span) : base(span)
        {
            Value = value; Ctx = ctx;
        }
    }

    public sealed class Name : Expr
    {
        public string Id { get; }
        public ExprContext Ctx { get; }
        public Name(string id, ExprContext ctx, SourceSpan span) : base(span)
        {
            Id = id ?? string.Empty; Ctx = ctx;
        }
    }

    public sealed class ListExpr : Expr
    {
        public IReadOnlyList<Expr> Elts { get; }
        public ExprContext Ctx { get; }
        public ListExpr(IReadOnlyList<Expr> elts, ExprContext ctx, SourceSpan span) : base(span)
        {
            Elts = elts ?? Array.Empty<Expr>();
            Ctx = ctx;
        }
    }

    public sealed class TupleExpr : Expr
    {
        public IReadOnlyList<Expr> Elts { get; }
        public ExprContext Ctx { get; }
        public TupleExpr(IReadOnlyList<Expr> elts, ExprContext ctx, SourceSpan span) : base(span)
        {
            Elts = elts ?? Array.Empty<Expr>();
            Ctx = ctx;
        }
    }

    public sealed class Slice : Expr
    {
        public Expr Lower { get; }
        public Expr Upper { get; }
        public Expr Step { get; }
        public Slice(Expr lower, Expr upper, Expr step, SourceSpan span) : base(span)
        {
            Lower = lower; Upper = upper; Step = step;
        }
    }
}
