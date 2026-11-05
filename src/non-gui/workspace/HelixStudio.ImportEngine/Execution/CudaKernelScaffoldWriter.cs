using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HelixPythonNamespace.ImportEngine.Analysis;
using HelixPythonNamespace.ImportEngine.Planning;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Execution
{
    /// <summary>
    /// Generates the "CUDA kernel debug scaffold" (SS §linked-cmake-project convention — see
    /// tests/fixtures/python/g1m_ccxtsri_pae_gpu__helix_import_v2_cuda_separated/cuda_kernel_debug/README_HELIXSTUDIO_CONVENTION.md)
    /// from kernels CudaKernelScaffoldAnalyzer found embedded as Python string literals:
    /// extracts each kernel into its own package/cuda/*.cu file, rewrites the Python loader to
    /// read from disk (inlining the old inline-string load with the same NVRTC-safe #include
    /// expansion CuPy needs), and generates a companion native CMake project — one plain host
    /// C++ executable per kernel, each calling the REAL (untouched) kernel through a mechanical
    /// extern "C" launcher — so the kernel body can be stepped into with a real debugger
    /// (cuda-gdb/Nsight/VS), not just HelixStudio's CPU emulator.
    ///
    /// This is a best-effort SCAFFOLD, not a functional test: synthetic buffers are fixed-size
    /// (8 elements) and zero-initialized, scalar parameters get small generic defaults. The goal
    /// is "you can set a breakpoint inside your real kernel and step through it", not
    /// scientifically meaningful output — the kernel's actual domain semantics are unknowable to
    /// a generic tool. <see cref="ImportExecutionResult.Notes"/> says so explicitly.
    /// </summary>
    public static class CudaKernelScaffoldWriter
    {
        private const int SyntheticBufferLength = 8;
        private const string DefaultCudaArchitecture = "75-real";
        private const string LauncherPrefix = "helix_launch_";

        public static void Write(ImportPlan plan, IReadOnlyList<PackageDescriptor> finalPackages,
            string root, string rootTomlPath, ImportExecutionResult result)
        {
            if (!plan.GenerateCudaDebugScaffold) return;

            // Re-analyze against the FINAL (post-rename/post-move) package directories rather
            // than trusting the wizard-time analysis snapshot, whose paths Phase 1 may have
            // already moved out from under.
            CudaScaffoldAnalysis analysis = CudaKernelScaffoldAnalyzer.Analyze(finalPackages);
            if (!analysis.HasKernels)
            {
                result.Diagnostics.Add("CUDA debug scaffold requested, but no inline CUDA kernels were found to extract.");
                return;
            }

            var byPackage = analysis.Kernels.GroupBy(k => k.PackageDirectory, StringComparer.OrdinalIgnoreCase);
            var cudaDirsByPackage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, DetectedCudaKernel> pkgGroup in byPackage)
            {
                string pkgDir = pkgGroup.Key;
                string cudaDir = Path.Combine(pkgDir, "cuda");
                Directory.CreateDirectory(cudaDir);
                cudaDirsByPackage[pkgDir] = cudaDir;

                bool anyUsesPrelude = pkgGroup.Any(k => k.UsesCommonPrelude);
                if (anyUsesPrelude && !string.IsNullOrEmpty(analysis.CommonPreludeSource))
                {
                    File.WriteAllText(Path.Combine(cudaDir, "common.cuh"), BuildCommonHeader(analysis.CommonPreludeSource));
                }

                foreach (DetectedCudaKernel kernel in pkgGroup)
                {
                    string cuPath = Path.Combine(cudaDir, kernel.CuFileName);
                    File.WriteAllText(cuPath, BuildKernelCuFile(kernel));
                }

                // Rewrite the loader module(s) that embedded these kernels: inject the file-
                // reading helper once per file, then retarget each kernel's assignment.
                foreach (IGrouping<string, DetectedCudaKernel> fileGroup in pkgGroup.GroupBy(k => k.SourceFile))
                    RewriteLoaderModule(fileGroup.Key, fileGroup.ToList());

                result.Notes.Add("CUDA debug scaffold: extracted " + pkgGroup.Count() + " kernel(s) into " +
                    MakeRelative(root, cudaDir));
            }

            string scaffoldDir = Path.Combine(root, "cuda_kernel_debug");
            Directory.CreateDirectory(Path.Combine(scaffoldDir, "kernels"));
            Directory.CreateDirectory(Path.Combine(scaffoldDir, "apps"));
            Directory.CreateDirectory(Path.Combine(scaffoldDir, "common"));

            File.WriteAllText(Path.Combine(scaffoldDir, "common", "debug_common.h"), DebugCommonHeader());
            File.WriteAllText(Path.Combine(scaffoldDir, ".gitignore"), "cmake-build-*/\n");

            foreach (DetectedCudaKernel kernel in analysis.Kernels)
            {
                File.WriteAllText(Path.Combine(scaffoldDir, "kernels", "launch_" + kernel.CuFileName),
                    BuildLauncherCuFile(kernel));
                File.WriteAllText(Path.Combine(scaffoldDir, "apps", "main_" + kernel.KernelName + ".cpp"),
                    BuildHostAppCppFile(kernel));
            }

            File.WriteAllText(Path.Combine(scaffoldDir, "CMakeLists.txt"),
                BuildCMakeLists(plan.SolutionName, root, scaffoldDir, cudaDirsByPackage.Values, analysis.Kernels));

            var helixstudioKeys = new Dictionary<string, string>
            {
                { "linked-cmake-project", "\"cuda_kernel_debug\"" }
            };
            TomlTableMerger.EnsureTable(rootTomlPath, "tool.helixstudio", helixstudioKeys);

            result.Notes.Add("CUDA debug scaffold: generated cuda_kernel_debug/ (" + analysis.Kernels.Count +
                " kernel(s), 1 static library, " + analysis.Kernels.Count + " debug executable(s)) and set " +
                "[tool.helixstudio] linked-cmake-project = \"cuda_kernel_debug\" in the root pyproject.toml.");
            result.Diagnostics.Add(
                "CUDA debug scaffold apps use fixed-size synthetic buffers (zero-initialized, " +
                SyntheticBufferLength + " elements) and generic scalar defaults — a best-effort " +
                "harness for stepping into the real kernel with a debugger, not a functional test.");
        }

        // ---- Python loader rewrite ----

        private static readonly Regex ImportBlockEnd = new Regex(@"^(?:from\s+\S+\s+import\s+.+|import\s+\S+)\s*$", RegexOptions.Multiline);

        private static void RewriteLoaderModule(string pyFilePath, List<DetectedCudaKernel> kernels)
        {
            string text;
            try { text = File.ReadAllText(pyFilePath); } catch { return; }

            if (text.IndexOf("_read_cuda_source", StringComparison.Ordinal) < 0)
            {
                if (text.IndexOf("\nimport re\n", StringComparison.Ordinal) < 0 &&
                    !text.StartsWith("import re\n", StringComparison.Ordinal))
                    text = InsertAfterImports(text, "import re\nfrom pathlib import Path\n");
                text = InsertAfterImports(text, BuildLoaderHelper());
            }

            foreach (DetectedCudaKernel kernel in kernels)
            {
                if (string.IsNullOrEmpty(kernel.AssignmentVariableName)) continue;
                string pattern = @"\b" + Regex.Escape(kernel.AssignmentVariableName) +
                    @"\s*=\s*(?:\w+\s*\+\s*)?r?(""""""|''').*?\1";
                // MatchEvaluator, not a replacement-pattern string: Regex.Replace treats '$' as a
                // backreference token in the string overload, and a kernel/variable name could in
                // principle contain one — a literal delegate side-steps that entirely.
                string replacement = kernel.AssignmentVariableName + " = _read_cuda_source(\"" + kernel.CuFileName + "\")";
                text = Regex.Replace(text, pattern, delegate { return replacement; }, RegexOptions.Singleline);
            }

            try { File.WriteAllText(pyFilePath, text); } catch { }
        }

        private static string InsertAfterImports(string text, string block)
        {
            if (text.IndexOf(block.TrimEnd('\n'), StringComparison.Ordinal) >= 0) return text;
            MatchCollection imports = ImportBlockEnd.Matches(text);
            if (imports.Count == 0) return block + "\n" + text;
            Match last = imports[imports.Count - 1];
            int insertAt = last.Index + last.Length;
            // Skip to the end of the blank-line gap after the import block.
            while (insertAt < text.Length && text[insertAt] == '\r') insertAt++;
            if (insertAt < text.Length && text[insertAt] == '\n') insertAt++;
            return text.Substring(0, insertAt) + "\n\n" + block + text.Substring(insertAt);
        }

        private static string BuildLoaderHelper()
        {
            return
                "_CUDA_DIR = Path(__file__).resolve().parent / \"cuda\"\n" +
                "_INCLUDE_RE = re.compile(r'^[ \\t]*#include\\s*\"([^\"]+)\"[ \\t]*$', re.MULTILINE)\n" +
                "\n\n" +
                "def _read_cuda_source(filename: str) -> str:\n" +
                "    \"\"\"Load a .cu file from cuda/, inlining any local #include \"x.cuh\" header.\n\n" +
                "    CuPy's RawKernel compiles through NVRTC, which does not walk the filesystem\n" +
                "    for local #include paths the way nvcc does, so the (single-level) header\n" +
                "    include is expanded here before the source is handed to cp.RawKernel.\n" +
                "    \"\"\"\n" +
                "    text = (_CUDA_DIR / filename).read_text()\n" +
                "    return _INCLUDE_RE.sub(lambda m: (_CUDA_DIR / m.group(1)).read_text(), text)\n";
        }

        // ---- .cu / .cuh generation ----

        private static string BuildCommonHeader(string preludeSource)
        {
            return "// Shared device helpers, extracted by HelixStudio Import Engine from the " +
                "Python source that used to embed this text inline (via #include \"common.cuh\").\n" +
                preludeSource.Trim('\r', '\n') + "\n";
        }

        private static string BuildKernelCuFile(DetectedCudaKernel kernel)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("// Extracted by HelixStudio Import Engine from ").Append(Path.GetFileName(kernel.SourceFile)).Append(".\n");
            if (kernel.UsesCommonPrelude) sb.Append("#include \"common.cuh\"\n\n");
            sb.Append(kernel.Body.Trim()).Append('\n');
            return sb.ToString();
        }

        private static string BuildLauncherCuFile(DetectedCudaKernel kernel)
        {
            string paramList = string.Join(", ", kernel.Parameters.Select(p => p.TypeText + " " + p.Name));
            string argList = string.Join(", ", kernel.Parameters.Select(p => p.Name));
            StringBuilder sb = new StringBuilder();
            sb.Append("// Auto-generated HelixStudio CUDA kernel-debug launcher for `").Append(kernel.KernelName).Append("`.\n");
            sb.Append("// Wraps the real kernel (untouched, included below) in a plain extern \"C\" function\n");
            sb.Append("// so a host C++ main can call it without <<<>>> launch syntax.\n");
            sb.Append("#include \"").Append(kernel.CuFileName).Append("\"\n\n");
            sb.Append("extern \"C\" cudaError_t ").Append(LauncherPrefix).Append(kernel.KernelName).Append("(\n");
            sb.Append("    dim3 grid, dim3 block, size_t shmemBytes, cudaStream_t stream");
            if (paramList.Length > 0) sb.Append(",\n    ").Append(paramList);
            sb.Append(")\n{\n");
            sb.Append("    ").Append(kernel.KernelName).Append("<<<grid, block, shmemBytes, stream>>>(").Append(argList).Append(");\n");
            sb.Append("    return cudaGetLastError();\n}\n");
            return sb.ToString();
        }

        private static string BuildHostAppCppFile(DetectedCudaKernel kernel)
        {
            string paramDeclList = string.Join(",\n    ", kernel.Parameters.Select(p => p.TypeText + " " + p.Name));
            StringBuilder sb = new StringBuilder();
            sb.Append("// Auto-generated HelixStudio CUDA kernel-debug harness for `").Append(kernel.KernelName).Append("`.\n");
            sb.Append("// Best-effort SCAFFOLD: fixed-size (").Append(SyntheticBufferLength).Append("-element), zero-initialized\n");
            sb.Append("// buffers and small generic scalar defaults — enough to build, link, launch, and let you\n");
            sb.Append("// set a breakpoint inside the real kernel body and step through it with a real debugger.\n");
            sb.Append("// Not a functional test: this tool cannot know what values are domain-meaningful.\n");
            sb.Append("#include <cuda_runtime.h>\n\n#include <cstdio>\n#include <vector>\n\n#include \"debug_common.h\"\n\n");
            sb.Append("extern \"C\" cudaError_t ").Append(LauncherPrefix).Append(kernel.KernelName).Append("(\n");
            sb.Append("    dim3 grid, dim3 block, size_t shmemBytes, cudaStream_t stream");
            if (paramDeclList.Length > 0) sb.Append(",\n    ").Append(paramDeclList);
            sb.Append(");\n\n");
            sb.Append("int main() {\n");
            sb.Append("    printSectionHeader(\"").Append(kernel.KernelName).Append("\");\n\n");
            sb.Append("    const int N = ").Append(SyntheticBufferLength).Append(";\n\n");

            List<string> callArgs = new List<string>();
            List<string> freeVars = new List<string>();
            foreach (CudaKernelParam p in kernel.Parameters)
            {
                if (p.IsPointer)
                {
                    string baseType = BaseType(p.TypeText);
                    string varName = "d_" + p.Name;
                    sb.Append("    ").Append(baseType).Append("* ").Append(varName)
                      .Append(" = deviceZeros<").Append(baseType).Append(">(N);\n");
                    callArgs.Add(varName);
                    freeVars.Add(varName);
                }
                else
                {
                    string value = DefaultScalarValue(p.TypeText);
                    callArgs.Add(value);
                }
            }
            sb.Append('\n');
            sb.Append("    dim3 grid(1), block(N);\n");
            sb.Append("    size_t shmemBytes = 0;\n\n");
            sb.Append("    CUDA_CHECK(").Append(LauncherPrefix).Append(kernel.KernelName).Append("(\n");
            sb.Append("        grid, block, shmemBytes, 0");
            if (callArgs.Count > 0) sb.Append(",\n        ").Append(string.Join(", ", callArgs));
            sb.Append("));\n");
            sb.Append("    CUDA_CHECK(cudaDeviceSynchronize());\n\n");
            sb.Append("    std::printf(\"launch + sync succeeded for ").Append(kernel.KernelName).Append("\\n\");\n\n");
            foreach (string v in freeVars) sb.Append("    cudaFree(").Append(v).Append(");\n");
            sb.Append("    return 0;\n}\n");
            return sb.ToString();
        }

        // Strips qualifiers/pointer stars to the underlying element type, for
        // deviceZeros<T>()'s template argument (e.g. "const long long* __restrict__" -> "long long").
        private static string BaseType(string typeText)
        {
            string t = typeText;
            t = Regex.Replace(t, @"\b__restrict__\b", string.Empty);
            t = Regex.Replace(t, @"\bconst\b", string.Empty);
            t = t.Replace("*", string.Empty);
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        private static string DefaultScalarValue(string typeText)
        {
            string t = BaseType(typeText);
            if (t == "float") return "1.0f";
            if (t == "double") return "1.0";
            if (t == "bool") return "false";
            // int / long long / size_t / unsigned variants and anything else numeric-looking:
            // 1 is small enough to stay in-bounds against every N-length synthetic buffer above,
            // and non-zero so a simple "if (n == 0) return;" guard doesn't make the launch a no-op.
            return "1";
        }

        private static string DebugCommonHeader()
        {
            return
                "// Shared host-only helpers for the per-kernel debug executables under apps/.\n" +
                "// Plain C++ (no CUDA language extensions) - compiled by the regular host compiler,\n" +
                "// not nvcc, and linked against CUDA::cudart (see ../CMakeLists.txt).\n" +
                "#pragma once\n\n" +
                "#include <cuda_runtime.h>\n\n" +
                "#include <cstdio>\n#include <cstdlib>\n#include <vector>\n\n" +
                "#define CUDA_CHECK(expr)                                                          \\\n" +
                "    do {                                                                          \\\n" +
                "        cudaError_t _cuda_check_err = (expr);                                     \\\n" +
                "        if (_cuda_check_err != cudaSuccess) {                                     \\\n" +
                "            std::fprintf(stderr, \"[CUDA ERROR] %s:%d: %s -> %s\\n\", __FILE__,      \\\n" +
                "                          __LINE__, #expr, cudaGetErrorString(_cuda_check_err));   \\\n" +
                "            std::exit(1);                                                         \\\n" +
                "        }                                                                         \\\n" +
                "    } while (0)\n\n" +
                "// Allocates a device buffer and copies `host` into it.\n" +
                "template <typename T>\n" +
                "T* deviceUpload(const std::vector<T>& host) {\n" +
                "    T* d = nullptr;\n" +
                "    size_t bytes = host.size() * sizeof(T);\n" +
                "    CUDA_CHECK(cudaMalloc(reinterpret_cast<void**>(&d), bytes > 0 ? bytes : sizeof(T)));\n" +
                "    if (!host.empty()) {\n" +
                "        CUDA_CHECK(cudaMemcpy(d, host.data(), bytes, cudaMemcpyHostToDevice));\n" +
                "    }\n" +
                "    return d;\n" +
                "}\n\n" +
                "// Allocates a zero-initialized device buffer of `count` elements (for kernel outputs).\n" +
                "template <typename T>\n" +
                "T* deviceZeros(size_t count) {\n" +
                "    T* d = nullptr;\n" +
                "    size_t bytes = (count > 0 ? count : 1) * sizeof(T);\n" +
                "    CUDA_CHECK(cudaMalloc(reinterpret_cast<void**>(&d), bytes));\n" +
                "    CUDA_CHECK(cudaMemset(d, 0, bytes));\n" +
                "    return d;\n" +
                "}\n\n" +
                "// Copies `count` elements of a device buffer back to the host.\n" +
                "template <typename T>\n" +
                "std::vector<T> deviceDownload(T* d, size_t count) {\n" +
                "    std::vector<T> host(count);\n" +
                "    if (count > 0) {\n" +
                "        CUDA_CHECK(cudaMemcpy(host.data(), d, count * sizeof(T), cudaMemcpyDeviceToHost));\n" +
                "    }\n" +
                "    return host;\n" +
                "}\n\n" +
                "inline void printSectionHeader(const char* kernelName) {\n" +
                "    std::printf(\"=== HelixStudio CUDA kernel debug harness: %s ===\\n\", kernelName);\n" +
                "}\n";
        }

        // ---- CMakeLists.txt generation ----

        private static string BuildCMakeLists(string solutionName, string root, string scaffoldDir,
            IEnumerable<string> cudaDirs, List<DetectedCudaKernel> kernels)
        {
            string safeName = SanitizeIdentifier(solutionName);
            string libTarget = safeName + "_cuda_kernels";

            StringBuilder sb = new StringBuilder();
            sb.Append("# Auto-generated by HelixStudio Import Engine — CUDA kernel debug scaffold.\n");
            sb.Append("# See ").Append(safeName).Append("'s root pyproject.toml [tool.helixstudio] linked-cmake-project,\n");
            sb.Append("# and README_HELIXSTUDIO_CONVENTION.md for the full convention this implements.\n");
            sb.Append("cmake_minimum_required(VERSION 3.24)\n\n");
            sb.Append("# Override with -DCMAKE_CUDA_ARCHITECTURES=<yours>-real for your GPU. MUST be set as a\n");
            sb.Append("# CACHE variable before project() enables the CUDA language, and the -real suffix (SASS-\n");
            sb.Append("# only, no embedded PTX) avoids a driver-side PTX-JIT rejection when nvcc is newer than\n");
            sb.Append("# the installed driver's JIT compiler.\n");
            sb.Append("if(NOT DEFINED CMAKE_CUDA_ARCHITECTURES)\n");
            sb.Append("  set(CMAKE_CUDA_ARCHITECTURES ").Append(DefaultCudaArchitecture)
              .Append(" CACHE STRING \"HelixStudio CUDA kernel debug target architecture\")\n");
            sb.Append("endif()\n\n");
            sb.Append("project(").Append(safeName).Append("_cuda_kernel_debug LANGUAGES CXX CUDA)\n\n");
            sb.Append("set(CMAKE_CXX_STANDARD 17)\nset(CMAKE_CXX_STANDARD_REQUIRED ON)\n");
            sb.Append("set(CMAKE_CUDA_STANDARD 17)\nset(CMAKE_CUDA_STANDARD_REQUIRED ON)\n\n");
            sb.Append("find_package(CUDAToolkit REQUIRED)\n\n");

            List<string> relDirs = cudaDirs.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(d => MakeRelative(scaffoldDir, d).Replace('\\', '/')).ToList();
            for (int i = 0; i < relDirs.Count; i++)
                sb.Append("set(HELIX_KERNEL_SRC_DIR_").Append(i).Append(" \"${CMAKE_CURRENT_SOURCE_DIR}/")
                  .Append(relDirs[i]).Append("\")\n");
            sb.Append('\n');

            sb.Append("add_library(").Append(libTarget).Append(" STATIC\n");
            foreach (DetectedCudaKernel k in kernels)
                sb.Append("    kernels/launch_").Append(k.CuFileName).Append('\n');
            sb.Append(")\n");
            sb.Append("target_include_directories(").Append(libTarget).Append(" PRIVATE common");
            for (int i = 0; i < relDirs.Count; i++) sb.Append(" ${HELIX_KERNEL_SRC_DIR_").Append(i).Append('}');
            sb.Append(")\n");
            sb.Append("target_link_libraries(").Append(libTarget).Append(" PUBLIC CUDA::cudart)\n");
            sb.Append("set_target_properties(").Append(libTarget).Append(" PROPERTIES CUDA_SEPARABLE_COMPILATION ON)\n\n");

            sb.Append("function(helix_add_kernel_debug_app kernel_name)\n");
            sb.Append("  add_executable(debug_${kernel_name} apps/main_${kernel_name}.cpp)\n");
            sb.Append("  target_link_libraries(debug_${kernel_name} PRIVATE ").Append(libTarget).Append(" CUDA::cudart)\n");
            sb.Append("  target_include_directories(debug_${kernel_name} PRIVATE common)\n");
            sb.Append("  set_target_properties(debug_${kernel_name} PROPERTIES CUDA_SEPARABLE_COMPILATION ON)\n");
            sb.Append("endfunction()\n\n");

            foreach (DetectedCudaKernel k in kernels)
                sb.Append("helix_add_kernel_debug_app(").Append(k.KernelName).Append(")\n");

            return sb.ToString();
        }

        private static string SanitizeIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return "helix";
            string cleaned = Regex.Replace(s, @"[^A-Za-z0-9_]", "_").ToLowerInvariant();
            if (cleaned.Length == 0 || char.IsDigit(cleaned[0])) cleaned = "s_" + cleaned;
            return cleaned;
        }

        private static string MakeRelative(string fromDir, string toDir)
        {
            Uri fromUri = new Uri(AppendSlash(fromDir));
            Uri toUri = new Uri(AppendSlash(toDir));
            string rel = fromUri.MakeRelativeUri(toUri).ToString();
            return Uri.UnescapeDataString(rel).TrimEnd('/');
        }

        private static string AppendSlash(string path)
        {
            string full = Path.GetFullPath(path);
            return full.EndsWith(Path.DirectorySeparatorChar.ToString()) ? full : full + Path.DirectorySeparatorChar;
        }
    }
}
