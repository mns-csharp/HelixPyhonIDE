namespace HelixPythonNamespace.CudaVirtualCompiler.Symtable
{
    /// <summary>
    /// The kind of syntactic block a Scope corresponds to. Drives name-lookup rules
    /// (e.g. class members are reachable via `this`, namespaces re-open and merge).
    /// Port of the C++ helix::cuda_vcompiler::symtable::BlockKind
    /// (cuda_vcompiler/symtable/block_kind.h).
    /// </summary>
    public enum BlockKind
    {
        TranslationUnit,
        Namespace,
        Class,
        Function,
        Block,
        TemplateParams,
    }
}
