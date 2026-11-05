using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    /// <summary>CPython: <c>FunctionDef(identifier name, arguments args, stmt* body, expr* decorator_list, expr? returns, string? type_comment, type_param* type_params)</c>.</summary>
    public sealed class FunctionDef : Stmt
    {
        public string Name { get; }
        public Arguments Args { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<Expr> DecoratorList { get; }
        public Expr Returns { get; }
        public string TypeComment { get; }
        public IReadOnlyList<TypeParam> TypeParams { get; }

        public FunctionDef(string name, Arguments args, IReadOnlyList<Stmt> body,
            IReadOnlyList<Expr> decoratorList, Expr returns, string typeComment,
            IReadOnlyList<TypeParam> typeParams, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Args = args;
            Body = body ?? Array.Empty<Stmt>();
            DecoratorList = decoratorList ?? Array.Empty<Expr>();
            Returns = returns;
            TypeComment = typeComment;
            TypeParams = typeParams ?? Array.Empty<TypeParam>();
        }
    }

    public sealed class AsyncFunctionDef : Stmt
    {
        public string Name { get; }
        public Arguments Args { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<Expr> DecoratorList { get; }
        public Expr Returns { get; }
        public string TypeComment { get; }
        public IReadOnlyList<TypeParam> TypeParams { get; }

        public AsyncFunctionDef(string name, Arguments args, IReadOnlyList<Stmt> body,
            IReadOnlyList<Expr> decoratorList, Expr returns, string typeComment,
            IReadOnlyList<TypeParam> typeParams, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Args = args;
            Body = body ?? Array.Empty<Stmt>();
            DecoratorList = decoratorList ?? Array.Empty<Expr>();
            Returns = returns;
            TypeComment = typeComment;
            TypeParams = typeParams ?? Array.Empty<TypeParam>();
        }
    }

    /// <summary>CPython: <c>ClassDef(identifier name, expr* bases, keyword* keywords, stmt* body, expr* decorator_list, type_param* type_params)</c>.</summary>
    public sealed class ClassDef : Stmt
    {
        public string Name { get; }
        public IReadOnlyList<Expr> Bases { get; }
        public IReadOnlyList<Keyword> Keywords { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<Expr> DecoratorList { get; }
        public IReadOnlyList<TypeParam> TypeParams { get; }

        public ClassDef(string name, IReadOnlyList<Expr> bases, IReadOnlyList<Keyword> keywords,
            IReadOnlyList<Stmt> body, IReadOnlyList<Expr> decoratorList,
            IReadOnlyList<TypeParam> typeParams, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Bases = bases ?? Array.Empty<Expr>();
            Keywords = keywords ?? Array.Empty<Keyword>();
            Body = body ?? Array.Empty<Stmt>();
            DecoratorList = decoratorList ?? Array.Empty<Expr>();
            TypeParams = typeParams ?? Array.Empty<TypeParam>();
        }
    }

    public sealed class Return : Stmt
    {
        public Expr Value { get; }
        public Return(Expr value, SourceSpan span) : base(span) { Value = value; }
    }

    public sealed class Delete : Stmt
    {
        public IReadOnlyList<Expr> Targets { get; }
        public Delete(IReadOnlyList<Expr> targets, SourceSpan span) : base(span)
        {
            Targets = targets ?? Array.Empty<Expr>();
        }
    }

    public sealed class Assign : Stmt
    {
        public IReadOnlyList<Expr> Targets { get; }
        public Expr Value { get; }
        public string TypeComment { get; }

        public Assign(IReadOnlyList<Expr> targets, Expr value, string typeComment, SourceSpan span) : base(span)
        {
            Targets = targets ?? Array.Empty<Expr>();
            Value = value;
            TypeComment = typeComment;
        }
    }

    public sealed class TypeAlias : Stmt
    {
        public Expr Name { get; }
        public IReadOnlyList<TypeParam> TypeParams { get; }
        public Expr Value { get; }

        public TypeAlias(Expr name, IReadOnlyList<TypeParam> typeParams, Expr value, SourceSpan span) : base(span)
        {
            Name = name;
            TypeParams = typeParams ?? Array.Empty<TypeParam>();
            Value = value;
        }
    }

    public sealed class AugAssign : Stmt
    {
        public Expr Target { get; }
        public BinOp Op { get; }
        public Expr Value { get; }

        public AugAssign(Expr target, BinOp op, Expr value, SourceSpan span) : base(span)
        {
            Target = target; Op = op; Value = value;
        }
    }

    public sealed class AnnAssign : Stmt
    {
        public Expr Target { get; }
        public Expr Annotation { get; }
        public Expr Value { get; }       // nullable
        public bool Simple { get; }

        public AnnAssign(Expr target, Expr annotation, Expr value, bool simple, SourceSpan span) : base(span)
        {
            Target = target; Annotation = annotation; Value = value; Simple = simple;
        }
    }

    public sealed class For : Stmt
    {
        public Expr Target { get; }
        public Expr Iter { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<Stmt> OrElse { get; }
        public string TypeComment { get; }

        public For(Expr target, Expr iter, IReadOnlyList<Stmt> body, IReadOnlyList<Stmt> orElse,
            string typeComment, SourceSpan span) : base(span)
        {
            Target = target;
            Iter = iter;
            Body = body ?? Array.Empty<Stmt>();
            OrElse = orElse ?? Array.Empty<Stmt>();
            TypeComment = typeComment;
        }
    }

    public sealed class AsyncFor : Stmt
    {
        public Expr Target { get; }
        public Expr Iter { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<Stmt> OrElse { get; }
        public string TypeComment { get; }

        public AsyncFor(Expr target, Expr iter, IReadOnlyList<Stmt> body, IReadOnlyList<Stmt> orElse,
            string typeComment, SourceSpan span) : base(span)
        {
            Target = target;
            Iter = iter;
            Body = body ?? Array.Empty<Stmt>();
            OrElse = orElse ?? Array.Empty<Stmt>();
            TypeComment = typeComment;
        }
    }

    public sealed class While : Stmt
    {
        public Expr Test { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<Stmt> OrElse { get; }

        public While(Expr test, IReadOnlyList<Stmt> body, IReadOnlyList<Stmt> orElse, SourceSpan span) : base(span)
        {
            Test = test;
            Body = body ?? Array.Empty<Stmt>();
            OrElse = orElse ?? Array.Empty<Stmt>();
        }
    }

    public sealed class If : Stmt
    {
        public Expr Test { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<Stmt> OrElse { get; }

        public If(Expr test, IReadOnlyList<Stmt> body, IReadOnlyList<Stmt> orElse, SourceSpan span) : base(span)
        {
            Test = test;
            Body = body ?? Array.Empty<Stmt>();
            OrElse = orElse ?? Array.Empty<Stmt>();
        }
    }

    public sealed class With : Stmt
    {
        public IReadOnlyList<WithItem> Items { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public string TypeComment { get; }

        public With(IReadOnlyList<WithItem> items, IReadOnlyList<Stmt> body, string typeComment, SourceSpan span) : base(span)
        {
            Items = items ?? Array.Empty<WithItem>();
            Body = body ?? Array.Empty<Stmt>();
            TypeComment = typeComment;
        }
    }

    public sealed class AsyncWith : Stmt
    {
        public IReadOnlyList<WithItem> Items { get; }
        public IReadOnlyList<Stmt> Body { get; }
        public string TypeComment { get; }

        public AsyncWith(IReadOnlyList<WithItem> items, IReadOnlyList<Stmt> body, string typeComment, SourceSpan span) : base(span)
        {
            Items = items ?? Array.Empty<WithItem>();
            Body = body ?? Array.Empty<Stmt>();
            TypeComment = typeComment;
        }
    }

    public sealed class Match : Stmt
    {
        public Expr Subject { get; }
        public IReadOnlyList<MatchCase> Cases { get; }

        public Match(Expr subject, IReadOnlyList<MatchCase> cases, SourceSpan span) : base(span)
        {
            Subject = subject;
            Cases = cases ?? Array.Empty<MatchCase>();
        }
    }

    public sealed class Raise : Stmt
    {
        public Expr Exc { get; }
        public Expr Cause { get; }
        public Raise(Expr exc, Expr cause, SourceSpan span) : base(span) { Exc = exc; Cause = cause; }
    }

    public sealed class Try : Stmt
    {
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<ExceptHandler> Handlers { get; }
        public IReadOnlyList<Stmt> OrElse { get; }
        public IReadOnlyList<Stmt> FinalBody { get; }

        public Try(IReadOnlyList<Stmt> body, IReadOnlyList<ExceptHandler> handlers,
            IReadOnlyList<Stmt> orElse, IReadOnlyList<Stmt> finalBody, SourceSpan span) : base(span)
        {
            Body = body ?? Array.Empty<Stmt>();
            Handlers = handlers ?? Array.Empty<ExceptHandler>();
            OrElse = orElse ?? Array.Empty<Stmt>();
            FinalBody = finalBody ?? Array.Empty<Stmt>();
        }
    }

    public sealed class TryStar : Stmt
    {
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<ExceptHandler> Handlers { get; }
        public IReadOnlyList<Stmt> OrElse { get; }
        public IReadOnlyList<Stmt> FinalBody { get; }

        public TryStar(IReadOnlyList<Stmt> body, IReadOnlyList<ExceptHandler> handlers,
            IReadOnlyList<Stmt> orElse, IReadOnlyList<Stmt> finalBody, SourceSpan span) : base(span)
        {
            Body = body ?? Array.Empty<Stmt>();
            Handlers = handlers ?? Array.Empty<ExceptHandler>();
            OrElse = orElse ?? Array.Empty<Stmt>();
            FinalBody = finalBody ?? Array.Empty<Stmt>();
        }
    }

    public sealed class Assert : Stmt
    {
        public Expr Test { get; }
        public Expr Msg { get; }
        public Assert(Expr test, Expr msg, SourceSpan span) : base(span) { Test = test; Msg = msg; }
    }

    public sealed class Import : Stmt
    {
        public IReadOnlyList<Alias> Names { get; }
        public Import(IReadOnlyList<Alias> names, SourceSpan span) : base(span)
        {
            Names = names ?? Array.Empty<Alias>();
        }
    }

    public sealed class ImportFrom : Stmt
    {
        public string Module { get; }      // may be null for relative-only "from . import x"
        public IReadOnlyList<Alias> Names { get; }
        public int Level { get; }

        public ImportFrom(string module, IReadOnlyList<Alias> names, int level, SourceSpan span) : base(span)
        {
            Module = module;
            Names = names ?? Array.Empty<Alias>();
            Level = level;
        }
    }

    public sealed class Global : Stmt
    {
        public IReadOnlyList<string> Names { get; }
        public Global(IReadOnlyList<string> names, SourceSpan span) : base(span)
        {
            Names = names ?? Array.Empty<string>();
        }
    }

    public sealed class Nonlocal : Stmt
    {
        public IReadOnlyList<string> Names { get; }
        public Nonlocal(IReadOnlyList<string> names, SourceSpan span) : base(span)
        {
            Names = names ?? Array.Empty<string>();
        }
    }

    /// <summary>Expression-as-statement (CPython's <c>Expr</c> statement; renamed to avoid clashing with our Expr base).</summary>
    public sealed class ExprStmt : Stmt
    {
        public Expr Value { get; }
        public ExprStmt(Expr value, SourceSpan span) : base(span) { Value = value; }
    }

    public sealed class Pass : Stmt
    {
        public Pass(SourceSpan span) : base(span) { }
    }

    public sealed class Break : Stmt
    {
        public Break(SourceSpan span) : base(span) { }
    }

    public sealed class Continue : Stmt
    {
        public Continue(SourceSpan span) : base(span) { }
    }
}
