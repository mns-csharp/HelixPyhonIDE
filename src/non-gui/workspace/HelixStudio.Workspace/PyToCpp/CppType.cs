using System;
using System.Collections.Generic;
using System.Linq;

namespace HelixPythonNamespace.Workspace.PyToCpp
{
    /// <summary>
    /// Pass 1's own strict type model — deliberately NOT the VirtualCompiler's gradual
    /// TypeRef/ClassRef system (which is designed to degrade to Any/Unknown for the IDE's
    /// best-effort linting). Every value Pass 1 accepts must resolve to exactly one of
    /// these concrete types; anything that would resolve to "unknown" is a rejection, not
    /// a gradual-typing fallback.
    /// </summary>
    public abstract class CppType : IEquatable<CppType>
    {
        public abstract bool Equals(CppType other);
        public override bool Equals(object obj) => obj is CppType t && Equals(t);
        public abstract override int GetHashCode();
        public abstract override string ToString();

        // System.HashCode isn't available on net472 — a small manual combine instead.
        protected static int Combine(int a, int b) => unchecked((a * 397) ^ b);
        protected static int Combine(int a, object b) => Combine(a, b?.GetHashCode() ?? 0);

        public static readonly CppType Int = new PrimitiveType("int");
        public static readonly CppType Float = new PrimitiveType("float");
        public static readonly CppType Bool = new PrimitiveType("bool");
        public static readonly CppType Str = new PrimitiveType("str");
        public static readonly CppType None = new PrimitiveType("None");

        public bool IsNumeric => this == Int || this == Float;
    }

    public sealed class PrimitiveType : CppType
    {
        public string Name { get; }
        public PrimitiveType(string name) { Name = name; }
        public override bool Equals(CppType other) => other is PrimitiveType p && p.Name == Name;
        public override int GetHashCode() => Name.GetHashCode();
        public override string ToString() => Name;
    }

    public sealed class ListType : CppType
    {
        public CppType Element { get; }
        public ListType(CppType element) { Element = element; }
        public override bool Equals(CppType other) => other is ListType l && l.Element.Equals(Element);
        public override int GetHashCode() => Combine(typeof(ListType).GetHashCode(), Element);
        public override string ToString() => "List[" + Element + "]";
    }

    public sealed class DictType : CppType
    {
        public CppType Key { get; }
        public CppType Value { get; }
        public DictType(CppType key, CppType value) { Key = key; Value = value; }
        public override bool Equals(CppType other) => other is DictType d && d.Key.Equals(Key) && d.Value.Equals(Value);
        public override int GetHashCode() => Combine(Combine(typeof(DictType).GetHashCode(), Key), Value);
        public override string ToString() => "Dict[" + Key + ", " + Value + "]";
    }

    public sealed class SetType : CppType
    {
        public CppType Element { get; }
        public SetType(CppType element) { Element = element; }
        public override bool Equals(CppType other) => other is SetType s && s.Element.Equals(Element);
        public override int GetHashCode() => Combine(typeof(SetType).GetHashCode(), Element);
        public override string ToString() => "Set[" + Element + "]";
    }

    public sealed class TupleType : CppType
    {
        public IReadOnlyList<CppType> Elements { get; }
        public TupleType(IReadOnlyList<CppType> elements) { Elements = elements; }
        public override bool Equals(CppType other) => other is TupleType t && t.Elements.SequenceEqual(Elements);
        public override int GetHashCode()
        {
            int h = typeof(TupleType).GetHashCode();
            foreach (var e in Elements) h = Combine(h, e);
            return h;
        }
        public override string ToString() => "Tuple[" + string.Join(", ", Elements) + "]";
    }

    public sealed class OptionalType : CppType
    {
        public CppType Inner { get; }
        public OptionalType(CppType inner) { Inner = inner; }
        public override bool Equals(CppType other) => other is OptionalType o && o.Inner.Equals(Inner);
        public override int GetHashCode() => Combine(typeof(OptionalType).GetHashCode(), Inner);
        public override string ToString() => "Optional[" + Inner + "]";
    }

    public sealed class ClassType : CppType
    {
        public string Name { get; }
        public ClassType(string name) { Name = name; }
        public override bool Equals(CppType other) => other is ClassType c && c.Name == Name;
        public override int GetHashCode() => Combine(typeof(ClassType).GetHashCode(), Name);
        public override string ToString() => Name;
    }

    /// <summary>A callable's shape — used for the function's own entry and for a bound method lookup.</summary>
    public sealed class FunctionType : CppType
    {
        public IReadOnlyList<CppType> Parameters { get; }
        public CppType Return { get; }
        public FunctionType(IReadOnlyList<CppType> parameters, CppType ret) { Parameters = parameters; Return = ret; }
        public override bool Equals(CppType other) =>
            other is FunctionType f && f.Return.Equals(Return) && f.Parameters.SequenceEqual(Parameters);
        public override int GetHashCode()
        {
            int h = Combine(typeof(FunctionType).GetHashCode(), Return);
            foreach (var p in Parameters) h = Combine(h, p);
            return h;
        }
        public override string ToString() => "(" + string.Join(", ", Parameters) + ") -> " + Return;
    }
}
