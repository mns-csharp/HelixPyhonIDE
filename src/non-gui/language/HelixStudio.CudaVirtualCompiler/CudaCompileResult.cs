namespace HelixPythonNamespace.CudaVirtualCompiler
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.Python.Diagnostics;

    /// <summary>
    /// Output of CudaVirtualCompiler.CompileSource. Owns the AST translation unit
    /// and (optionally) the SymbolTable produced during the symtable pass. The
    /// SymbolTable's AST node back-pointers stay valid for the lifetime of this
    /// result — never reseat the AST without dropping the SymbolTable. Port of the
    /// C++ helix::cuda_vcompiler::CudaCompileResult (cuda_vcompiler/cuda_compile_result.{h,cpp}).
    /// </summary>
    public sealed class CudaCompileResult
    {
        private readonly TranslationUnit unit_;
        private readonly Symtable.SymbolTable symbolTable_;
        private readonly List<CudaCompileDiagnostic> diagnostics_;
        private readonly DateTime compiledAt_;

        public CudaCompileResult(List<CudaCompileDiagnostic> diagnostics)
        {
            unit_ = null;
            symbolTable_ = null;
            diagnostics_ = diagnostics ?? new List<CudaCompileDiagnostic>();
            compiledAt_ = DateTime.UtcNow;
        }

        public CudaCompileResult(TranslationUnit unit, Symtable.SymbolTable symbolTable,
                                 List<CudaCompileDiagnostic> diagnostics)
        {
            unit_ = unit;
            symbolTable_ = symbolTable;
            diagnostics_ = diagnostics ?? new List<CudaCompileDiagnostic>();
            compiledAt_ = DateTime.UtcNow;
        }

        public TranslationUnit Unit { get { return unit_; } }
        public Symtable.SymbolTable SymbolTable { get { return symbolTable_; } }
        public IReadOnlyList<CudaCompileDiagnostic> Diagnostics { get { return diagnostics_; } }
        public DateTime CompiledAt { get { return compiledAt_; } }

        public bool HasErrors()
        {
            for (int i = 0; i < diagnostics_.Count; ++i)
            {
                if (diagnostics_[i].Severity >= ParseSeverity.Error) return true;
            }
            return false;
        }

        public int ErrorCount()
        {
            int n = 0;
            for (int i = 0; i < diagnostics_.Count; ++i)
            {
                if (diagnostics_[i].Severity >= ParseSeverity.Error) ++n;
            }
            return n;
        }

        public int WarningCount()
        {
            int n = 0;
            for (int i = 0; i < diagnostics_.Count; ++i)
            {
                if (diagnostics_[i].Severity == ParseSeverity.Warning) ++n;
            }
            return n;
        }
    }
}
