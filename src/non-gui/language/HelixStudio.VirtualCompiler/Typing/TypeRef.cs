using System;
using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>
    /// Base class for every type reference the virtual compiler propagates.
    /// Modelled loosely on mypy's <c>Type</c> hierarchy — one concrete subclass
    /// per category. Values are immutable; equality is structural by default
    /// (two ClassRefs with the same QualifiedName are equal).
    /// </summary>
    public abstract class TypeRef
    {
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            AppendTo(sb);
            return sb.ToString();
        }

        public abstract void AppendTo(StringBuilder sb);
    }

    /// <summary>Gradual-type escape hatch (PEP 484 <c>Any</c>). Every operation succeeds.</summary>
    public sealed class AnyRef : TypeRef
    {
        public static readonly AnyRef Instance = new AnyRef();
        private AnyRef() { }
        public override void AppendTo(StringBuilder sb) { sb.Append("Any"); }
    }

    /// <summary>Inference failure marker — like AnyRef but signals "we gave up", not "user said Any".</summary>
    public sealed class UnknownRef : TypeRef
    {
        public static readonly UnknownRef Instance = new UnknownRef();
        private UnknownRef() { }
        public override void AppendTo(StringBuilder sb) { sb.Append("Unknown"); }
    }

    /// <summary>Singleton <c>None</c> — the value type of <c>None</c>.</summary>
    public sealed class NoneRef : TypeRef
    {
        public static readonly NoneRef Instance = new NoneRef();
        private NoneRef() { }
        public override void AppendTo(StringBuilder sb) { sb.Append("None"); }
    }

    /// <summary>Bottom type — e.g. a function that never returns.</summary>
    public sealed class NeverRef : TypeRef
    {
        public static readonly NeverRef Instance = new NeverRef();
        private NeverRef() { }
        public override void AppendTo(StringBuilder sb) { sb.Append("Never"); }
    }

    /// <summary>Reference to a class or type alias by its fully qualified dotted name.</summary>
    public sealed class ClassRef : TypeRef
    {
        public string QualifiedName { get; }

        public ClassRef(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName)) throw new ArgumentException("qualifiedName");
            QualifiedName = qualifiedName;
        }

        public override void AppendTo(StringBuilder sb) { sb.Append(QualifiedName); }

        public override bool Equals(object obj)
        {
            return obj is ClassRef other && other.QualifiedName == QualifiedName;
        }
        public override int GetHashCode() { return QualifiedName.GetHashCode(); }
    }

    /// <summary>Class with type arguments (e.g. <c>list[int]</c>, <c>dict[str, int]</c>).</summary>
    public sealed class GenericRef : TypeRef
    {
        public ClassRef Base { get; }
        public IReadOnlyList<TypeRef> Args { get; }

        public GenericRef(ClassRef baseRef, IReadOnlyList<TypeRef> args)
        {
            Base = baseRef ?? throw new ArgumentNullException(nameof(baseRef));
            Args = args ?? Array.Empty<TypeRef>();
        }

        public override void AppendTo(StringBuilder sb)
        {
            Base.AppendTo(sb);
            sb.Append('[');
            for (int i = 0; i < Args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Args[i].AppendTo(sb);
            }
            sb.Append(']');
        }
    }

    /// <summary>Disjoint-sum type, e.g. <c>str | int | None</c>.</summary>
    public sealed class UnionRef : TypeRef
    {
        public IReadOnlyList<TypeRef> Members { get; }

        public UnionRef(IReadOnlyList<TypeRef> members)
        {
            Members = members ?? Array.Empty<TypeRef>();
        }

        public override void AppendTo(StringBuilder sb)
        {
            for (int i = 0; i < Members.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                Members[i].AppendTo(sb);
            }
        }
    }

    /// <summary>Fixed-length tuple type <c>tuple[T1, T2, …]</c> or <c>tuple[T, ...]</c> if <see cref="IsVariadic"/>.</summary>
    public sealed class TupleRef : TypeRef
    {
        public IReadOnlyList<TypeRef> Items { get; }
        public bool IsVariadic { get; }

        public TupleRef(IReadOnlyList<TypeRef> items, bool isVariadic)
        {
            Items = items ?? Array.Empty<TypeRef>();
            IsVariadic = isVariadic;
        }

        public override void AppendTo(StringBuilder sb)
        {
            sb.Append("tuple[");
            for (int i = 0; i < Items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Items[i].AppendTo(sb);
            }
            if (IsVariadic) sb.Append(", ...");
            sb.Append(']');
        }
    }

    /// <summary>Function signature — <c>(params) -&gt; return</c>. Params are unnamed for positional-only use.</summary>
    public sealed class CallableRef : TypeRef
    {
        public IReadOnlyList<CallableParam> Params { get; }
        public TypeRef ReturnType { get; }
        public bool IsEllipsisParams { get; }  // Callable[..., R]

        public CallableRef(IReadOnlyList<CallableParam> paramList, TypeRef returnType, bool isEllipsisParams = false)
        {
            Params = paramList ?? Array.Empty<CallableParam>();
            ReturnType = returnType ?? AnyRef.Instance;
            IsEllipsisParams = isEllipsisParams;
        }

        public override void AppendTo(StringBuilder sb)
        {
            sb.Append("(");
            if (IsEllipsisParams) sb.Append("...");
            else
            {
                for (int i = 0; i < Params.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Params[i].AppendTo(sb);
                }
            }
            sb.Append(") -> ");
            ReturnType.AppendTo(sb);
        }
    }

    public enum CallableParamKind
    {
        PositionalOnly,
        PositionalOrKeyword,
        VarPositional,
        KeywordOnly,
        VarKeyword
    }

    public sealed class CallableParam
    {
        public string Name { get; }
        public TypeRef Type { get; }
        public CallableParamKind Kind { get; }
        public bool HasDefault { get; }

        public CallableParam(string name, TypeRef type, CallableParamKind kind, bool hasDefault)
        {
            Name = name ?? string.Empty;
            Type = type ?? AnyRef.Instance;
            Kind = kind;
            HasDefault = hasDefault;
        }

        internal void AppendTo(StringBuilder sb)
        {
            switch (Kind)
            {
                case CallableParamKind.VarPositional: sb.Append('*'); break;
                case CallableParamKind.VarKeyword: sb.Append("**"); break;
            }
            if (!string.IsNullOrEmpty(Name)) { sb.Append(Name); sb.Append(": "); }
            Type.AppendTo(sb);
            if (HasDefault) sb.Append(" = ...");
        }
    }

    /// <summary>A specific literal value — e.g. <c>Literal["on", "off"]</c> has two LiteralRef members.</summary>
    public sealed class LiteralRef : TypeRef
    {
        public object Value { get; }
        public LiteralRef(object value) { Value = value; }

        public override void AppendTo(StringBuilder sb)
        {
            sb.Append("Literal[");
            if (Value == null) sb.Append("None");
            else if (Value is string s) { sb.Append('"'); sb.Append(s); sb.Append('"'); }
            else sb.Append(Value);
            sb.Append(']');
        }
    }

    /// <summary>PEP 484 TypeVar placeholder — resolved by context during substitution.</summary>
    public sealed class TypeVarRef : TypeRef
    {
        public string Name { get; }
        public TypeRef Bound { get; }       // nullable
        public TypeVarRef(string name, TypeRef bound = null)
        {
            Name = name ?? string.Empty;
            Bound = bound;
        }
        public override void AppendTo(StringBuilder sb) { sb.Append(Name); }
    }

    /// <summary>
    /// Convenience singletons for common builtin classes. Kept flyweight so equality via
    /// <see cref="ClassRef.Equals(object)"/> works without allocations for hot names.
    /// </summary>
    public static class BuiltinRefs
    {
        public static readonly ClassRef Int = new ClassRef("builtins.int");
        public static readonly ClassRef Float = new ClassRef("builtins.float");
        public static readonly ClassRef Bool = new ClassRef("builtins.bool");
        public static readonly ClassRef Str = new ClassRef("builtins.str");
        public static readonly ClassRef Bytes = new ClassRef("builtins.bytes");
        public static readonly ClassRef Complex = new ClassRef("builtins.complex");
        public static readonly ClassRef Object = new ClassRef("builtins.object");
        public static readonly ClassRef List = new ClassRef("builtins.list");
        public static readonly ClassRef Dict = new ClassRef("builtins.dict");
        public static readonly ClassRef Set = new ClassRef("builtins.set");
        public static readonly ClassRef FrozenSet = new ClassRef("builtins.frozenset");
        public static readonly ClassRef Tuple = new ClassRef("builtins.tuple");
        public static readonly ClassRef Type = new ClassRef("builtins.type");
    }
}
