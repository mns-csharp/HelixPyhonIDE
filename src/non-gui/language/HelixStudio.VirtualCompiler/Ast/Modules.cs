using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    /// <summary>CPython: <c>Module(stmt* body, type_ignore* type_ignores)</c>.</summary>
    public sealed class Module : Mod
    {
        public IReadOnlyList<Stmt> Body { get; }
        public IReadOnlyList<TypeIgnore> TypeIgnores { get; }

        public Module(IReadOnlyList<Stmt> body, IReadOnlyList<TypeIgnore> typeIgnores, SourceSpan span)
            : base(span)
        {
            Body = body ?? Array.Empty<Stmt>();
            TypeIgnores = typeIgnores ?? Array.Empty<TypeIgnore>();
        }
    }

    public sealed class Interactive : Mod
    {
        public IReadOnlyList<Stmt> Body { get; }
        public Interactive(IReadOnlyList<Stmt> body, SourceSpan span) : base(span)
        {
            Body = body ?? Array.Empty<Stmt>();
        }
    }

    public sealed class Expression : Mod
    {
        public Expr Body { get; }
        public Expression(Expr body, SourceSpan span) : base(span)
        {
            Body = body;
        }
    }

    public sealed class FunctionType : Mod
    {
        public IReadOnlyList<Expr> ArgTypes { get; }
        public Expr Returns { get; }
        public FunctionType(IReadOnlyList<Expr> argTypes, Expr returns, SourceSpan span) : base(span)
        {
            ArgTypes = argTypes ?? Array.Empty<Expr>();
            Returns = returns;
        }
    }

    /// <summary>CPython: <c>type_ignore = TypeIgnore(int lineno, string tag)</c>.</summary>
    public sealed class TypeIgnore : AstHelper
    {
        public string Tag { get; }
        public TypeIgnore(string tag, SourceSpan span) : base(span)
        {
            Tag = tag ?? string.Empty;
        }
    }
}
