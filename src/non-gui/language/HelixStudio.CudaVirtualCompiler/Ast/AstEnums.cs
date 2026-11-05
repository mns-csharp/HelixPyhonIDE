namespace HelixPythonNamespace.CudaVirtualCompiler.Ast
{
    /// <summary>
    /// Effective execution space of a function, derived from its CUDA qualifiers.
    /// A function with both __host__ and __device__ is HostDevice; a plain function
    /// is Host; __global__ is a kernel. Port of the C++
    /// helix::cuda_vcompiler::ast::FuncSpace (cuda_vcompiler/ast/ast_enums.h).
    /// </summary>
    public enum FuncSpace
    {
        Host,        // default, or explicit __host__
        Device,      // __device__
        Global,      // __global__ (kernel)
        HostDevice,  // __host__ __device__
    }

    /// <summary>
    /// Storage / linkage qualifiers that matter to the CUDA analyses. Port of the
    /// C++ helix::cuda_vcompiler::ast::StorageClass.
    /// </summary>
    public enum StorageClass
    {
        None,
        Static,
        Extern,
        // CUDA memory-space storage qualifiers on variables.
        Device,    // __device__ global
        Constant,  // __constant__
        Shared,    // __shared__
        Managed,   // __managed__
    }

    public enum RecordKind { Struct, Class, Union }

    public enum AccessSpec { Default, Public, Protected, Private }

    public enum BinOpKind
    {
        Add, Sub, Mul, Div, Mod,
        Shl, Shr, BitAnd, BitOr, BitXor,
        LogAnd, LogOr,
        Lt, Gt, Le, Ge, Eq, Ne,
        Assign, AddAssign, SubAssign, MulAssign, DivAssign, ModAssign,
        AndAssign, OrAssign, XorAssign, ShlAssign, ShrAssign,
        Comma,
    }

    public enum UnaryOpKind
    {
        Plus, Minus, Not, BitNot,
        Deref,      // *p
        AddrOf,     // &x
        PreInc, PreDec, PostInc, PostDec,
    }
}
