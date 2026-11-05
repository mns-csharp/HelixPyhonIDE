namespace HelixPythonNamespace.CudaVirtualCompiler.Symtable
{
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.Python.Text;

    public enum SymbolKind
    {
        Function,
        Variable,
        Parameter,
        Field,
        Type,          // struct/class/union/typedef/enum name
        Namespace,
        EnumConstant,
    }

    /// <summary>
    /// One declared entity. Functions are stored one Symbol per overload; lookups
    /// return every matching Symbol so the analyses can reason about overload sets
    /// without full overload resolution. For builtins (the seeded CUDA runtime API)
    /// <see cref="IsBuiltin"/> is true and <see cref="FuncDecl"/> is null. Port of the
    /// C++ helix::cuda_vcompiler::symtable::Symbol (cuda_vcompiler/symtable/symbol.h).
    /// </summary>
    public sealed class Symbol
    {
        public string Name = string.Empty;
        public SymbolKind Kind = SymbolKind.Variable;
        // For variables/params/fields: the declared type. For functions: the
        // return type. For types: empty.
        public CxxType Type = new CxxType();
        public FuncSpace Space = FuncSpace.Host;         // functions only
        public StorageClass Storage = StorageClass.None;
        public bool IsBuiltin;
        public bool IsKernel;                             // __global__ function
        public FunctionDecl FuncDecl;                     // null for builtins / non-functions
        public SourceSpan DefSpan;

        /// <summary>True when the symbol came from a resolved #include (context unit), not
        /// the file under analysis — a same-named declaration in the main file is then a
        /// REDECLARATION (legal), not a redefinition.</summary>
        public bool FromContext;

        public bool ReturnsCudaError()
        {
            return Kind == SymbolKind.Function && Type.Base == "cudaError_t";
        }
    }
}
