using System;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Symtable
{
    /// <summary>
    /// Symbol-flag bitset, mirroring CPython's <c>SYMTABLE_*</c> and <c>DEF_*</c>
    /// constants in <c>Include/internal/pycore_symtable.h</c>. Values are
    /// deliberately identical so the port code reads like its C counterpart.
    /// </summary>
    [Flags]
    public enum SymbolFlag
    {
        None         = 0,
        DefGlobal    = 1 << 0,   // `global x`
        DefLocal     = 1 << 1,   // bound to the current block
        DefParam     = 1 << 2,   // function parameter
        Use          = 1 << 3,   // name is referenced
        DefFree      = 1 << 4,   // name is referenced in an enclosing function
        DefFreeClass = 1 << 5,   // free reference from within a class body
        DefImport    = 1 << 6,   // imported
        DefAnnot     = 1 << 7,   // has an annotation (no binding)
        DefCompIter  = 1 << 8,   // iteration variable of a comprehension
        DefTypeParam = 1 << 9,   // type parameter (PEP 695)
        DefCompCell  = 1 << 10,  // cell variable in a comprehension

        // Convenience: anything that makes `name` bound in its block.
        DefBound     = DefLocal | DefParam | DefImport
    }

    /// <summary>
    /// A name entry inside a <see cref="Scope"/>. Tracks which definition/use
    /// flags have been observed and the first definition span for diagnostics.
    /// </summary>
    public sealed class Symbol
    {
        public string Name { get; }
        public SymbolFlag Flags { get; private set; }
        public SourceSpan FirstDefSpan { get; private set; }
        public SourceSpan FirstUseSpan { get; private set; }

        // Filled in after Pass 2 (NameResolver).
        public SymbolScope Scope { get; internal set; }

        internal Symbol(string name)
        {
            Name = name ?? string.Empty;
            Flags = SymbolFlag.None;
        }

        public bool Has(SymbolFlag f) { return (Flags & f) != 0; }

        internal void AddFlag(SymbolFlag f, SourceSpan span)
        {
            if ((f & (SymbolFlag.DefLocal | SymbolFlag.DefParam | SymbolFlag.DefImport | SymbolFlag.DefGlobal))
                != 0 && FirstDefSpan.Length == 0 && !Has(SymbolFlag.DefBound | SymbolFlag.DefGlobal))
            {
                FirstDefSpan = span;
            }
            if ((f & SymbolFlag.Use) != 0 && FirstUseSpan.Length == 0)
            {
                FirstUseSpan = span;
            }
            Flags |= f;
        }

        public override string ToString()
        {
            return Name + " [" + Flags + "]";
        }
    }
}
