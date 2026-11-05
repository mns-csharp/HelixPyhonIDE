namespace HelixPythonNamespace.CudaVirtualCompiler
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using HelixPythonNamespace.CudaVirtualCompiler.Analyze;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
    using HelixPythonNamespace.CudaVirtualCompiler.Parsing;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.CudaVirtualCompiler.Validate;
    using HelixPythonNamespace.Python.Diagnostics;

    /// <summary>
    /// Public facade for the self-contained CUDA C++ virtual compiler. Mirrors
    /// vcompiler::VirtualCompiler. The pipeline is: lex+parse -&gt; AST -&gt; structural
    /// validate -&gt; build symbol table + resolve names -&gt; the four CUDA analyses
    /// (launch config, qualifier/sync, memory space, lint). Every pass appends to
    /// one diagnostics list. No kernel is ever executed; all findings are static.
    /// Port of the C++ helix::cuda_vcompiler::CudaVirtualCompiler
    /// (cuda_vcompiler/cuda_virtual_compiler.{h,cpp}).
    /// </summary>
    public static class CudaVirtualCompiler
    {
        public static CudaCompileResult CompileSource(string source, string filePath = null, CudaCompileOptions options = null)
        {
            if (source == null) source = string.Empty;
            if (filePath == null) filePath = string.Empty;
            if (options == null) options = CudaCompileOptions.Default();

            List<CudaCompileDiagnostic> diagnostics = new List<CudaCompileDiagnostic>();
            if (options.EnableLanguageStandard)
                diagnostics.AddRange(LanguageStandardAnalyzer.Analyze(
                    source, filePath, options.LanguageStandard));

            // Phase 0: collect object-like #define macros up front so the lexer can
            // expand them (e.g. `#define N 5` used in `<<<1, N>>>` and kernel bodies).
            // The same instance is reused by the launch-config analysis in phase 4.
            Preprocessor pp = new Preprocessor();
            pp.Scan(source);

            // Phase 1-2: lex + parse. CU1xxx lexical and CU2xxx parse diagnostics surface here.
            TranslationUnit unit = null;
            if (options.EnableLexParse)
            {
                CudaLexer lexer = new CudaLexer(source, filePath, pp.Defines);
                List<CudaToken> tokens = lexer.Tokenize();
                AddParseDiags(diagnostics, filePath, lexer.Diagnostics);

                CudaParser parser = new CudaParser(tokens, filePath);
                unit = parser.Parse();
                AddParseDiags(diagnostics, filePath, parser.Diagnostics);
            }

            // Dialect checks that need syntax context (lambda, auto) — the token
            // pass above ran before parsing and cannot see them.
            if (options.EnableLanguageStandard && unit != null)
                diagnostics.AddRange(LanguageStandardAnalyzer.AnalyzeAst(
                    unit, filePath, options.LanguageStandard));

            // Phase 3: structural validation (CU2xxx/CU5030) + symbol table.
            if (options.EnableAstValidator && unit != null)
            {
                AstValidator validator = new AstValidator(filePath);
                validator.Validate(unit);
                AddParseDiags(diagnostics, filePath, validator.Diagnostics);
            }

            // Phase 3.5: follow the entry file's quoted #include graph so cross-file
            // symbols exist at analysis time (opt-in — see the option's doc).
            IncludeResolution includes = null;
            if (options.EnableIncludeResolution && !string.IsNullOrEmpty(filePath) && unit != null)
            {
                try { includes = IncludeResolver.Resolve(filePath, source, options.IncludeDirectories); }
                catch { includes = null; }
            }

            SymbolTable table = null;
            if (options.EnableSymtablePass && unit != null)
            {
                SymbolTableBuilder builder = new SymbolTableBuilder(filePath);
                table = builder.Build(includes != null ? includes.Units : null, unit);
                AddParseDiags(diagnostics, filePath, builder.Diagnostics);
            }

            // Phase 4: the four CUDA analyses, each behind its flag.
            if (unit != null && table != null)
            {
                if (options.EnableLaunchConfig)
                {
                    LaunchAnalyzer a = new LaunchAnalyzer(filePath);
                    a.Analyze(unit, table, pp);
                    AddParseDiags(diagnostics, filePath, a.Diagnostics);
                }
                if (options.EnableQualifierSync)
                {
                    QualifierSyncAnalyzer a = new QualifierSyncAnalyzer(filePath);
                    a.Analyze(unit, table);
                    AddParseDiags(diagnostics, filePath, a.Diagnostics);
                }
                if (options.EnableMemorySpace)
                {
                    MemorySpaceAnalyzer a = new MemorySpaceAnalyzer(filePath);
                    a.Analyze(unit, table);
                    AddParseDiags(diagnostics, filePath, a.Diagnostics);
                }
                if (options.EnableLint)
                {
                    LintAnalyzer a = new LintAnalyzer(filePath);
                    a.Analyze(unit, table);
                    AddParseDiags(diagnostics, filePath, a.Diagnostics);
                }
                // CU2101 undefined-name check. Historically disabled for any TU with a
                // system include ("absent std headers make absence unprovable") — which
                // was EVERY real file, so junk identifiers never surfaced in the editor.
                // With include resolution the project's own headers are merged in; when
                // every quoted include resolved, the pass runs in STRICT mode (Error, the
                // way nvcc treats an undefined identifier), leaning on the analyzer's
                // skip-rules + external-name catalog for the still-unexpanded std headers.
                bool strictNames = includes != null && includes.AllQuotedResolved;
                bool namesProvable = strictNames ||
                    source.IndexOf("#include <", System.StringComparison.Ordinal) < 0;
                if (options.EnableNameResolution && namesProvable)
                {
                    NameResolutionAnalyzer a = new NameResolutionAnalyzer(filePath, strictNames);
                    a.Analyze(unit, table);
                    AddParseDiags(diagnostics, filePath, a.Diagnostics);
                }
            }

            return new CudaCompileResult(unit, table, diagnostics);
        }

        public static CudaCompileResult CompileFile(string filePath, CudaCompileOptions options = null)
        {
            if (!File.Exists(filePath))
            {
                throw new IOException("Cannot open file: '" + filePath + "'");
            }
            string source = File.ReadAllText(filePath);
            return CompileSource(source, filePath, options);
        }

        /// <summary>
        /// Two-pass workspace compile. <paramref name="cancel"/> is polled at each
        /// per-file iteration boundary (GUI Stop button); it replaces the C++
        /// std::atomic&lt;bool&gt;* cancelFlag.
        /// </summary>
        public static List<CompileFileResult> CompileSolution(IReadOnlyList<WorkspaceFile> files,
            CudaCompileOptions options = null, CancellationToken cancel = default(CancellationToken))
        {
            List<CompileFileResult> results = new List<CompileFileResult>();
            if (files == null) return results;
            for (int i = 0; i < files.Count; ++i)
            {
                if (cancel.IsCancellationRequested) break;
                WorkspaceFile wf = files[i];
                if (wf == null || string.IsNullOrEmpty(wf.FilePath)) continue;
                if (!File.Exists(wf.FilePath)) continue;
                string source = File.ReadAllText(wf.FilePath);
                string name = string.IsNullOrEmpty(wf.TranslationUnitName) ? wf.FilePath : wf.TranslationUnitName;
                CudaCompileResult cr = CompileSource(source, wf.FilePath, options);
                results.Add(new CompileFileResult(wf.FilePath, name, cr));
            }
            return results;
        }

        private static void AddParseDiags(List<CudaCompileDiagnostic> outList, string filePath, IReadOnlyList<ParseDiagnostic> diags)
        {
            for (int i = 0; i < diags.Count; ++i)
            {
                ParseDiagnostic d = diags[i];
                outList.Add(new CudaCompileDiagnostic(filePath, d.Code, d.Severity, d.Span, d.Message));
            }
        }
    }
}
