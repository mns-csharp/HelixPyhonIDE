namespace HelixPythonNamespace.CudaVirtualCompiler.Lexing
{
    using System.Collections.Generic;

    /// <summary>
    /// Keyword and CUDA built-in classification for the lexer. Port of the C++
    /// helix::cuda_vcompiler keyword table (cuda_vcompiler/lexer/keywords.{h,cpp}).
    /// </summary>
    public static class Keywords
    {
        private static readonly Dictionary<string, CudaTokenKind> Table = BuildTable();

        private static readonly HashSet<string> BuiltinVars = new HashSet<string>
        {
            "threadIdx", "blockIdx", "blockDim", "gridDim", "warpSize",
        };

        /// <summary>
        /// Map an identifier lexeme to its keyword kind, or Identifier if it is not a
        /// reserved word. Covers C++ keywords and the CUDA execution-space / storage
        /// qualifiers (__global__ etc.). CUDA built-in variables (threadIdx, ...) are
        /// deliberately NOT keywords here — they resolve as ordinary identifiers and
        /// are seeded as builtin symbols in the symbol table.
        /// </summary>
        public static CudaTokenKind KeywordKind(string lexeme)
        {
            CudaTokenKind kind;
            if (lexeme != null && Table.TryGetValue(lexeme, out kind)) return kind;
            return CudaTokenKind.Identifier;
        }

        /// <summary>
        /// True if <paramref name="lexeme"/> is a known CUDA built-in variable
        /// (threadIdx, blockIdx, blockDim, gridDim, warpSize). Used by the
        /// symbol-table builder and the editor's highlighter to classify these
        /// identifiers.
        /// </summary>
        public static bool IsCudaBuiltinVar(string lexeme)
        {
            return lexeme != null && BuiltinVars.Contains(lexeme);
        }

        private static Dictionary<string, CudaTokenKind> BuildTable()
        {
            return new Dictionary<string, CudaTokenKind>
            {
                // Fundamental types.
                { "void", CudaTokenKind.KwVoid },
                { "bool", CudaTokenKind.KwBool },
                { "char", CudaTokenKind.KwChar },
                { "short", CudaTokenKind.KwShort },
                { "int", CudaTokenKind.KwInt },
                { "long", CudaTokenKind.KwLong },
                { "float", CudaTokenKind.KwFloat },
                { "double", CudaTokenKind.KwDouble },
                { "signed", CudaTokenKind.KwSigned },
                { "unsigned", CudaTokenKind.KwUnsigned },
                { "auto", CudaTokenKind.KwAuto },
                // Type-forming / cv / decls.
                { "const", CudaTokenKind.KwConst },
                { "constexpr", CudaTokenKind.KwConstexpr },
                { "volatile", CudaTokenKind.KwVolatile },
                { "struct", CudaTokenKind.KwStruct },
                { "class", CudaTokenKind.KwClass },
                { "union", CudaTokenKind.KwUnion },
                { "enum", CudaTokenKind.KwEnum },
                { "typedef", CudaTokenKind.KwTypedef },
                { "using", CudaTokenKind.KwUsing },
                { "namespace", CudaTokenKind.KwNamespace },
                { "template", CudaTokenKind.KwTemplate },
                { "typename", CudaTokenKind.KwTypename },
                { "operator", CudaTokenKind.KwOperator },
                { "this", CudaTokenKind.KwThis },
                { "nullptr", CudaTokenKind.KwNullptr },
                { "true", CudaTokenKind.KwTrue },
                { "false", CudaTokenKind.KwFalse },
                { "sizeof", CudaTokenKind.KwSizeof },
                { "new", CudaTokenKind.KwNew },
                { "delete", CudaTokenKind.KwDelete },
                { "static", CudaTokenKind.KwStatic },
                { "extern", CudaTokenKind.KwExtern },
                { "inline", CudaTokenKind.KwInline },
                { "virtual", CudaTokenKind.KwVirtual },
                { "friend", CudaTokenKind.KwFriend },
                { "public", CudaTokenKind.KwPublic },
                { "private", CudaTokenKind.KwPrivate },
                { "protected", CudaTokenKind.KwProtected },
                { "return", CudaTokenKind.KwReturn },
                // Control flow.
                { "if", CudaTokenKind.KwIf },
                { "else", CudaTokenKind.KwElse },
                { "for", CudaTokenKind.KwFor },
                { "while", CudaTokenKind.KwWhile },
                { "do", CudaTokenKind.KwDo },
                { "switch", CudaTokenKind.KwSwitch },
                { "case", CudaTokenKind.KwCase },
                { "default", CudaTokenKind.KwDefault },
                { "break", CudaTokenKind.KwBreak },
                { "continue", CudaTokenKind.KwContinue },
                { "goto", CudaTokenKind.KwGoto },
                // Casts.
                { "static_cast", CudaTokenKind.KwStaticCast },
                { "reinterpret_cast", CudaTokenKind.KwReinterpretCast },
                { "const_cast", CudaTokenKind.KwConstCast },
                { "dynamic_cast", CudaTokenKind.KwDynamicCast },
                // CUDA execution-space / storage qualifiers. Both the __x__ spelling
                // and (where the toolkit also accepts it) the bare form are mapped.
                { "__global__", CudaTokenKind.KwGlobal },
                { "__device__", CudaTokenKind.KwDevice },
                { "__host__", CudaTokenKind.KwHost },
                { "__constant__", CudaTokenKind.KwConstant },
                { "__shared__", CudaTokenKind.KwShared },
                { "__managed__", CudaTokenKind.KwManaged },
                { "__restrict__", CudaTokenKind.KwRestrict },
                { "__restrict", CudaTokenKind.KwRestrict },
                { "__forceinline__", CudaTokenKind.KwForceInline },
                { "__noinline__", CudaTokenKind.KwNoInline },
                { "__launch_bounds__", CudaTokenKind.KwLaunchBounds },
            };
        }
    }
}
