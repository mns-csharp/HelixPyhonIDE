namespace HelixPythonNamespace.CudaVirtualCompiler
{
    /// <summary>
    /// Per-call feature gates. Each pass can be disabled independently so the four
    /// CUDA analyses can ship and be tuned incrementally. Mirrors
    /// vcompiler::CompileOptions. Port of the C++
    /// helix::cuda_vcompiler::CudaCompileOptions (cuda_vcompiler/cuda_compile_options.h).
    /// </summary>
    public sealed class CudaCompileOptions
    {
        public bool EnableLexParse = true;       // CU1xxx + CU2xxx parse
        public bool EnableAstValidator = true;   // CU2xxx structural
        public bool EnableSymtablePass = true;   // CU2xxx resolution
        public bool EnableQualifierSync = true;  // CU5xxx
        public bool EnableLaunchConfig = true;   // CU3xxx
        public bool EnableMemorySpace = true;    // CU4xxx
        public bool EnableLint = true;           // CU6xxx
        public bool EnableLanguageStandard = true; // CU7xxx selected-dialect feature checks
        public bool EnableNameResolution = true;   // CU2101 undefined-name warnings
        public string LanguageStandard = "20";

        /// <summary>
        /// Follow quoted <c>#include "..."</c> directives from the entry file (workspace-layout
        /// aware search, transitive, memoized) and merge the headers' declarations into the
        /// symbol table before analysis. When every quoted include resolves, the undefined-name
        /// pass runs in STRICT mode: CU2101 becomes an Error — matching nvcc, which rejects
        /// undefined identifiers — instead of an unmarkable warning. Off by default so
        /// single-snippet compiles (tests, emulator) keep their self-contained behavior.
        /// </summary>
        public bool EnableIncludeResolution = false;

        /// <summary>Extra include roots to search (e.g. CMake target include dirs); the
        /// resolver also derives roots from the entry file's ancestor directories.</summary>
        public System.Collections.Generic.List<string> IncludeDirectories = null;

        public static CudaCompileOptions Default()
        {
            return new CudaCompileOptions();
        }
    }
}
