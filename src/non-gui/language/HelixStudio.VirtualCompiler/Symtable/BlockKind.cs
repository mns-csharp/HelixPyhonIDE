namespace HelixPythonNamespace.VirtualCompiler.Symtable
{
    /// <summary>
    /// Block kinds from CPython's <c>_Py_block_ty</c> (<c>Python/symtable.c</c>).
    /// Each scope node is tagged with one of these so lookup rules can branch.
    /// </summary>
    public enum BlockKind
    {
        Module,
        Function,
        Class,
        Annotation,          // function annotation evaluated lazily (PEP 563)
        TypeVarBound,        // bound expression of a generic TypeVar
        TypeAlias,           // type X = ... (PEP 695)
        TypeParam,           // the brackets of def foo[T]() or class C[T]
        Comprehension,       // list/set/dict/generator comp body
        Lambda               // lambda expressions
    }

    /// <summary>Where a name resolves after binding analysis (CPython's <c>_Py_SymbolScope_ty</c>).</summary>
    public enum SymbolScope
    {
        Unknown = 0,
        Local = 1,
        GlobalExplicit = 2,
        GlobalImplicit = 3,
        Free = 4,
        Cell = 5
    }
}
