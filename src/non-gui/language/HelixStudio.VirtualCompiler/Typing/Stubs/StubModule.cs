using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;

namespace HelixPythonNamespace.VirtualCompiler.Typing.Stubs
{
    /// <summary>
    /// A single parsed <c>.pyi</c> module. Keyed by its dotted module name
    /// (e.g. <c>"builtins"</c>, <c>"os.path"</c>, <c>"typing"</c>). Declarations
    /// are indexed by bare identifier for fast <see cref="TryGet"/> lookup.
    /// </summary>
    public sealed class StubModule
    {
        public string QualifiedName { get; }
        public IReadOnlyDictionary<string, StubDeclaration> Declarations { get; }
        public IReadOnlyList<ParseDiagnostic> ParseDiagnostics { get; }

        public StubModule(string qualifiedName,
            IReadOnlyDictionary<string, StubDeclaration> declarations,
            IReadOnlyList<ParseDiagnostic> parseDiagnostics)
        {
            QualifiedName = qualifiedName ?? string.Empty;
            Declarations = declarations ?? new Dictionary<string, StubDeclaration>();
            ParseDiagnostics = parseDiagnostics ?? Array.Empty<ParseDiagnostic>();
        }

        public bool TryGet(string name, out StubDeclaration decl)
        {
            return Declarations.TryGetValue(name, out decl);
        }

        public override string ToString()
        {
            return "StubModule " + QualifiedName + " (" + Declarations.Count + " decls)";
        }
    }
}
