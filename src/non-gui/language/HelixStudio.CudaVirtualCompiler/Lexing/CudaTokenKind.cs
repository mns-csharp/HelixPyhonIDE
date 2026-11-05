namespace HelixPythonNamespace.CudaVirtualCompiler.Lexing
{
    /// <summary>
    /// All token kinds produced by the CUDA C++ lexer. The keyword block is kept
    /// contiguous between KwVoid..KwLaunchBounds so IsKeyword() is a range check
    /// (same technique as the Python lexer's token_kind.h). CUDA execution-space and
    /// storage qualifiers are lexed as distinct keyword kinds so the parser binds
    /// them cleanly; CUDA built-in variables (threadIdx, blockIdx, ...) are lexed as
    /// plain Identifier and classified in the symbol table, which keeps shadow-lint
    /// working. Port of the C++ helix::cuda_vcompiler::CudaTokenKind
    /// (cuda_vcompiler/lexer/token_kind.h).
    /// </summary>
    public enum CudaTokenKind
    {
        Unknown = 0,
        EndOfFile,

        // Trivia.
        Whitespace,
        NewLine,
        LineContinuation,
        CommentLine,
        CommentBlock,
        PreprocessorDirective,

        // Primary.
        Identifier,
        IntLiteral,
        FloatLiteral,
        CharLiteral,
        StringLiteral,

        // ---- Keyword block (contiguous) ----
        // Built-in / fundamental types.
        KwVoid,
        KwBool,
        KwChar,
        KwShort,
        KwInt,
        KwLong,
        KwFloat,
        KwDouble,
        KwSigned,
        KwUnsigned,
        KwAuto,
        // Type-forming / cv.
        KwConst,
        KwConstexpr,
        KwVolatile,
        KwStruct,
        KwClass,
        KwUnion,
        KwEnum,
        KwTypedef,
        KwUsing,
        KwNamespace,
        KwTemplate,
        KwTypename,
        KwOperator,
        KwThis,
        KwNullptr,
        KwTrue,
        KwFalse,
        KwSizeof,
        KwNew,
        KwDelete,
        KwStatic,
        KwExtern,
        KwInline,
        KwVirtual,
        KwFriend,
        KwPublic,
        KwPrivate,
        KwProtected,
        KwReturn,
        // Control flow.
        KwIf,
        KwElse,
        KwFor,
        KwWhile,
        KwDo,
        KwSwitch,
        KwCase,
        KwDefault,
        KwBreak,
        KwContinue,
        KwGoto,
        // Casts.
        KwStaticCast,
        KwReinterpretCast,
        KwConstCast,
        KwDynamicCast,
        // ---- CUDA execution-space / storage qualifiers ----
        KwGlobal,        // __global__
        KwDevice,        // __device__
        KwHost,          // __host__
        KwConstant,      // __constant__
        KwShared,        // __shared__
        KwManaged,       // __managed__
        KwRestrict,      // __restrict__
        KwForceInline,   // __forceinline__
        KwNoInline,      // __noinline__
        KwLaunchBounds,  // __launch_bounds__
        // ---- end keyword block ----

        // Punctuation.
        LeftParen,
        RightParen,
        LeftBrace,
        RightBrace,
        LeftBracket,
        RightBracket,
        Semicolon,
        Comma,
        Colon,
        Scope,         // ::
        Dot,
        Arrow,         // ->
        Ellipsis,      // ...
        Question,

        // Operators.
        Assign,
        Plus,
        Minus,
        Star,
        Slash,
        Percent,
        Amp,
        Pipe,
        Caret,
        Tilde,
        Bang,
        Less,
        Greater,
        LessEqual,
        GreaterEqual,
        EqualEqual,
        NotEqual,
        AmpAmp,
        PipePipe,
        LeftShift,     // <<
        RightShift,    // >>
        PlusPlus,
        MinusMinus,
        PlusAssign,
        MinusAssign,
        StarAssign,
        SlashAssign,
        PercentAssign,
        AmpAssign,
        PipeAssign,
        CaretAssign,
        LeftShiftAssign,
        RightShiftAssign,

        // CUDA kernel-launch chevrons.
        TripleLess,    // <<<
        TripleGreater, // >>>
        Spaceship,     // <=>  (C++20 three-way comparison)
    }

    /// <summary>
    /// Classification helpers over <see cref="CudaTokenKind"/>. Port of the free
    /// functions IsKeyword/IsCudaQualifier/IsTrivia in the C++ token_kind.h.
    /// </summary>
    public static class CudaTokenKinds
    {
        /// <summary>True if <paramref name="k"/> is in the contiguous keyword block (including CUDA qualifiers).</summary>
        public static bool IsKeyword(CudaTokenKind k)
        {
            return k >= CudaTokenKind.KwVoid && k <= CudaTokenKind.KwLaunchBounds;
        }

        /// <summary>True if <paramref name="k"/> is one of the CUDA execution-space / storage qualifier keywords.</summary>
        public static bool IsCudaQualifier(CudaTokenKind k)
        {
            return k >= CudaTokenKind.KwGlobal && k <= CudaTokenKind.KwLaunchBounds;
        }

        public static bool IsTrivia(CudaTokenKind k)
        {
            return k == CudaTokenKind.Whitespace ||
                   k == CudaTokenKind.NewLine ||
                   k == CudaTokenKind.LineContinuation ||
                   k == CudaTokenKind.CommentLine ||
                   k == CudaTokenKind.CommentBlock ||
                   k == CudaTokenKind.PreprocessorDirective;
        }
    }
}
