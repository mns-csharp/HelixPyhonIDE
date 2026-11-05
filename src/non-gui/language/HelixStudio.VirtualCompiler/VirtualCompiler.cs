using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Lexing;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.VirtualCompiler.Ast;
using HelixPythonNamespace.VirtualCompiler.Compile;
using HelixPythonNamespace.VirtualCompiler.Compliance;
using HelixPythonNamespace.VirtualCompiler.Imports;
using HelixPythonNamespace.VirtualCompiler.Symtable;
using HelixPythonNamespace.VirtualCompiler.Typing;
using HelixPythonNamespace.VirtualCompiler.Typing.Stubs;
using HelixPythonNamespace.VirtualCompiler.Validate;
using PyParser = HelixPythonNamespace.Python.Python;
using PyParseResult = HelixPythonNamespace.Python.ParseResult;

namespace HelixPythonNamespace.VirtualCompiler
{
    /// <summary>
    /// Public facade for the virtual compiler. Phase 1.0 runs: Parse → CstToAst → AstValidator.
    /// Later phases (symtable, compile-time checks, type inference) plug into this pipeline
    /// gated by <see cref="CompileOptions"/>.
    /// </summary>
    public static class VirtualCompiler
    {
        public static CompileResult CompileSource(string source, string filePath = null, CompileOptions options = null)
        {
            return CompileSource(source, filePath, options, null);
        }

        public static CompileResult CompileSource(string source, string filePath, CompileOptions options,
            WorkspaceIndex workspace,
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            return CompileSource(source, filePath, options, workspace, null, cancel);
        }

        // The resolver is threaded in rather than constructed per file so a whole-solution compile
        // parses each package's __init__.py once instead of once per importer.
        private static CompileResult CompileSource(string source, string filePath, CompileOptions options,
            WorkspaceIndex workspace, ModuleResolver resolver,
            System.Threading.CancellationToken cancel)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            options = options ?? CompileOptions.Default;
            filePath = filePath ?? string.Empty;

            List<CompileDiagnostic> diagnostics = new List<CompileDiagnostic>();

            // Cooperative cancellation is checked at each stage boundary below so that a single
            // heavy file stops promptly on Cancel rather than running the whole pipeline to the end.
            PyParseResult parseResult = PyParser.ParseModule(source, filePath, cancel);

            if (options.EnableSyntaxPass)
            {
                foreach (ParseDiagnostic d in parseResult.Diagnostics)
                {
                    diagnostics.Add(CompileDiagnostic.FromParse(filePath, d));
                }
            }

            Module astModule = null;
            if (parseResult.Module != null)
            {
                cancel.ThrowIfCancellationRequested();
                astModule = CstToAst.Lower(parseResult.Module);

                if (options.EnableAstValidator)
                {
                    cancel.ThrowIfCancellationRequested();
                    AstValidator validator = new AstValidator(filePath);
                    validator.ValidateModule(astModule);
                    foreach (CompileDiagnostic d in validator.Diagnostics)
                    {
                        diagnostics.Add(d);
                    }
                }

                SymbolTable table = null;
                if (options.EnableSymtablePass)
                {
                    cancel.ThrowIfCancellationRequested();
                    SymbolTableBuilder builder = new SymbolTableBuilder(filePath);
                    table = builder.Build(astModule);
                    foreach (CompileDiagnostic d in builder.Diagnostics) diagnostics.Add(d);

                    NameResolver nameResolver = new NameResolver(filePath, table);
                    nameResolver.Resolve();
                    foreach (CompileDiagnostic d in nameResolver.Diagnostics) diagnostics.Add(d);

                    UnusedImportChecker unused = new UnusedImportChecker(filePath, table, astModule);
                    unused.Check();
                    foreach (CompileDiagnostic d in unused.Diagnostics) diagnostics.Add(d);
                }

                if (options.EnableCompileValidator && table != null)
                {
                    cancel.ThrowIfCancellationRequested();
                    CompileValidator cv = new CompileValidator(filePath, table);
                    cv.Validate();
                    foreach (CompileDiagnostic d in cv.Diagnostics) diagnostics.Add(d);
                }

                if (options.EnableTypeChecker && table != null)
                {
                    cancel.ThrowIfCancellationRequested();
                    TypeInferencer inf = new TypeInferencer(filePath, table, StubLoader.BuiltinIndex, workspace);
                    inf.CheckModule(astModule);
                    foreach (CompileDiagnostic d in inf.Diagnostics) diagnostics.Add(d);
                }

                // Solution policy: files outside the workspace, and procedural code, are hard
                // blockers. Reported as errors so the Run/Debug preflight refuses to launch.
                if (options.EnableCompliancePass && options.Policy != null)
                {
                    cancel.ThrowIfCancellationRequested();
                    ComplianceChecker compliance = new ComplianceChecker(filePath, options.Policy);
                    compliance.Check(astModule);
                    foreach (CompileDiagnostic d in compliance.Diagnostics) diagnostics.Add(d);
                }

                // Import resolution runs against the real sys.path the code will execute under,
                // so a packaging fault (a namespace package shadowing the real one, a project
                // absent from the path, a name that is never re-exported) surfaces here instead
                // of as an ImportError the first time the user hits Run.
                if (options.EnableImportResolution)
                {
                    ModuleResolver effective = resolver
                        ?? (options.SearchPath != null ? new ModuleResolver(options.SearchPath) : null);
                    if (effective != null)
                    {
                        cancel.ThrowIfCancellationRequested();
                        ImportChecker importChecker = new ImportChecker(filePath, effective, cancel);
                        importChecker.Check(astModule, source);
                        foreach (CompileDiagnostic d in importChecker.Diagnostics) diagnostics.Add(d);
                    }
                }
            }

            // Honor `# type: ignore` and `# noqa` comments: drop every diagnostic on a line the
            // author explicitly silenced. These are standard type-checker/linter conventions and common in
            // real code for runtime-injected names (IPython's get_ipython) and platform shims. We
            // suppress the whole line regardless of any [error-code] bracket, because our PY#### codes
            // do not map onto mypy's error-code names — honoring the author's intent to silence the
            // line is the correct, professional behavior.
            if (diagnostics.Count > 0)
            {
                HashSet<int> ignoreLines = CollectSuppressedLines(source, cancel);
                if (ignoreLines.Count > 0)
                    diagnostics.RemoveAll(d => ignoreLines.Contains(d.Span.Start.Line)
                                               && !IsUnsuppressablePolicy(d.Code));
            }

            return new CompileResult(astModule, diagnostics);
        }

        /// <summary>
        /// Solution-policy diagnostics (PY8xxx) survive `# noqa` / `# type: ignore`.
        ///
        /// Those comments express an author's intent to silence a warning about their own code.
        /// The policy rules are not advisory: they gate Run and Debug, and a one-line comment must
        /// not be able to smuggle unviewable or procedural code past a hard blocker. Every other
        /// diagnostic remains suppressible exactly as before.
        /// </summary>
        private static bool IsUnsuppressablePolicy(string code)
        {
            return !string.IsNullOrEmpty(code) && code.StartsWith("PY8", StringComparison.Ordinal);
        }

        // Lines carrying a `# type: ignore[...]` comment. Re-lexes the source so the match is on real
        // comment tokens, never text that merely looks like the marker inside a string literal.
        private static HashSet<int> CollectSuppressedLines(string source,
            System.Threading.CancellationToken cancel)
        {
            HashSet<int> lines = new HashSet<int>();
            IReadOnlyList<Token> tokens;
            try { tokens = new PythonLexer(source).Tokenize(cancel); }
            catch (OperationCanceledException) { throw; }
            catch { return lines; }
            foreach (Token t in tokens)
            {
                if (t.Kind == TokenKind.Comment &&
                    (IsTypeIgnoreComment(t.Text) || IsNoqaComment(t.Text)))
                    lines.Add(t.Span.Start.Line);
            }
            return lines;
        }

        // Matches a comment of the form `# type: ignore` optionally followed by `[codes]` or trailing
        // text (`# type: ignore  # why`). Rejects near-misses like `# type: ignoreme` and `## type:`.
        private static bool IsTypeIgnoreComment(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '#') return false;
            int i = 1;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            if (!Consume(text, ref i, "type:")) return false;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            if (!Consume(text, ref i, "ignore")) return false;
            if (i < text.Length)
            {
                char c = text[i];
                if (c != ' ' && c != '\t' && c != '[' && c != '#') return false;   // e.g. "ignoreme"
            }
            return true;
        }

        // Matches `# noqa`, optionally followed by a Ruff/Flake8 code list (`# noqa: F401`)
        // or trailing rationale. The lexer restriction in CollectSuppressedLines ensures that
        // text inside a string literal cannot suppress a real diagnostic.
        private static bool IsNoqaComment(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '#') return false;
            int i = 1;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            if (!Consume(text, ref i, "noqa")) return false;
            if (i < text.Length)
            {
                char c = text[i];
                if (c != ' ' && c != '\t' && c != ':' && c != '#') return false;
            }
            return true;
        }

        private static bool Consume(string text, ref int i, string word)
        {
            if (i + word.Length > text.Length) return false;
            if (string.CompareOrdinal(text, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        public static CompileResult CompileFile(string filePath, CompileOptions options = null)
        {
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            string text = File.ReadAllText(filePath);
            return CompileSource(text, filePath, options);
        }

        /// <summary>
        /// Two-pass compile across every file in a workspace. Pass 1 parses each file, lowers to AST,
        /// and registers every top-level <c>def</c>/<c>class</c> in a <see cref="WorkspaceIndex"/>
        /// keyed by <c>{ModuleFqn}.{Name}</c>. Pass 2 runs the full per-file compile pipeline with
        /// the index injected so that <c>from mod import foo</c> binds <c>foo</c> to the real
        /// <see cref="CallableRef"/> / <see cref="ClassRef"/> and cross-module calls get full
        /// argument-type checking.
        /// </summary>
        /// <param name="files">File path → module FQN pairs. ModuleFqn is the dotted import path
        /// (e.g. <c>lib_utils.geometry</c>, not including the leaf function/class name).</param>
        public static IReadOnlyList<CompileFileResult> CompileSolution(
            IEnumerable<WorkspaceFile> files, CompileOptions options = null,
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            options = options ?? CompileOptions.Default;

            // Materialize files + parse + lower once. Share AST across both passes.
            List<PreparedFile> prepared = new List<PreparedFile>();
            WorkspaceIndex index = new WorkspaceIndex();
            // One resolver for the whole batch: its module and export caches are what keep
            // import resolution from re-reading the same __init__.py for every importer.
            ModuleResolver resolver = options.EnableImportResolution && options.SearchPath != null
                ? new ModuleResolver(options.SearchPath)
                : null;

            foreach (WorkspaceFile wf in files)
            {
                cancel.ThrowIfCancellationRequested();
                if (wf.FilePath == null) continue;
                string source;
                try { source = File.ReadAllText(wf.FilePath); }
                catch { continue; }

                PyParseResult pr = PyParser.ParseModule(source, wf.FilePath, cancel);
                Module astModule = pr.Module != null ? CstToAst.Lower(pr.Module) : null;
                prepared.Add(new PreparedFile(wf.FilePath, wf.ModuleFqn ?? string.Empty, source, pr, astModule));

                if (astModule != null && !string.IsNullOrEmpty(wf.ModuleFqn))
                {
                    RegisterTopLevelDecls(index, astModule, wf.ModuleFqn);
                }
            }

            // Pass 2: full compile per file, with the index wired through.
            List<CompileFileResult> results = new List<CompileFileResult>(prepared.Count);
            foreach (PreparedFile pf in prepared)
            {
                cancel.ThrowIfCancellationRequested();
                CompileResult cr = CompileSource(pf.Source, pf.FilePath, options, index, resolver, cancel);
                results.Add(new CompileFileResult(pf.FilePath, pf.ModuleFqn, cr));
            }
            return results;
        }

        private static void RegisterTopLevelDecls(WorkspaceIndex index, Module m, string moduleFqn)
        {
            foreach (Stmt s in m.Body)
            {
                switch (s)
                {
                    case FunctionDef fn:
                        index.Register(moduleFqn + "." + fn.Name,
                            TypeInferencer.BuildDecoratedFunctionBinding(
                                fn.Args, fn.Returns, fn.DecoratorList));
                        break;
                    case AsyncFunctionDef afn:
                        index.Register(moduleFqn + "." + afn.Name,
                            TypeInferencer.BuildDecoratedFunctionBinding(
                                afn.Args, afn.Returns, afn.DecoratorList));
                        break;
                    case ClassDef cls:
                        index.Register(moduleFqn + "." + cls.Name, new ClassRef(cls.Name));
                        index.RegisterClass(moduleFqn + "." + cls.Name, cls);
                        break;
                }
            }
        }

        private sealed class PreparedFile
        {
            public readonly string FilePath;
            public readonly string ModuleFqn;
            public readonly string Source;
            public readonly PyParseResult ParseResult;
            public readonly Module AstModule;

            public PreparedFile(string filePath, string moduleFqn, string source,
                PyParseResult parseResult, Module astModule)
            {
                FilePath = filePath;
                ModuleFqn = moduleFqn;
                Source = source;
                ParseResult = parseResult;
                AstModule = astModule;
            }
        }
    }

    /// <summary>One file in a workspace-wide compile batch.</summary>
    public struct WorkspaceFile
    {
        /// <summary>Absolute path on disk.</summary>
        public string FilePath;
        /// <summary>Dotted import path of the module this file represents
        /// (e.g. <c>pkg.mod</c>). Empty for files outside any project tree.</summary>
        public string ModuleFqn;

        public WorkspaceFile(string filePath, string moduleFqn)
        {
            FilePath = filePath;
            ModuleFqn = moduleFqn;
        }
    }

    /// <summary>Per-file outcome from <see cref="VirtualCompiler.CompileSolution"/>.</summary>
    public sealed class CompileFileResult
    {
        public string FilePath { get; }
        public string ModuleFqn { get; }
        public CompileResult Result { get; }

        public CompileFileResult(string filePath, string moduleFqn, CompileResult result)
        {
            FilePath = filePath;
            ModuleFqn = moduleFqn;
            Result = result;
        }
    }
}
