namespace HelixPythonNamespace.CudaVirtualCompiler
{
    /// <summary>
    /// Diagnostic code constants. CU#### scheme, grouped by producing pass so the
    /// editor can route by prefix the way it routes Python's PY#### codes:
    ///   CU1xxx  lexer / preprocessor
    ///   CU2xxx  parser + structural AST validation + name resolution
    ///   CU3xxx  kernel launch-configuration analysis
    ///   CU4xxx  pointer memory-space analysis
    ///   CU5xxx  execution-space qualifier + synchronization rules
    ///   CU6xxx  lint (unchecked errors, dead code, naming)
    ///   CU7xxx  selected C++/CUDA language-standard availability
    /// Port of the C++ helix::cuda_vcompiler::CudaDiagnosticCodes namespace
    /// (cuda_vcompiler/cuda_compile_diagnostic.h).
    /// </summary>
    public static class CudaDiagnosticCodes
    {
        // Lexer / preprocessor (CU1xxx)
        public const string LexUnexpectedCharacter = "CU1001";
        public const string LexUnterminatedString = "CU1002";
        public const string LexUnterminatedComment = "CU1003";
        public const string LexInvalidNumber = "CU1004";

        // Parser / AST-validation / name resolution (CU2xxx)
        public const string ParseUnexpectedToken = "CU2001";
        public const string ParseUnexpectedEof = "CU2002";
        public const string ParseExpectedToken = "CU2003";
        public const string ParseInvalidSyntax = "CU2099";
        public const string SemUndefinedName = "CU2101";
        public const string SemDuplicateDefinition = "CU2102";
        public const string SemAmbiguousOverload = "CU2103";
        public const string SemBreakOutsideLoop = "CU2110";
        public const string SemContinueOutsideLoop = "CU2111";
        public const string SemReturnOutsideFunc = "CU2112";
        public const string SemKernelNonVoidReturn = "CU2120";

        // Launch configuration (CU3xxx)
        public const string LaunchOfNonKernel = "CU3001";
        public const string LaunchFromDeviceNoCdp = "CU3002";
        public const string LaunchBlockTooLarge = "CU3010";   // x*y*z > 1024
        public const string LaunchBlockDimTooLarge = "CU3011"; // per-dim cap
        public const string LaunchGridDimTooLarge = "CU3012";
        public const string LaunchDimIsZero = "CU3013";
        public const string LaunchSharedMemTooLarge = "CU3020"; // > 48 KB static
        public const string LaunchSharedMemUnused = "CU3021";
        public const string LaunchSharedMemMissing = "CU3022";
        public const string LaunchArgCountMismatch = "CU3030";
        public const string LaunchBadStreamArg = "CU3031";

        // Memory-space analysis (CU4xxx)
        public const string MemDeviceDerefOnHost = "CU4001";
        public const string MemHostDerefOnDevice = "CU4002";
        public const string MemMemcpyDirectionWrong = "CU4010";
        public const string MemLeak = "CU4020";
        public const string MemFreeOfHostPointer = "CU4021";
        public const string MemDoubleFree = "CU4022";
        public const string MemUseAfterFree = "CU4023";
        public const string MemHostPtrToKernel = "CU4030";
        public const string MemSpaceConflict = "CU4040";

        // Qualifier / synchronization rules (CU5xxx)
        public const string QualDeviceCallsHost = "CU5001";
        public const string QualKernelCalledOnDevice = "CU5002";
        public const string QualBuiltinVarInHost = "CU5003";
        public const string QualSharedInHost = "CU5010";
        public const string QualSharedBadSize = "CU5011";
        public const string QualDeviceQualOnLocal = "CU5012";
        public const string QualConstantWritten = "CU5013";
        public const string QualSyncInDivergentBranch = "CU5020";
        public const string QualSyncInDivergentLoop = "CU5021";
        public const string QualKernelBadReturnType = "CU5030";
        public const string QualKernelBadParam = "CU5031";

        // Lint (CU6xxx)
        public const string LintUncheckedCudaError = "CU6001";
        public const string LintMissingPostLaunchCheck = "CU6002";
        public const string LintNamingConvention = "CU6010";
        public const string LintUnreachableCode = "CU6020";
        public const string LintUnusedLocal = "CU6021";
        public const string LintKernelNeverLaunched = "CU6022";
        // Static array bounds (CU6030-CU6039): compile-time detectable OOB accesses.
        public const string LintArrayOob = "CU6030";    // index >= declared size
        public const string LintArrayNegIdx = "CU6031"; // index < 0

        // Selected language dialect (CU7xxx)
        public const string StandardFeatureUnavailable = "CU7001";
    }
}
