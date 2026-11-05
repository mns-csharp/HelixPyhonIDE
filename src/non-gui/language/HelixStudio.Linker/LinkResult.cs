namespace HelixPythonNamespace.Linker
{
    using System.Collections.Generic;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Aggregate output of a <see cref="PythonLinker.Link"/> run: the module table,
    /// per-node import + reference resolution, the reverse reference index, and the
    /// PY7xxx diagnostics. Port of the C++ <c>LinkResult</c> (the type-inference
    /// <c>IModuleAttributeResolver</c> hook is intentionally omitted at this layer).
    /// </summary>
    public sealed class LinkResult
    {
        public Dictionary<string, ModuleSummary> Modules { get; } = new Dictionary<string, ModuleSummary>();

        /// <summary>Per-Import/ImportFrom/Alias-node resolution.</summary>
        public Dictionary<AstNode, BindingTarget> ImportResolution { get; } = new Dictionary<AstNode, BindingTarget>();

        /// <summary>Per-Name/Attribute-node resolution from the reference binder.</summary>
        public Dictionary<AstNode, BindingTarget> BindingMap { get; } = new Dictionary<AstNode, BindingTarget>();

        /// <summary>Owning module FQN per resolved node.</summary>
        public Dictionary<AstNode, string> NodeOwner { get; } = new Dictionary<AstNode, string>();

        public ReferenceIndex References { get; private set; } = new ReferenceIndex();

        public List<LinkDiagnostic> Diagnostics { get; } = new List<LinkDiagnostic>();

        public bool LinkSkipped { get; set; }

        internal void SetReferences(ReferenceIndex idx)
        {
            References = idx ?? new ReferenceIndex();
        }
    }
}
