namespace HelixPythonNamespace.CudaEmulator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using HelixPythonNamespace.CudaVirtualCompiler;
    using HelixPythonNamespace.CudaVirtualCompiler.Analyze;
    using HelixPythonNamespace.Python.Text;

    public sealed class CudaCppTranslationUnitInfo
    {
        public string FilePath;
        public int StartLine;
        public int LineCount;
        public int SourceLength;
    }

    /// <summary>Maps a linked AST line back to the physical source line that emitted it.</summary>
    public sealed class CudaCppSourceLineInfo
    {
        public int LinkedLine;
        public string FilePath;
        public int SourceLine;
    }

    /// <summary>
    /// Result of the managed CUDA/C++ compiler + CPU CUDA runtime toolchain.
    /// This is a real compile/run result: diagnostics come from the CUDA/C++ compiler
    /// pipeline and stdout/exit/kernel statistics come from executing the AST.
    /// </summary>
    public sealed class CudaCppEmulatorToolchainResult
    {
        public bool CompileSucceeded;
        public bool RunAttempted;
        public bool RunSucceeded;
        public bool SafetyGatePassed;
        public int ExitCode;
        public int SourceFileCount;
        public readonly List<string> IncludedFiles = new List<string>();
        public long CompileElapsedMs;
        public long RunElapsedMs;
        public string Output;
        public string FailureReason;
        public CudaCompileResult Compilation;
        public readonly List<CudaCppTranslationUnitInfo> TranslationUnits =
            new List<CudaCppTranslationUnitInfo>();
        public readonly List<CudaCppSourceLineInfo> SourceLineMap =
            new List<CudaCppSourceLineInfo>();
        public int MaximumIncludeDepthReached;
        public int ExpandedHeaderCharacters;
        public int ConditionalDirectiveCount;
        public int SkippedConditionalIncludes;
        public int MacroIncludeCount;
        public int UnknownConditionalExpressionCount;
        public int ExpandedMacroTokenCount;
        public readonly List<KernelTimingSample> Kernels = new List<KernelTimingSample>();
    }

    /// <summary>
    /// First-class compiler/runtime facade for the Helix CUDA/C++ Emulator.
    /// Each source retains its translation-unit identity and diagnostics are mapped back
    /// to the originating file. The executable program image is linked into one AST for
    /// deterministic interpretation; binary object emission is intentionally not claimed.
    /// </summary>
    public static class CudaCppEmulatorToolchain
    {
        public const string DisplayName = "Helix CUDA/C++ Emulator";
        public const string Version = "1.2";
        public const int MaxIncludeDepth = 128;
        public const int MaxIncludedFiles = 4096;
        public const int MaxExpandedHeaderCharacters = 16 * 1024 * 1024;

        public static CudaCppEmulatorToolchainResult CompileAndRun(
            IEnumerable<string> sourceFiles, bool runProgram, string entryPoint = "main",
            bool requireCheckedCudaCalls = true, string cxxStandard = "20",
            string cudaStandard = "20", IEnumerable<string> includeDirectories = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new CudaCppEmulatorToolchainResult();
            var source = new StringBuilder();
            var standardDiagnostics = new List<CudaCompileDiagnostic>();
            const string linkedPath = "helix-emulator-linked-program.cu";
            int currentLine = 1;

            if (sourceFiles != null)
                foreach (string path in sourceFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.FailureReason = "emulator compilation cancelled";
                        result.ExitCode = 1;
                        return result;
                    }
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    string extension = Path.GetExtension(path);
                    if (!IsCompilableExtension(extension)) continue;
                    string fullPath = Path.GetFullPath(path);
                    string text = File.ReadAllText(path);
                    string selectedStandard = string.Equals(extension, ".cu",
                        StringComparison.OrdinalIgnoreCase) ? cudaStandard : cxxStandard;
                    standardDiagnostics.AddRange(LanguageStandardAnalyzer.Analyze(
                        text, fullPath, selectedStandard));
                    string includeError;
                    int expandedHeaderCharacters = result.ExpandedHeaderCharacters;
                    var activeIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    activeIncludes.Add(fullPath);
                    var macros = new Dictionary<string, string>(StringComparer.Ordinal);
                    SeedPredefinedMacros(macros, extension, selectedStandard);
                    int conditionalDirectiveCount = result.ConditionalDirectiveCount;
                    int skippedConditionalIncludes = result.SkippedConditionalIncludes;
                    int macroIncludeCount = result.MacroIncludeCount;
                    int unknownConditionalExpressionCount = result.UnknownConditionalExpressionCount;
                    int expandedMacroTokenCount = result.ExpandedMacroTokenCount;
                    text = ExpandIncludes(text, Path.GetDirectoryName(fullPath),
                        activeIncludes, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        result.IncludedFiles, includeDirectories, cancellationToken, 0,
                        ref expandedHeaderCharacters, ref result.MaximumIncludeDepthReached,
                        macros, ref conditionalDirectiveCount, ref skippedConditionalIncludes,
                        ref macroIncludeCount, ref unknownConditionalExpressionCount,
                        ref expandedMacroTokenCount,
                        out includeError);
                    result.ExpandedHeaderCharacters = expandedHeaderCharacters;
                    result.ConditionalDirectiveCount = conditionalDirectiveCount;
                    result.SkippedConditionalIncludes = skippedConditionalIncludes;
                    result.MacroIncludeCount = macroIncludeCount;
                    result.UnknownConditionalExpressionCount = unknownConditionalExpressionCount;
                    result.ExpandedMacroTokenCount = expandedMacroTokenCount;
                    if (!string.IsNullOrEmpty(includeError))
                    {
                        result.FailureReason = includeError;
                        result.ExitCode = 1;
                        return result;
                    }
                    source.AppendLine("// ---- translation unit: " + fullPath + " ----");
                    currentLine++;
                    BuildSourceLineMap(text, fullPath, currentLine + 1, result.SourceLineMap);
                    var unit = new CudaCppTranslationUnitInfo
                    {
                        FilePath = fullPath,
                        StartLine = currentLine,
                        LineCount = CountLines(text),
                        SourceLength = text.Length
                    };
                    result.TranslationUnits.Add(unit);
                    source.Append(text);
                    if (text.Length == 0 || text[text.Length - 1] != '\n') source.AppendLine();
                    currentLine += unit.LineCount;
                    result.SourceFileCount++;
                }

            if (result.SourceFileCount == 0)
            {
                result.FailureReason = "No C/C++/CUDA translation-unit sources were found.";
                result.ExitCode = 1;
                return result;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                result.FailureReason = "emulator compilation cancelled";
                result.ExitCode = 1;
                return result;
            }

            var compileClock = Stopwatch.StartNew();
            var linkedOptions = CudaCompileOptions.Default();
            linkedOptions.EnableLanguageStandard = false;
            CudaCompileResult linked =
                global::HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(
                    source.ToString(), linkedPath, linkedOptions);
            result.Compilation = RemapDiagnostics(linked, result.TranslationUnits,
                result.SourceLineMap);
            if (standardDiagnostics.Count != 0)
            {
                var allDiagnostics = new List<CudaCompileDiagnostic>();
                foreach (CudaCompileDiagnostic diagnostic in result.Compilation.Diagnostics)
                    allDiagnostics.Add(diagnostic);
                allDiagnostics.AddRange(standardDiagnostics);
                result.Compilation = new CudaCompileResult(result.Compilation.Unit,
                    result.Compilation.SymbolTable, allDiagnostics);
            }
            compileClock.Stop();
            result.CompileElapsedMs = compileClock.ElapsedMilliseconds;
            result.CompileSucceeded = result.Compilation != null &&
                result.Compilation.Unit != null && result.Compilation.SymbolTable != null &&
                !result.Compilation.HasErrors();
            if (!result.CompileSucceeded)
            {
                result.ExitCode = 1;
                var details = new StringBuilder("Managed CUDA/C++ compilation failed.");
                int shown = 0;
                foreach (CudaCompileDiagnostic diagnostic in result.Compilation.Diagnostics)
                    if (shown++ < 5) details.Append(" ").Append(diagnostic.ToString());
                result.FailureReason = details.ToString();
                return result;
            }
            if (!runProgram)
            {
                result.SafetyGatePassed = true;
                result.ExitCode = 0;
                return result;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                result.FailureReason = "emulator execution cancelled";
                result.ExitCode = 1;
                return result;
            }

            result.SafetyGatePassed = !requireCheckedCudaCalls ||
                !HasUncheckedCudaOperation(result.Compilation);
            if (!result.SafetyGatePassed)
            {
                result.ExitCode = 1;
                result.FailureReason =
                    "CUDA safety gate refused execution: check every cudaError_t result " +
                    "and check each kernel launch with cudaGetLastError or synchronization.";
                return result;
            }

            result.RunAttempted = true;
            var sink = new StringOutputSink();
            var emulator = new CudaEmulator(result.Compilation, sink);
            var runClock = Stopwatch.StartNew();
            EmulationResult run = emulator.RunProgram(entryPoint);
            runClock.Stop();
            result.RunElapsedMs = runClock.ElapsedMilliseconds;
            result.Output = sink.Text;
            result.RunSucceeded = run.Ok;
            result.ExitCode = run.Ok ? run.ExitCode : 1;
            if (!run.Ok && run.Error != null) result.FailureReason = run.Error.ErrorMessage;
            foreach (KernelTimingSample sample in emulator.Stats.Kernels)
                result.Kernels.Add(sample);
            return result;
        }

        private static bool HasUncheckedCudaOperation(CudaCompileResult compilation)
        {
            if (compilation == null) return false;
            foreach (CudaCompileDiagnostic diagnostic in compilation.Diagnostics)
                if (diagnostic.Code == CudaDiagnosticCodes.LintUncheckedCudaError ||
                    diagnostic.Code == CudaDiagnosticCodes.LintMissingPostLaunchCheck)
                    return true;
            return false;
        }

        private static CudaCompileResult RemapDiagnostics(CudaCompileResult linked,
            List<CudaCppTranslationUnitInfo> units, List<CudaCppSourceLineInfo> sourceLineMap)
        {
            if (linked == null) return null;
            var mapped = new List<CudaCompileDiagnostic>();
            foreach (CudaCompileDiagnostic diagnostic in linked.Diagnostics)
            {
                CudaCppSourceLineInfo mappedStart = FindSourceLine(sourceLineMap,
                    diagnostic.Span.Start.Line);
                CudaCppSourceLineInfo mappedEnd = FindSourceLine(sourceLineMap,
                    diagnostic.Span.End.Line);
                if (mappedStart != null)
                {
                    int mappedEndLine = mappedEnd != null &&
                        string.Equals(mappedEnd.FilePath, mappedStart.FilePath,
                            StringComparison.OrdinalIgnoreCase)
                        ? mappedEnd.SourceLine : mappedStart.SourceLine;
                    var mappedSpan = new SourceSpan(
                        new SourcePosition(0, mappedStart.SourceLine,
                            Math.Max(1, diagnostic.Span.Start.Column)),
                        new SourcePosition(Math.Max(0, diagnostic.Span.Length),
                            Math.Max(1, mappedEndLine),
                            Math.Max(1, diagnostic.Span.End.Column)));
                    mapped.Add(new CudaCompileDiagnostic(mappedStart.FilePath, diagnostic.Code,
                        diagnostic.Severity, mappedSpan, diagnostic.Message));
                    continue;
                }
                CudaCppTranslationUnitInfo owner = null;
                for (int i = 0; i < units.Count; i++)
                {
                    CudaCppTranslationUnitInfo candidate = units[i];
                    if (diagnostic.Span.Start.Line >= candidate.StartLine &&
                        diagnostic.Span.Start.Line < candidate.StartLine + candidate.LineCount)
                    {
                        owner = candidate;
                        break;
                    }
                }
                if (owner == null)
                {
                    mapped.Add(diagnostic);
                    continue;
                }

                int startLine = diagnostic.Span.Start.Line - owner.StartLine + 1;
                int endLine = diagnostic.Span.End.Line - owner.StartLine + 1;
                var start = new SourcePosition(0, Math.Max(1, startLine),
                    Math.Max(1, diagnostic.Span.Start.Column));
                var end = new SourcePosition(Math.Max(0, diagnostic.Span.Length),
                    Math.Max(1, endLine), Math.Max(1, diagnostic.Span.End.Column));
                mapped.Add(new CudaCompileDiagnostic(owner.FilePath, diagnostic.Code,
                    diagnostic.Severity, new SourceSpan(start, end), diagnostic.Message));
            }
            return new CudaCompileResult(linked.Unit, linked.SymbolTable, mapped);
        }

        private static CudaCppSourceLineInfo FindSourceLine(
            List<CudaCppSourceLineInfo> sourceLineMap, int linkedLine)
        {
            if (sourceLineMap == null) return null;
            for (int i = 0; i < sourceLineMap.Count; i++)
                if (sourceLineMap[i].LinkedLine == linkedLine) return sourceLineMap[i];
            return null;
        }

        private static void BuildSourceLineMap(string expanded, string rootPath, int linkedStart,
            List<CudaCppSourceLineInfo> map)
        {
            var frames = new List<SourceMapFrame>();
            frames.Add(new SourceMapFrame(rootPath));
            string[] lines = (expanded ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            int lineCount = lines.Length;
            if (lineCount > 0 && lines[lineCount - 1].Length == 0) lineCount--;
            int linkedLine = linkedStart;
            for (int i = 0; i < lineCount; i++, linkedLine++)
            {
                string line = lines[i];
                SourceMapFrame frame = frames[frames.Count - 1];
                string header = ParseMarkerPath(line, "// ---- included header: ", " ----");
                string endHeader = ParseMarkerPath(line, "// ---- end included header: ", " ----");
                if (header != null)
                {
                    map.Add(new CudaCppSourceLineInfo { LinkedLine = linkedLine,
                        FilePath = header, SourceLine = 1 });
                    frames.Add(new SourceMapFrame(header));
                    continue;
                }
                if (endHeader != null)
                {
                    map.Add(new CudaCppSourceLineInfo { LinkedLine = linkedLine,
                        FilePath = frame.Path, SourceLine = Math.Max(1, frame.NextLine - 1) });
                    if (frames.Count > 1) frames.RemoveAt(frames.Count - 1);
                    continue;
                }
                if (line.TrimStart().StartsWith("// ---- include-site: ",
                    StringComparison.Ordinal))
                {
                    map.Add(new CudaCppSourceLineInfo { LinkedLine = linkedLine,
                        FilePath = frame.Path, SourceLine = frame.NextLine });
                    frame.NextLine++;
                    continue;
                }
                map.Add(new CudaCppSourceLineInfo { LinkedLine = linkedLine,
                    FilePath = frame.Path, SourceLine = frame.NextLine });
                frame.NextLine++;
            }
        }

        private sealed class SourceMapFrame
        {
            public SourceMapFrame(string path) { Path = path; NextLine = 1; }
            public string Path;
            public int NextLine;
        }

        private static string ParseMarkerPath(string line, string prefix, string suffix)
        {
            string trimmed = (line ?? string.Empty).TrimStart();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal) ||
                !trimmed.EndsWith(suffix, StringComparison.Ordinal)) return null;
            return trimmed.Substring(prefix.Length,
                trimmed.Length - prefix.Length - suffix.Length);
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            int lines = 0;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            if (text[text.Length - 1] != '\n') lines++;
            return lines;
        }

        private static string ExpandIncludes(string source, string directory,
            HashSet<string> active, HashSet<string> pragmaOnceExpanded,
            List<string> includedFiles, IEnumerable<string> includeDirectories,
            CancellationToken cancellationToken, int depth, ref int expandedHeaderCharacters,
            ref int maximumIncludeDepth, Dictionary<string, string> macros,
            ref int conditionalDirectiveCount, ref int skippedConditionalIncludes,
            ref int macroIncludeCount, ref int unknownConditionalExpressionCount,
            ref int expandedMacroTokenCount,
            out string error)
        {
            error = null;
            if (cancellationToken.IsCancellationRequested)
            {
                error = "emulator preprocessing cancelled";
                return string.Empty;
            }
            if (depth > MaxIncludeDepth)
            {
                error = "include depth limit exceeded (maximum " + MaxIncludeDepth + ")";
                return string.Empty;
            }
            if (depth > maximumIncludeDepth) maximumIncludeDepth = depth;
            var expanded = new StringBuilder();
            string[] lines = (source ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var conditions = new List<ConditionalFrame>();
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if ((lineIndex & 255) == 0 && cancellationToken.IsCancellationRequested)
                {
                    error = "emulator preprocessing cancelled";
                    return string.Empty;
                }
                string line = lines[lineIndex];
                string trimmed = line.Trim();
                if (trimmed.StartsWith("#if", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#ifdef", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#ifndef", StringComparison.Ordinal))
                {
                    bool parentActive = IsConditionalActive(conditions);
                    bool known;
                    bool branch = EvaluateConditional(trimmed, macros, out known);
                    if (!known) unknownConditionalExpressionCount++;
                    conditions.Add(new ConditionalFrame(parentActive, branch && known));
                    conditionalDirectiveCount++;
                    expanded.AppendLine(line);
                    continue;
                }
                if (trimmed.StartsWith("#elif", StringComparison.Ordinal))
                {
                    if (conditions.Count > 0)
                    {
                        ConditionalFrame frame = conditions[conditions.Count - 1];
                        bool known;
                        bool branch = EvaluateConditional(trimmed, macros, out known);
                        if (!known) unknownConditionalExpressionCount++;
                        frame.Active = frame.ParentActive && !frame.BranchTaken && branch && known;
                        if (frame.Active) frame.BranchTaken = true;
                    }
                    conditionalDirectiveCount++;
                    expanded.AppendLine(line);
                    continue;
                }
                if (trimmed.StartsWith("#else", StringComparison.Ordinal))
                {
                    if (conditions.Count > 0)
                    {
                        ConditionalFrame frame = conditions[conditions.Count - 1];
                        frame.Active = frame.ParentActive && !frame.BranchTaken;
                        frame.BranchTaken = true;
                    }
                    conditionalDirectiveCount++;
                    expanded.AppendLine(line);
                    continue;
                }
                if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
                {
                    if (conditions.Count > 0) conditions.RemoveAt(conditions.Count - 1);
                    conditionalDirectiveCount++;
                    expanded.AppendLine(line);
                    continue;
                }
                bool activeBranch = IsConditionalActive(conditions);
                if (trimmed.StartsWith("#define", StringComparison.Ordinal) && activeBranch)
                {
                    RecordMacro(trimmed, macros);
                    expanded.AppendLine(line);
                    continue;
                }
                if (trimmed.StartsWith("#undef", StringComparison.Ordinal) && activeBranch)
                {
                    string undef = trimmed.Substring("#undef".Length).Trim();
                    if (undef.Length > 0) macros.Remove(FirstToken(undef));
                    expanded.AppendLine(line);
                    continue;
                }
                if (trimmed.StartsWith("#include", StringComparison.Ordinal))
                {
                    if (!activeBranch)
                    {
                        skippedConditionalIncludes++;
                        expanded.AppendLine(line);
                        continue;
                    }
                    bool angled;
                    bool macroInclude;
                    string requested = ParseIncludeRequest(trimmed, macros, out angled,
                        out macroInclude);
                    if (macroInclude) macroIncludeCount++;
                    if (!string.IsNullOrEmpty(requested))
                    {
                        string header = ResolveHeader(requested, directory, includeDirectories, angled);
                        if (!File.Exists(header))
                        {
                            if (angled) { expanded.AppendLine(line); continue; }
                            error = "local header not found: " + requested +
                                " (searched " + DescribeSearch(directory, includeDirectories) + ")";
                            return string.Empty;
                        }
                        if (active.Contains(header))
                        {
                            // A pragma-once header may legally be reached again through a
                            // nested include graph. Without pragma once, this is a real cycle.
                            if (pragmaOnceExpanded.Contains(header)) continue;
                            error = "cyclic local header include detected: " + header;
                            return string.Empty;
                        }
                        active.Add(header);
                        bool newHeader = !ContainsPath(includedFiles, header);
                        if (newHeader && includedFiles.Count >= MaxIncludedFiles)
                        {
                            active.Remove(header);
                            error = "included-file limit exceeded (maximum " +
                                MaxIncludedFiles + ")";
                            return string.Empty;
                        }
                        if (newHeader)
                            includedFiles.Add(header);
                        string headerText = File.ReadAllText(header);
                        expandedHeaderCharacters += headerText.Length;
                        if (expandedHeaderCharacters > MaxExpandedHeaderCharacters)
                        {
                            active.Remove(header);
                            error = "expanded-header size limit exceeded (maximum " +
                                MaxExpandedHeaderCharacters + " characters)";
                            return string.Empty;
                        }
                        bool pragmaOnce = ContainsPragmaOnce(headerText);
                        if (pragmaOnce && !pragmaOnceExpanded.Add(header))
                        {
                            active.Remove(header);
                            continue;
                        }
                        expanded.AppendLine("// ---- include-site: " + header + " ----");
                        expanded.AppendLine("// ---- included header: " + header + " ----");
                        string nested = ExpandIncludes(headerText, Path.GetDirectoryName(header),
                            active, pragmaOnceExpanded, includedFiles, includeDirectories,
                            cancellationToken, depth + 1, ref expandedHeaderCharacters,
                            ref maximumIncludeDepth, macros, ref conditionalDirectiveCount,
                            ref skippedConditionalIncludes, ref macroIncludeCount,
                            ref unknownConditionalExpressionCount, ref expandedMacroTokenCount,
                            out error);
                        active.Remove(header);
                        if (!string.IsNullOrEmpty(error)) return string.Empty;
                        expanded.Append(nested);
                        expanded.AppendLine("// ---- end included header: " + header + " ----");
                        continue;
                    }
                }
                if (!activeBranch)
                {
                    // §10 correctness: a line inside an inactive #if/#elif/#else arm
                    // must never reach the parser — both arms of a platform guard
                    // used to be compiled together, producing phantom redefinitions
                    // and parse errors. A blank line keeps the SourceLineMap
                    // arithmetic (and therefore diagnostic remapping) exact.
                    expanded.AppendLine();
                    continue;
                }
                expanded.AppendLine(ExpandObjectMacros(line, macros, ref expandedMacroTokenCount));
            }
            return expanded.ToString();
        }

        /// <summary>
        /// Seeds the translation unit's predefined macros the way a real compiler
        /// driver does: __cplusplus per the selected dialect, the CUDA compiler
        /// identity macros for .cu units, and the host platform macro. Without
        /// these, every `#ifdef __CUDACC__` / `#if __cplusplus >= …` portability
        /// guard in real code takes the wrong branch.
        /// </summary>
        internal static void SeedPredefinedMacros(Dictionary<string, string> macros,
            string extension, string languageStandard)
        {
            macros["__cplusplus"] = CplusplusValueFor(languageStandard);
            macros["_WIN32"] = "1";   // the IDE's emulator host is Windows
            if (string.Equals(extension, ".cu", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cuh", StringComparison.OrdinalIgnoreCase))
            {
                macros["__CUDACC__"] = "1";
                macros["__NVCC__"] = "1";
                // The emulator runs a single merged host+device pass; expose a
                // representative device architecture so __CUDA_ARCH__ guards take
                // their device branch (documented single-pass semantics).
                macros["__CUDA_ARCH__"] = "700";
            }
        }

        // The standard __cplusplus literal for each supported dialect token. The
        // toolchain passes bare numbers ("98" … "26", default "20"); the CMake and
        // UI layers use "c++NN" — accept both. Unknown values fall back to C++20,
        // the toolchain's default dialect.
        internal static string CplusplusValueFor(string languageStandard)
        {
            string s = (languageStandard ?? string.Empty).Trim().ToLowerInvariant();
            if (s.StartsWith("c++", StringComparison.Ordinal)) s = s.Substring(3);
            switch (s)
            {
                case "98":
                case "03": return "199711L";
                case "11": return "201103L";
                case "14": return "201402L";
                case "17": return "201703L";
                case "20": return "202002L";
                case "23": return "202302L";
                case "26": return "202400L";
                default: return "202002L";
            }
        }

        private static string ExpandObjectMacros(string line, Dictionary<string, string> macros,
            ref int expandedMacroTokenCount)
        {
            string current = line ?? string.Empty;
            // A small fixed point allows #define A B / #define B 2 without risking an
            // unbounded recursive macro cycle.
            for (int pass = 0; pass < 8; pass++)
            {
                string next = ExpandObjectMacrosOnce(current, macros, ref expandedMacroTokenCount);
                if (string.Equals(next, current, StringComparison.Ordinal)) return current;
                current = next;
            }
            return current;
        }

        private static string ExpandObjectMacrosOnce(string line, Dictionary<string, string> macros,
            ref int expandedMacroTokenCount)
        {
            var output = new StringBuilder();
            bool inString = false;
            bool inCharacter = false;
            bool escaped = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (!inString && !inCharacter && ch == '/' && i + 1 < line.Length &&
                    line[i + 1] == '/')
                {
                    output.Append(line.Substring(i));
                    break;
                }
                if (ch == '"' && !inCharacter && !escaped) inString = !inString;
                else if (ch == '\'' && !inString && !escaped) inCharacter = !inCharacter;
                if (!inString && !inCharacter && (char.IsLetter(ch) || ch == '_'))
                {
                    int start = i;
                    i++;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    string token = line.Substring(start, i - start);
                    string replacement;
                    if (macros.TryGetValue(token, out replacement) && IsSafeObjectMacro(replacement))
                    {
                        output.Append(replacement);
                        expandedMacroTokenCount++;
                    }
                    else output.Append(token);
                    i--;
                }
                else output.Append(ch);
                escaped = ch == '\\' && !escaped;
                if (ch != '\\') escaped = false;
            }
            return output.ToString();
        }

        private static bool IsSafeObjectMacro(string replacement)
        {
            if (string.IsNullOrWhiteSpace(replacement)) return false;
            string value = replacement.Trim();
            return value.IndexOf('"') < 0 && value.IndexOf('\'') < 0 &&
                value.IndexOf('<') < 0 && value.IndexOf('>') < 0 &&
                value.IndexOf('#') < 0 && value.IndexOf('\\') < 0;
        }

        private sealed class ConditionalFrame
        {
            public ConditionalFrame(bool parentActive, bool active)
            {
                ParentActive = parentActive;
                Active = parentActive && active;
                BranchTaken = Active;
            }
            public bool ParentActive;
            public bool Active;
            public bool BranchTaken;
        }

        private static bool IsConditionalActive(List<ConditionalFrame> conditions)
        {
            for (int i = 0; i < conditions.Count; i++)
                if (!conditions[i].Active) return false;
            return true;
        }

        private static bool EvaluateConditional(string directive,
            Dictionary<string, string> macros, out bool known)
        {
            known = true;
            string expression;
            if (directive.StartsWith("#ifdef", StringComparison.Ordinal))
                return macros.ContainsKey(FirstToken(directive.Substring(6).Trim()));
            if (directive.StartsWith("#ifndef", StringComparison.Ordinal))
                return !macros.ContainsKey(FirstToken(directive.Substring(7).Trim()));
            if (directive.StartsWith("#elif", StringComparison.Ordinal))
                expression = directive.Substring(5).Trim();
            else
                expression = directive.Substring(3).Trim();
            var parser = new ConditionalExpressionParser(expression, macros);
            int value;
            if (!parser.TryParse(out value))
            {
                known = false;
                return true;
            }
            return value != 0;
        }

        /// <summary>A deliberately small, deterministic evaluator for common integer #if forms.</summary>
        private sealed class ConditionalExpressionParser
        {
            private readonly string _text;
            private readonly Dictionary<string, string> _macros;
            private int _position;
            private bool _unknown;

            public ConditionalExpressionParser(string text, Dictionary<string, string> macros)
            {
                _text = text ?? string.Empty;
                _macros = macros;
            }

            public bool TryParse(out int value)
            {
                value = ParseConditional();
                SkipWhitespace();
                return !_unknown && _position == _text.Length;
            }

            private int ParseConditional()
            {
                int condition = ParseOr();
                if (!Consume("?")) return condition;
                int whenTrue = ParseConditional();
                if (!Consume(":")) { _unknown = true; return 0; }
                int whenFalse = ParseConditional();
                return condition != 0 ? whenTrue : whenFalse;
            }

            private int ParseOr()
            {
                int left = ParseAnd();
                while (Consume("||")) left = (left != 0 || ParseAnd() != 0) ? 1 : 0;
                return left;
            }

            private int ParseAnd()
            {
                int left = ParseBitwiseOr();
                while (Consume("&&")) left = (left != 0 && ParseBitwiseOr() != 0) ? 1 : 0;
                return left;
            }

            private int ParseBitwiseOr()
            {
                int left = ParseBitwiseXor();
                while (ConsumeSingle('|')) left |= ParseBitwiseXor();
                return left;
            }

            private int ParseBitwiseXor()
            {
                int left = ParseBitwiseAnd();
                while (ConsumeSingle('^')) left ^= ParseBitwiseAnd();
                return left;
            }

            private int ParseBitwiseAnd()
            {
                int left = ParseEquality();
                while (ConsumeSingle('&')) left &= ParseEquality();
                return left;
            }

            private int ParseEquality()
            {
                int left = ParseRelational();
                while (true)
                {
                    if (Consume("==")) left = left == ParseRelational() ? 1 : 0;
                    else if (Consume("!=")) left = left != ParseRelational() ? 1 : 0;
                    else return left;
                }
            }

            private int ParseRelational()
            {
                int left = ParseShift();
                while (true)
                {
                    if (Consume("<=")) left = left <= ParseShift() ? 1 : 0;
                    else if (Consume(">=")) left = left >= ParseShift() ? 1 : 0;
                    else if (Consume("<")) left = left < ParseShift() ? 1 : 0;
                    else if (Consume(">")) left = left > ParseShift() ? 1 : 0;
                    else return left;
                }
            }

            private int ParseShift()
            {
                int left = ParseAdditive();
                while (true)
                {
                    if (Consume("<<")) left <<= ParseAdditive();
                    else if (Consume(">>")) left >>= ParseAdditive();
                    else return left;
                }
            }

            private int ParseAdditive()
            {
                int left = ParseMultiplicative();
                while (true)
                {
                    if (Consume("+")) left += ParseMultiplicative();
                    else if (Consume("-")) left -= ParseMultiplicative();
                    else return left;
                }
            }

            private int ParseMultiplicative()
            {
                int left = ParseUnary();
                while (true)
                {
                    if (Consume("*")) left *= ParseUnary();
                    else if (Consume("/"))
                    {
                        int divisor = ParseUnary();
                        if (divisor == 0) { _unknown = true; return 0; }
                        left /= divisor;
                    }
                    else if (Consume("%"))
                    {
                        int divisor = ParseUnary();
                        if (divisor == 0) { _unknown = true; return 0; }
                        left %= divisor;
                    }
                    else return left;
                }
            }

            private int ParseUnary()
            {
                if (Consume("!")) return ParseUnary() == 0 ? 1 : 0;
                if (Consume("~")) return ~ParseUnary();
                if (Consume("+")) return ParseUnary();
                if (Consume("-")) return -ParseUnary();
                if (Consume("("))
                {
                    int nested = ParseConditional();
                    if (!Consume(")")) _unknown = true;
                    return nested;
                }
                string token = ReadToken();
                if (token.Length == 0) { _unknown = true; return 0; }
                if (string.Equals(token, "defined", StringComparison.Ordinal))
                {
                    bool parenthesized = Consume("(");
                    string name = ReadToken();
                    if (parenthesized && !Consume(")")) _unknown = true;
                    return _macros.ContainsKey(name) ? 1 : 0;
                }
                int number;
                if (TryParseIntegerLiteral(token, out number)) return number;
                if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(token, "false", StringComparison.OrdinalIgnoreCase)) return 0;
                string replacement;
                if (_macros.TryGetValue(token, out replacement) &&
                    TryParseIntegerLiteral(replacement.Trim(), out number)) return number;
                _unknown = true;
                return 0;
            }

            private static bool TryParseIntegerLiteral(string token, out int value)
            {
                value = 0;
                if (string.IsNullOrEmpty(token)) return false;
                string normalized = token;
                while (normalized.Length > 0)
                {
                    char suffix = normalized[normalized.Length - 1];
                    if (suffix == 'u' || suffix == 'U' || suffix == 'l' || suffix == 'L')
                        normalized = normalized.Substring(0, normalized.Length - 1);
                    else break;
                }
                if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(normalized.Substring(2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out value);
                if (normalized.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                {
                    if (normalized.Length == 2) return false;
                    int parsed = 0;
                    for (int i = 2; i < normalized.Length; i++)
                    {
                        if (normalized[i] != '0' && normalized[i] != '1') return false;
                        parsed = (parsed << 1) | (normalized[i] - '0');
                    }
                    value = parsed;
                    return true;
                }
                if (normalized.Length > 1 && normalized[0] == '0')
                {
                    int parsed = 0;
                    for (int i = 1; i < normalized.Length; i++)
                    {
                        if (normalized[i] < '0' || normalized[i] > '7') return false;
                        parsed = (parsed * 8) + (normalized[i] - '0');
                    }
                    value = parsed;
                    return true;
                }
                return int.TryParse(normalized, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value);
            }

            private string ReadToken()
            {
                SkipWhitespace();
                int start = _position;
                while (_position < _text.Length &&
                    (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_')) _position++;
                return _text.Substring(start, _position - start);
            }

            private bool Consume(string token)
            {
                SkipWhitespace();
                if (_position + token.Length > _text.Length ||
                    !string.Equals(_text.Substring(_position, token.Length), token,
                        StringComparison.Ordinal)) return false;
                _position += token.Length;
                return true;
            }

            private bool ConsumeSingle(char token)
            {
                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] != token) return false;
                if (_position + 1 < _text.Length && _text[_position + 1] == token) return false;
                _position++;
                return true;
            }

            private void SkipWhitespace()
            {
                while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
            }
        }

        private static void RecordMacro(string directive, Dictionary<string, string> macros)
        {
            string rest = directive.Substring("#define".Length).Trim();
            if (rest.Length == 0) return;
            string name = FirstToken(rest);
            if (name.Length == 0) return;
            if (rest.Length > name.Length && rest[name.Length] == '(') return;
            string value = rest.Substring(name.Length).Trim();
            macros[name] = value.Length == 0 ? "1" : value;
        }

        private static string FirstToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            int end = 0;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) &&
                text[end] != '(' && text[end] != ')') end++;
            return text.Substring(0, end);
        }

        private static string ParseIncludeRequest(string line,
            Dictionary<string, string> macros, out bool angled, out bool macroInclude)
        {
            angled = false;
            macroInclude = false;
            int quote = line.IndexOf('"');
            int less = line.IndexOf('<');
            int first = quote >= 0 ? quote : less;
            if (first < 0)
            {
                string token = FirstToken(line.Substring("#include".Length).Trim());
                string replacement;
                if (token.Length == 0 || !macros.TryGetValue(token, out replacement)) return null;
                bool replacementMacro;
                string request = ParseIncludeRequest("#include " + replacement, macros,
                    out angled, out replacementMacro);
                macroInclude = true;
                return request;
            }
            char opening = line[first];
            char closing = opening == '<' ? '>' : '"';
            int last = line.IndexOf(closing, first + 1);
            if (last <= first) return null;
            angled = opening == '<';
            return line.Substring(first + 1, last - first - 1).Trim();
        }

        private static string ResolveHeader(string requested, string directory,
            IEnumerable<string> includeDirectories, bool angled)
        {
            if (!angled)
            {
                string local = Path.GetFullPath(Path.Combine(directory ?? string.Empty, requested));
                if (File.Exists(local)) return local;
            }
            if (includeDirectories != null)
                foreach (string includeDirectory in includeDirectories)
                {
                    if (string.IsNullOrWhiteSpace(includeDirectory)) continue;
                    string candidate = Path.GetFullPath(Path.Combine(includeDirectory, requested));
                    if (File.Exists(candidate)) return candidate;
                }
            return Path.GetFullPath(Path.Combine(directory ?? string.Empty, requested));
        }

        private static string DescribeSearch(string directory, IEnumerable<string> includeDirectories)
        {
            var paths = new List<string>();
            if (!string.IsNullOrWhiteSpace(directory)) paths.Add(directory);
            if (includeDirectories != null)
                foreach (string includeDirectory in includeDirectories)
                    if (!string.IsNullOrWhiteSpace(includeDirectory) && !paths.Contains(includeDirectory))
                        paths.Add(includeDirectory);
            return string.Join("; ", paths.ToArray());
        }

        private static bool ContainsPragmaOnce(string source)
        {
            string[] lines = (source ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
                if (line.Trim().StartsWith("#pragma once", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool ContainsPath(List<string> paths, string candidate)
        {
            foreach (string path in paths)
                if (string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsCompilableExtension(string extension)
        {
            return string.Equals(extension, ".c", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cpp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cxx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cu", StringComparison.OrdinalIgnoreCase);
        }
    }
}
