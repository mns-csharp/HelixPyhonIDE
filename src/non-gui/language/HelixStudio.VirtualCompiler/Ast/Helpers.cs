using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    /// <summary>CPython: <c>arguments = (posonlyargs, args, vararg, kwonlyargs, kw_defaults, kwarg, defaults)</c>.</summary>
    public sealed class Arguments : AstHelper
    {
        public IReadOnlyList<Arg> PosOnlyArgs { get; }
        public IReadOnlyList<Arg> Args { get; }
        public Arg VarArg { get; }                       // *args  (null if absent)
        public IReadOnlyList<Arg> KwOnlyArgs { get; }
        public IReadOnlyList<Expr> KwDefaults { get; }   // one slot per KwOnlyArg (null = no default)
        public Arg KwArg { get; }                        // **kwargs (null if absent)
        public IReadOnlyList<Expr> Defaults { get; }     // defaults for PosOnlyArgs + Args, tail-aligned

        public Arguments(
            IReadOnlyList<Arg> posOnlyArgs,
            IReadOnlyList<Arg> args,
            Arg varArg,
            IReadOnlyList<Arg> kwOnlyArgs,
            IReadOnlyList<Expr> kwDefaults,
            Arg kwArg,
            IReadOnlyList<Expr> defaults,
            SourceSpan span) : base(span)
        {
            PosOnlyArgs = posOnlyArgs ?? Array.Empty<Arg>();
            Args = args ?? Array.Empty<Arg>();
            VarArg = varArg;
            KwOnlyArgs = kwOnlyArgs ?? Array.Empty<Arg>();
            KwDefaults = kwDefaults ?? Array.Empty<Expr>();
            KwArg = kwArg;
            Defaults = defaults ?? Array.Empty<Expr>();
        }

        public static Arguments Empty(SourceSpan span)
        {
            return new Arguments(null, null, null, null, null, null, null, span);
        }
    }

    /// <summary>CPython: <c>arg = (identifier arg, expr? annotation, string? type_comment)</c>.</summary>
    public sealed class Arg : AstHelper
    {
        public string Name { get; }
        public Expr Annotation { get; }
        public string TypeComment { get; }

        public Arg(string name, Expr annotation, string typeComment, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Annotation = annotation;
            TypeComment = typeComment;
        }
    }

    /// <summary>CPython: <c>keyword = (identifier? arg, expr value)</c>. <c>arg</c> null means **kwargs-splat.</summary>
    public sealed class Keyword : AstHelper
    {
        public string Name { get; }
        public Expr Value { get; }

        public Keyword(string name, Expr value, SourceSpan span) : base(span)
        {
            Name = name;
            Value = value;
        }
    }

    /// <summary>CPython: <c>alias = (identifier name, identifier? asname)</c>.</summary>
    public sealed class Alias : AstHelper
    {
        public string Name { get; }
        public string AsName { get; }

        public Alias(string name, string asName, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            AsName = asName;
        }
    }

    /// <summary>CPython: <c>withitem = (expr context_expr, expr? optional_vars)</c>.</summary>
    public sealed class WithItem : AstHelper
    {
        public Expr ContextExpr { get; }
        public Expr OptionalVars { get; }

        public WithItem(Expr contextExpr, Expr optionalVars, SourceSpan span) : base(span)
        {
            ContextExpr = contextExpr;
            OptionalVars = optionalVars;
        }
    }

    /// <summary>CPython: <c>comprehension = (expr target, expr iter, expr* ifs, int is_async)</c>.</summary>
    public sealed class Comprehension : AstHelper
    {
        public Expr Target { get; }
        public Expr Iter { get; }
        public IReadOnlyList<Expr> Ifs { get; }
        public bool IsAsync { get; }

        public Comprehension(Expr target, Expr iter, IReadOnlyList<Expr> ifs, bool isAsync, SourceSpan span) : base(span)
        {
            Target = target;
            Iter = iter;
            Ifs = ifs ?? Array.Empty<Expr>();
            IsAsync = isAsync;
        }
    }

    /// <summary>CPython: <c>excepthandler = ExceptHandler(expr? type, identifier? name, stmt* body)</c>.</summary>
    public sealed class ExceptHandler : AstHelper
    {
        public Expr Type { get; }
        public string Name { get; }
        public IReadOnlyList<Stmt> Body { get; }

        public ExceptHandler(Expr type, string name, IReadOnlyList<Stmt> body, SourceSpan span) : base(span)
        {
            Type = type;
            Name = name;
            Body = body ?? Array.Empty<Stmt>();
        }
    }

    /// <summary>CPython: <c>match_case = (pattern, expr? guard, stmt* body)</c>.</summary>
    public sealed class MatchCase : AstHelper
    {
        public Pattern Pattern { get; }
        public Expr Guard { get; }
        public IReadOnlyList<Stmt> Body { get; }

        public MatchCase(Pattern pattern, Expr guard, IReadOnlyList<Stmt> body, SourceSpan span) : base(span)
        {
            Pattern = pattern;
            Guard = guard;
            Body = body ?? Array.Empty<Stmt>();
        }
    }

    /// <summary>CPython 3.12+: <c>type_param = TypeVar | ParamSpec | TypeVarTuple</c>.</summary>
    public abstract class TypeParam : AstHelper
    {
        public string Name { get; }
        protected TypeParam(string name, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
        }
    }

    public sealed class TypeVarParam : TypeParam
    {
        public Expr Bound { get; }
        public TypeVarParam(string name, Expr bound, SourceSpan span) : base(name, span)
        {
            Bound = bound;
        }
    }

    public sealed class ParamSpecParam : TypeParam
    {
        public ParamSpecParam(string name, SourceSpan span) : base(name, span) { }
    }

    public sealed class TypeVarTupleParam : TypeParam
    {
        public TypeVarTupleParam(string name, SourceSpan span) : base(name, span) { }
    }
}
