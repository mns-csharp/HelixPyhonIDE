using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Typing.Stubs
{
    public enum StubKind
    {
        /// <summary>Top-level function <c>def f(...) -&gt; ...</c>.</summary>
        Function,
        /// <summary>A class (plus optionally its top-level method stubs).</summary>
        Class,
        /// <summary>Bare module-level name: <c>x: int</c> or <c>X = TypeVar("X")</c>.</summary>
        Variable,
        /// <summary>Type alias: <c>Alias = List[int]</c>, <c>type Alias = List[int]</c>.</summary>
        TypeAlias,
        /// <summary>Overload one of several — attach to sibling by the same name.</summary>
        Overload
    }

    /// <summary>
    /// A single top-level declaration inside a <c>.pyi</c> stub. Represents
    /// enough for lookup and (phase-3) type inference without holding the full
    /// CST. <see cref="Type"/> is populated when the declaration has a
    /// resolvable annotation; otherwise <see cref="TypeRef.Unknown"/>.
    /// </summary>
    public sealed class StubDeclaration
    {
        public string Name { get; }
        public StubKind Kind { get; }
        public SourceSpan Span { get; }
        public TypeRef Type { get; }

        /// <summary>Only set for <see cref="StubKind.Class"/> — nested declarations keyed by member name.</summary>
        public IReadOnlyDictionary<string, StubDeclaration> Members { get; }

        /// <summary>Only set for <see cref="StubKind.Class"/> — direct base expressions as dotted names.</summary>
        public IReadOnlyList<string> BaseNames { get; }

        /// <summary>Only set for <see cref="StubKind.Class"/> — <c>class Foo(Protocol):</c> etc.</summary>
        public bool IsProtocol { get; }

        public StubDeclaration(string name, StubKind kind, SourceSpan span, TypeRef type,
            IReadOnlyDictionary<string, StubDeclaration> members = null,
            IReadOnlyList<string> baseNames = null, bool isProtocol = false)
        {
            Name = name ?? string.Empty;
            Kind = kind;
            Span = span;
            Type = type ?? UnknownRef.Instance;
            Members = members;
            BaseNames = baseNames ?? Array.Empty<string>();
            IsProtocol = isProtocol;
        }

        public override string ToString()
        {
            return Kind + " " + Name + (Type != null ? ": " + Type : "");
        }
    }
}
