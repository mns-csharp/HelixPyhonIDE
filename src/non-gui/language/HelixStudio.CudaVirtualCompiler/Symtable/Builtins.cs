namespace HelixPythonNamespace.CudaVirtualCompiler.Symtable
{
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;

    /// <summary>
    /// Seeds the table's global scope (and function index) with the CUDA built-ins
    /// the analyses reason about: built-in variables (threadIdx/blockIdx/blockDim/
    /// gridDim/warpSize), fundamental CUDA types (dim3, cudaError_t, cudaStream_t,
    /// cudaMemcpyKind enumerators, ...), and the common runtime API + device
    /// intrinsics with their return type and execution space. This stands in for the
    /// CUDA headers we deliberately do not parse. Port of the C++
    /// helix::cuda_vcompiler::symtable::SeedBuiltins (cuda_vcompiler/symtable/builtins.cpp).
    /// </summary>
    public static class Builtins
    {
        public static void SeedBuiltins(SymbolTable table)
        {
            Scope g = table.Root;

            // ---- Built-in variables (device-only contexts) ----
            g.Add(MakeVar("threadIdx", "uint3"));
            g.Add(MakeVar("blockIdx", "uint3"));
            g.Add(MakeVar("blockDim", "dim3"));
            g.Add(MakeVar("gridDim", "dim3"));
            g.Add(MakeVar("warpSize", "int"));

            // ---- Fundamental CUDA types ----
            string[] types =
            {
                "dim3", "uint3", "int2", "int3", "int4",
                "float2", "float3", "float4", "double2",
                "char4", "uchar4",
                "cudaError_t", "cudaError", "cudaStream_t", "cudaEvent_t",
                "cudaDeviceProp", "cudaMemcpyKind", "cudaFuncAttributes",
                "size_t", "ssize_t", "cudaTextureObject_t",
                "cudaSurfaceObject_t",
            };
            for (int i = 0; i < types.Length; ++i) g.Add(MakeType(types[i]));

            // ---- Enum constants the analyses recognize ----
            g.Add(MakeEnumConst("cudaSuccess"));
            g.Add(MakeEnumConst("cudaMemcpyHostToHost"));
            g.Add(MakeEnumConst("cudaMemcpyHostToDevice"));
            g.Add(MakeEnumConst("cudaMemcpyDeviceToHost"));
            g.Add(MakeEnumConst("cudaMemcpyDeviceToDevice"));
            g.Add(MakeEnumConst("cudaMemcpyDefault"));

            // ---- Runtime API (host, returns cudaError_t unless noted) ----
            AddFunc(table, g, "cudaMalloc", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaMallocManaged", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaMallocHost", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaMallocPitch", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaFree", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaFreeHost", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaMemcpy", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaMemcpyAsync", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaMemset", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaMemsetAsync", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaDeviceSynchronize", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaStreamSynchronize", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaStreamCreate", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaStreamDestroy", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaEventCreate", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaEventRecord", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaEventSynchronize", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaEventElapsedTime", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaEventDestroy", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaGetLastError", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaPeekAtLastError", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaGetErrorString", "const char*", FuncSpace.Host);
            AddFunc(table, g, "cudaGetErrorName", "const char*", FuncSpace.Host);
            AddFunc(table, g, "cudaSetDevice", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaGetDevice", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaGetDeviceCount", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaGetDeviceProperties", "cudaError_t", FuncSpace.Host);
            AddFunc(table, g, "cudaDeviceReset", "cudaError_t", FuncSpace.Host);

            // ---- Device intrinsics ----
            AddFunc(table, g, "__syncthreads", "void", FuncSpace.Device);
            AddFunc(table, g, "__syncwarp", "void", FuncSpace.Device);
            AddFunc(table, g, "__threadfence", "void", FuncSpace.Device);
            AddFunc(table, g, "__threadfence_block", "void", FuncSpace.Device);
            AddFunc(table, g, "__ballot_sync", "unsigned", FuncSpace.Device);
            AddFunc(table, g, "__shfl_sync", "int", FuncSpace.Device);
            AddFunc(table, g, "__shfl_down_sync", "int", FuncSpace.Device);
            AddFunc(table, g, "atomicAdd", "int", FuncSpace.Device);
            AddFunc(table, g, "atomicSub", "int", FuncSpace.Device);
            AddFunc(table, g, "atomicExch", "int", FuncSpace.Device);
            AddFunc(table, g, "atomicMin", "int", FuncSpace.Device);
            AddFunc(table, g, "atomicMax", "int", FuncSpace.Device);
            AddFunc(table, g, "atomicCAS", "int", FuncSpace.Device);

            // ---- Callable from both spaces ----
            AddFunc(table, g, "printf", "int", FuncSpace.HostDevice);
            AddFunc(table, g, "min", "int", FuncSpace.HostDevice);
            AddFunc(table, g, "max", "int", FuncSpace.HostDevice);
        }

        private static Symbol MakeType(string name)
        {
            Symbol s = new Symbol();
            s.Name = name;
            s.Kind = SymbolKind.Type;
            s.IsBuiltin = true;
            return s;
        }

        private static Symbol MakeVar(string name, string typeBase)
        {
            Symbol s = new Symbol();
            s.Name = name;
            s.Kind = SymbolKind.Variable;
            s.Type = new CxxType(typeBase);
            s.IsBuiltin = true;
            return s;
        }

        private static Symbol MakeEnumConst(string name)
        {
            Symbol s = new Symbol();
            s.Name = name;
            s.Kind = SymbolKind.EnumConstant;
            s.Type = new CxxType("int");
            s.IsBuiltin = true;
            return s;
        }

        private static Symbol MakeFunc(string name, string returnBase, FuncSpace space)
        {
            Symbol s = new Symbol();
            s.Name = name;
            s.Kind = SymbolKind.Function;
            s.Type = new CxxType(returnBase);
            s.Space = space;
            s.IsBuiltin = true;
            return s;
        }

        private static void AddFunc(SymbolTable table, Scope g, string name, string ret, FuncSpace sp)
        {
            Symbol s = g.Add(MakeFunc(name, ret, sp));
            table.IndexFunction(s);
        }
    }
}
