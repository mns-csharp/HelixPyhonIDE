using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Analysis
{
    /// <summary>
    /// Detects the "CUDA kernel source embedded as a Python string constant, compiled at
    /// runtime via cp.RawKernel/NVRTC" pattern (see
    /// tests/fixtures/python/g1m_ccxtsri_pae_gpu__deformed_codebase/cuda_kernels.py) so the
    /// Import Wizard can offer to extract each kernel into its own .cu file and generate a
    /// companion native CMake "kernel debug" scaffold (SS §linked-cmake-project) — a set of
    /// tiny host executables, one per kernel, that let a real debugger (not the emulator) step
    /// into the actual device code. Heuristic and read-only, like the rest of ImportAnalyzer:
    /// codebases that don't follow this exact shape simply detect zero kernels.
    /// </summary>
    public static class CudaKernelScaffoldAnalyzer
    {
        // A triple-quoted (optionally raw-prefixed) Python string literal, non-greedy so each
        // match is exactly one literal even when a file has several back to back.
        private static readonly Regex StringLiteral = new Regex(
            "r?(\"\"\"|''')(?<body>.*?)\\1",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // Everything on the assignment's LHS/RHS-prefix immediately before a string literal,
        // used to tell "X = r\"\"\"...\"\"\"" (no shared prelude) apart from
        // "X = COMMON + r\"\"\"...\"\"\"" (prepends a shared prelude variable).
        private static readonly Regex AssignmentWithPrelude = new Regex(
            @"(?<prelude>\w+)\s*\+\s*$", RegexOptions.Compiled);

        // "_PROPOSE_SRC = " (optionally followed by "COMMON +") immediately before the literal —
        // captures the LHS variable name so the loader-rewrite pass can retarget it precisely.
        private static readonly Regex AssignmentTarget = new Regex(
            @"(?<var>[A-Za-z_]\w*)\s*=\s*(?:\w+\s*\+\s*)?$", RegexOptions.Compiled);

        private static readonly Regex KernelSignature = new Regex(
            "extern\\s+\"C\"\\s+__global__\\s+void\\s+(?<name>\\w+)\\s*\\(",
            RegexOptions.Compiled);

        /// <summary>
        /// The same signature with <c>extern "C"</c> optional, used only on text that came from a
        /// FILE.
        ///
        /// <para>
        /// The strict form stays in force for inline string literals, where it is the thing that
        /// keeps a docstring discussing CUDA from being mistaken for a kernel. A <c>.cu</c> the
        /// program went and read is not a guess — something already decided that file holds device
        /// code — and requiring <c>extern "C"</c> there would miss most hand-written kernels, since
        /// the linkage specifier is only needed to stop C++ name mangling when the name is looked
        /// up as a string, which is a NVRTC concern rather than a CUDA one.
        /// </para>
        /// </summary>
        private static readonly Regex FileKernelSignature = new Regex(
            "(?:extern\\s+\"C\"\\s+)?__global__\\s+void\\s+(?<name>\\w+)\\s*\\(",
            RegexOptions.Compiled);

        /// <summary>
        /// The Python idioms that read a whole file into a string, with the path captured.
        ///
        /// <para>
        /// Deliberately limited to a LITERAL path. <c>open(os.path.join(base, name))</c> and
        /// f-strings resolve to something only the running program knows, and guessing would
        /// produce a kernel attributed to the wrong file — worse than not finding it, because the
        /// user would then debug the wrong code. Those are the fake-GPU route's business: it sees
        /// the finished source at the moment the program hands it over.
        /// </para>
        /// </summary>
        private static readonly Regex FileLoad = new Regex(
            @"(?<expr>"
            + @"open\s*\(\s*(?<q>['""])(?<path>[^'""]+)\k<q>\s*(?:,[^)]*)?\)\s*\.\s*read\s*\(\s*\)"
            + @"|(?:pathlib\s*\.\s*)?Path\s*\(\s*(?<q2>['""])(?<path2>[^'""]+)\k<q2>\s*\)\s*\.\s*read_text\s*\([^)]*\)"
            + @")",
            RegexOptions.Compiled);

        /// <summary>Extensions a kernel is plausibly read from. <c>.txt</c> is in the wild.</summary>
        private static readonly string[] KernelFileExtensions = { ".cu", ".cuh", ".txt" };

        // "COMMON = NumberSpaceGPU.DEVICE_SRC" — resolves which class attribute holds the
        // shared device-code prelude so it can be tracked down and emitted as common.cuh.
        private static readonly Regex PreludeAlias = new Regex(
            @"^\s*(?<var>\w+)\s*=\s*(?:\w+\.)*(?<attr>\w+)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public static CudaScaffoldAnalysis Analyze(IReadOnlyList<PackageDescriptor> packages)
        {
            var result = new CudaScaffoldAnalysis();
            if (packages == null) return result;

            string preludeVarName = null;
            string preludeAttrName = null;

            var pyFilesByPackage = new List<KeyValuePair<PackageDescriptor, string>>();
            foreach (PackageDescriptor pkg in packages)
            {
                if (pkg == null || string.IsNullOrEmpty(pkg.DirectoryPath) || !Directory.Exists(pkg.DirectoryPath))
                    continue;
                foreach (string file in SafeEnumeratePyFiles(pkg.DirectoryPath))
                    pyFilesByPackage.Add(new KeyValuePair<PackageDescriptor, string>(pkg, file));
            }

            // Pass 1: find kernels, and note the shared-prelude variable name/attribute if any
            // kernel's assignment used one (e.g. "COMMON + r\"\"\"...\"\"\"").
            foreach (KeyValuePair<PackageDescriptor, string> entry in pyFilesByPackage)
            {
                string text;
                try { text = File.ReadAllText(entry.Value); } catch { continue; }

                foreach (Match lit in StringLiteral.Matches(text))
                {
                    string body = lit.Groups["body"].Value;
                    Match sig = KernelSignature.Match(body);
                    if (!sig.Success) continue;

                    // A literal can hold `__device__` helper functions BEFORE the kernel itself
                    // (see bonded_delta_e_kernel.cu's pae_bead/pae_planar_angle/pae_dihedral_term
                    // in the deformed fixture) — those must ride along into the same .cu file, or
                    // the extracted kernel fails to compile on undefined identifiers. Parameter
                    // parsing still needs the kernel's OWN signature in isolation, so both the
                    // full (preamble + kernel) text and the kernel-only text are kept.
                    string kernelOnly = ExtractBalancedFunction(body, sig.Index);
                    if (kernelOnly == null) continue;
                    string preamble = body.Substring(0, sig.Index).Trim();
                    string kernelText = preamble.Length > 0 ? preamble + "\n\n" + kernelOnly : kernelOnly;

                    bool usesPrelude = false;
                    string prefix = text.Substring(0, lit.Index);
                    string trimmedPrefix = prefix.TrimEnd();
                    Match preludeUse = AssignmentWithPrelude.Match(trimmedPrefix);
                    if (preludeUse.Success)
                    {
                        usesPrelude = true;
                        preludeVarName = preludeVarName ?? preludeUse.Groups["prelude"].Value;
                    }
                    Match assignTarget = AssignmentTarget.Match(trimmedPrefix);

                    var kernel = new DetectedCudaKernel
                    {
                        KernelName = sig.Groups["name"].Value,
                        Body = kernelText,
                        SourceFile = entry.Value,
                        PackageDirectory = entry.Key.DirectoryPath,
                        UsesCommonPrelude = usesPrelude,
                        AssignmentVariableName = assignTarget.Success ? assignTarget.Groups["var"].Value : null,
                    };
                    ParseParameters(kernelOnly, kernel.Parameters);
                    kernel.SourceKind = CudaKernelSourceKind.InlineString;
                    result.Kernels.Add(kernel);
                }

                // The other half of the pattern: the kernel is not in the .py at all, it is in a
                // file the .py reads at run time. Same detection, different place to read from.
                CollectFileLoadedKernels(text, entry.Value, entry.Key.DirectoryPath, result);

                if (preludeVarName != null && preludeAttrName == null)
                {
                    // "COMMON = NumberSpaceGPU.DEVICE_SRC" — the attribute name (DEVICE_SRC) is
                    // what we go looking for next, in every file (it's commonly defined in a
                    // different module than the one that aliases it as COMMON).
                    foreach (Match aliasMatch in PreludeAlias.Matches(text))
                    {
                        if (aliasMatch.Groups["var"].Value == preludeVarName)
                        {
                            preludeAttrName = aliasMatch.Groups["attr"].Value;
                            break;
                        }
                    }
                }
            }

            // Pass 2: resolve the shared prelude's actual source text, if any kernel needs one.
            if (preludeAttrName != null)
            {
                Regex preludeDef = new Regex(
                    "\\b" + Regex.Escape(preludeAttrName) + @"\s*=\s*r?(""""""|''')(?<body>.*?)\1",
                    RegexOptions.Singleline);
                foreach (KeyValuePair<PackageDescriptor, string> entry in pyFilesByPackage)
                {
                    string text;
                    try { text = File.ReadAllText(entry.Value); } catch { continue; }
                    Match m = preludeDef.Match(text);
                    if (m.Success)
                    {
                        result.CommonPreludeSource = m.Groups["body"].Value.Trim('\r', '\n');
                        break;
                    }
                }
            }

            return result;
        }

        // From `startIndex` (the start of "extern \"C\" __global__ ..."), walk forward to the
        // opening '{' of the function body, then track brace depth to its matching '}'.
        /// <summary>
        /// Find kernels in the files <paramref name="pythonText"/> reads at run time, and add them
        /// to <paramref name="result"/>.
        ///
        /// <para>
        /// A file is only opened if its extension is one a kernel plausibly lives in, and only if
        /// it resolves inside the package. Following an arbitrary literal out of a source tree
        /// would let a scan of somebody's project read whatever a string in it happened to name.
        /// </para>
        /// </summary>
        private static void CollectFileLoadedKernels(string pythonText, string pythonFile,
            string packageDirectory, CudaScaffoldAnalysis result)
        {
            string pythonDirectory;
            try { pythonDirectory = Path.GetDirectoryName(Path.GetFullPath(pythonFile)); }
            catch { return; }
            if (string.IsNullOrEmpty(pythonDirectory)) return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match load in FileLoad.Matches(pythonText))
            {
                string relative = load.Groups["path"].Success && load.Groups["path"].Value.Length > 0
                    ? load.Groups["path"].Value
                    : load.Groups["path2"].Value;
                if (string.IsNullOrEmpty(relative)) continue;

                string extension = SafeExtension(relative);
                if (Array.IndexOf(KernelFileExtensions, extension) < 0) continue;

                string resolved = ResolveKernelFile(relative, pythonDirectory, packageDirectory);
                if (resolved == null || !seen.Add(resolved)) continue;

                string source;
                try { source = File.ReadAllText(resolved); } catch { continue; }

                foreach (Match sig in FileKernelSignature.Matches(source))
                {
                    string kernelOnly = ExtractBalancedFunction(source, sig.Index);
                    if (kernelOnly == null) continue;

                    var kernel = new DetectedCudaKernel
                    {
                        KernelName = sig.Groups["name"].Value,
                        // The WHOLE file, not just the matched function: a kernel file normally
                        // carries __device__ helpers and #includes the kernel needs, and shipping
                        // the function alone would produce a .cu that does not compile.
                        Body = source.Trim(),
                        SourceFile = pythonFile,
                        PackageDirectory = packageDirectory,
                        SourceKind = CudaKernelSourceKind.ExternalFile,
                        ExternalFilePath = resolved,
                        LoadExpression = load.Groups["expr"].Value.Trim(),
                    };
                    ParseParameters(kernelOnly, kernel.Parameters);
                    result.Kernels.Add(kernel);
                }
            }
        }

        /// <summary>
        /// Resolve a path literal against the .py's own folder, then the package root, and refuse
        /// anything that escapes the package.
        /// </summary>
        private static string ResolveKernelFile(string relative, string pythonDirectory,
            string packageDirectory)
        {
            foreach (string baseDirectory in new[] { pythonDirectory, packageDirectory })
            {
                if (string.IsNullOrEmpty(baseDirectory)) continue;
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.IsPathRooted(relative)
                        ? relative
                        : Path.Combine(baseDirectory, relative));
                }
                catch { continue; }

                if (!File.Exists(candidate)) continue;
                if (!string.IsNullOrEmpty(packageDirectory)
                    && !HelixPythonNamespace.Core.IO.PathComparison.IsUnderDirectory(candidate, packageDirectory))
                    continue;
                return candidate;
            }
            return null;
        }

        private static string SafeExtension(string path)
        {
            try { return Path.GetExtension(path); }
            catch (ArgumentException) { return string.Empty; }
        }

        private static string ExtractBalancedFunction(string text, int startIndex)
        {
            int braceOpen = text.IndexOf('{', startIndex);
            if (braceOpen < 0) return null;
            int depth = 0;
            for (int i = braceOpen; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text.Substring(startIndex, i - startIndex + 1).Trim();
                }
            }
            return null;
        }

        // Splits the parenthesized parameter list into (type-text, name) pairs. Strips //
        // line comments first so they never leak into a split; tracks paren depth so a
        // parameter with a parenthesized default/expression (rare, but cheap to support)
        // doesn't get split on its internal comma.
        private static void ParseParameters(string kernelText, List<CudaKernelParam> outParams)
        {
            int parenOpen = kernelText.IndexOf('(');
            if (parenOpen < 0) return;
            int depth = 0, parenClose = -1;
            for (int i = parenOpen; i < kernelText.Length; i++)
            {
                if (kernelText[i] == '(') depth++;
                else if (kernelText[i] == ')')
                {
                    depth--;
                    if (depth == 0) { parenClose = i; break; }
                }
            }
            if (parenClose < 0) return;

            string paramsText = kernelText.Substring(parenOpen + 1, parenClose - parenOpen - 1);
            paramsText = Regex.Replace(paramsText, @"//[^\n]*", string.Empty);
            paramsText = Regex.Replace(paramsText, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

            List<string> chunks = SplitTopLevel(paramsText);
            foreach (string raw in chunks)
            {
                string chunk = raw.Trim();
                if (chunk.Length == 0) continue;
                int lastSep = LastNameBoundary(chunk);
                if (lastSep < 0) continue;
                string type = chunk.Substring(0, lastSep + 1).Trim();
                string name = chunk.Substring(lastSep + 1).Trim();
                if (name.Length == 0) continue;
                outParams.Add(new CudaKernelParam(type, name));
            }
        }

        private static List<string> SplitTopLevel(string s)
        {
            List<string> result = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < s.Length) result.Add(s.Substring(start));
            return result;
        }

        // The parameter name is the trailing identifier; everything before it (type,
        // qualifiers, '*', __restrict__) is the type text. Walk back from the end past the
        // name's word characters to find the boundary.
        private static int LastNameBoundary(string chunk)
        {
            int end = chunk.Length - 1;
            while (end >= 0 && char.IsWhiteSpace(chunk[end])) end--;
            int nameStart = end;
            while (nameStart >= 0 && (char.IsLetterOrDigit(chunk[nameStart]) || chunk[nameStart] == '_')) nameStart--;
            return nameStart;
        }

        private static IEnumerable<string> SafeEnumeratePyFiles(string dir)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.py", SearchOption.AllDirectories); }
            catch { yield break; }
            foreach (string f in files) yield return f;
        }
    }
}
