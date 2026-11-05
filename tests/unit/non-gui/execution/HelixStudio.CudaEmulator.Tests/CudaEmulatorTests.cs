namespace HelixPythonNamespace.CudaEmulator.Tests
{
    using System;
    using System.IO;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.CudaEmulator;
    using HelixPythonNamespace.CudaVirtualCompiler;

    [TestClass]
    public class CudaEmulatorTests
    {
        private sealed class RunOutcome
        {
            public EmulationResult Result;
            public string Output;
            public CudaEmulator Emu;
        }

        private static RunOutcome Run(string src)
        {
            CudaCompileResult compiled = CudaVirtualCompiler.CompileSource(src, "test.cu");
            StringOutputSink sink = new StringOutputSink();
            CudaEmulator emu = new CudaEmulator(compiled, sink);
            EmulationResult res = emu.RunProgram("main");
            RunOutcome o = new RunOutcome();
            o.Result = res;
            o.Output = sink.Text;
            o.Emu = emu;
            return o;
        }

        [TestMethod]
        public void ToolchainCompilesAndRunsMixedCudaCppProgram()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_toolchain_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string cudaFile = Path.Combine(dir, "kernel.cu");
            string cppFile = Path.Combine(dir, "main.cpp");
            File.WriteAllText(cudaFile,
                "__host__ __device__ int twice(int x) { return x * 2; }\n" +
                "__global__ void k(int* out) { int i=threadIdx.x; out[i]=twice(i+1); }\n");
            File.WriteAllText(cppFile,
                "int main(){ int* d; if(cudaMalloc(&d,12)!=cudaSuccess)return 1; " +
                "k<<<1,3>>>(d); if(cudaGetLastError()!=cudaSuccess)return 2; " +
                "if(cudaDeviceSynchronize()!=cudaSuccess)return 3; int h[3]; " +
                "if(cudaMemcpy(h,d,12,cudaMemcpyDeviceToHost)!=cudaSuccess)return 4; " +
                "printf(\"EMU:%d,%d,%d\\n\",h[0],h[1],h[2]); " +
                "if(cudaFree(d)!=cudaSuccess)return 5; return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { cudaFile, cppFile }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                string diagnostics = string.Empty;
                foreach (CudaCompileDiagnostic diagnostic in result.Compilation.Diagnostics)
                    diagnostics += diagnostic + Environment.NewLine;
                Assert.IsTrue(result.RunAttempted,
                    result.FailureReason + Environment.NewLine + diagnostics);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual(0, result.ExitCode);
                Assert.AreEqual(2, result.SourceFileCount);
                Assert.AreEqual(cudaFile, result.TranslationUnits[0].FilePath);
                Assert.AreEqual(cppFile, result.TranslationUnits[1].FilePath);
                Assert.AreEqual("EMU:2,4,6\n", result.Output);
                Assert.AreEqual(1, result.Kernels.Count);
                Assert.AreEqual(3UL, result.Kernels[0].TotalThreads);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainRefusesUncheckedCudaExecutionByDefault()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_safety_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "main.cu");
            File.WriteAllText(file,
                "__global__ void k(){}\n" +
                "int main(){ k<<<1,1>>>(); cudaDeviceSynchronize(); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { file }, true);
                Assert.IsTrue(result.CompileSucceeded);
                Assert.IsFalse(result.SafetyGatePassed);
                Assert.IsFalse(result.RunAttempted);
                Assert.AreEqual(1, result.ExitCode);
                StringAssert.Contains(result.FailureReason, "safety gate refused");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainAppliesCxxAndCudaStandardsPerSourceUnit()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_dialect_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string cuda = Path.Combine(dir, "kernel.cu");
            string cpp = Path.Combine(dir, "main.cpp");
            File.WriteAllText(cuda,
                "__global__ void k(){ constexpr int value=1; }\n");
            File.WriteAllText(cpp,
                "int main(){ int* p=nullptr; return p==nullptr; }\n");
            try
            {
                CudaCppEmulatorToolchainResult old =
                    CudaCppEmulatorToolchain.CompileAndRun(
                        new[] { cuda, cpp }, false, "main", true, "98", "03");
                Assert.IsFalse(old.CompileSucceeded);
                bool cppMapped = false, cudaMapped = false;
                foreach (CudaCompileDiagnostic diagnostic in old.Compilation.Diagnostics)
                    if (diagnostic.Code == CudaDiagnosticCodes.StandardFeatureUnavailable)
                    {
                        if (diagnostic.FilePath == cpp) cppMapped = true;
                        if (diagnostic.FilePath == cuda) cudaMapped = true;
                    }
                Assert.IsTrue(cppMapped, "Expected C++98 nullptr diagnostic in main.cpp.");
                Assert.IsTrue(cudaMapped, "Expected CUDA C++03 constexpr diagnostic in kernel.cu.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainExpandsQuotedLocalHeadersBeforeLinking()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_include_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string header = Path.Combine(dir, "kernel.hpp");
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(header, "int answer();\n");
            File.WriteAllText(source,
                "#include \"kernel.hpp\"\n" +
                "int answer(){ return 42; }\n" +
                "int main(){ printf(\"HDR:%d\\n\", answer()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("HDR:42\n", result.Output);
                Assert.AreEqual(1, result.IncludedFiles.Count);
                Assert.AreEqual(Path.GetFullPath(header), result.IncludedFiles[0]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainRejectsCyclicLocalHeaderIncludes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_cycle_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string a = Path.Combine(dir, "a.hpp");
            string b = Path.Combine(dir, "b.hpp");
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(a, "#include \"b.hpp\"\n");
            File.WriteAllText(b, "#include \"a.hpp\"\n");
            File.WriteAllText(source, "#include \"a.hpp\"\nint main(){return 0;}\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, false);
                Assert.IsFalse(result.CompileSucceeded);
                StringAssert.Contains(result.FailureReason, "cyclic local header include detected");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainSearchesConfiguredIncludeDirectoriesAndExpandsAngleHeaders()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_include_path_" +
                Guid.NewGuid().ToString("N"));
            string include = Path.Combine(dir, "include");
            Directory.CreateDirectory(include);
            string header = Path.Combine(include, "include_api.hpp");
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(header, "int include_answer(){ return 17; }\n");
            File.WriteAllText(source,
                "#include <include_api.hpp>\n" +
                "int main(){ printf(\"PATH:%d\\n\", include_answer()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true,
                        "main", true, "20", "20", new[] { include });
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("PATH:17\n", result.Output);
                Assert.AreEqual(1, result.IncludedFiles.Count);
                Assert.AreEqual(Path.GetFullPath(header), result.IncludedFiles[0]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainHonorsPragmaOnceAcrossRepeatedIncludes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_pragma_once_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string header = Path.Combine(dir, "once.hpp");
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(header, "#pragma once\nint once_answer(){ return 23; }\n");
            File.WriteAllText(source,
                "#include \"once.hpp\"\n#include \"once.hpp\"\n" +
                "int main(){ printf(\"ONCE:%d\\n\", once_answer()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("ONCE:23\n", result.Output);
                Assert.AreEqual(1, result.IncludedFiles.Count);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainMapsHeaderDiagnosticsToHeaderFileAndLine()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_header_map_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string header = Path.Combine(dir, "broken.hpp");
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(header, "int valid(){ return 1; }\nint broken = ;\n");
            File.WriteAllText(source,
                "#include \"broken.hpp\"\nint main(){ return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, false);
                Assert.IsFalse(result.CompileSucceeded);
                bool mapped = false;
                foreach (CudaCompileDiagnostic diagnostic in result.Compilation.Diagnostics)
                    if (string.Equals(diagnostic.FilePath, header, StringComparison.OrdinalIgnoreCase))
                    {
                        mapped = true;
                        Assert.IsTrue(diagnostic.Span.Start.Line >= 1,
                            "Header diagnostic should retain its physical line: " + diagnostic);
                    }
                Assert.IsTrue(mapped, "Expected malformed header diagnostic to map to broken.hpp.");
                bool headerLineTwo = false;
                foreach (CudaCppSourceLineInfo line in result.SourceLineMap)
                    if (string.Equals(line.FilePath, header, StringComparison.OrdinalIgnoreCase) &&
                        line.SourceLine == 2) headerLineTwo = true;
                Assert.IsTrue(headerLineTwo, "Expected linked source map to retain broken.hpp line 2.");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainSkipsIncludesInInactiveConditionalBranches()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_if_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(source,
                "#if 0\n#include \"missing-never-used.hpp\"\n#endif\n" +
                "int main(){ printf(\"IF:ok\\n\"); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("IF:ok\n", result.Output);
                Assert.AreEqual(1, result.SkippedConditionalIncludes);
                Assert.IsTrue(result.ConditionalDirectiveCount >= 2);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainElidesInactiveConditionalBranchBodies()
        {
            // SRS solution_explorer_rules §10 (loop 518): lines inside an inactive
            // #if/#else arm used to be handed to the parser, so both arms of a
            // platform guard compiled together (phantom redefinitions, parse errors).
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_elide_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(source,
                "#if 0\nthis is not C++ at all !!! ((\nint clash(){ return 1; }\n#endif\n" +
                "#ifdef _WIN32\nint pick(){ return 1; }\n#else\nint pick(){ return 2; }\n#endif\n" +
                "int main(){ printf(\"ELIDE:%d\\n\", pick()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("ELIDE:1\n", result.Output);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainSeedsCudaCompilerIdentityMacros()
        {
            // A .cu unit sees __CUDACC__ / __CUDA_ARCH__; portability guards take
            // their CUDA arm instead of silently compiling the wrong branch.
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_cudacc_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "main.cu");
            File.WriteAllText(source,
                "#ifdef __CUDACC__\nint mode(){ return 7; }\n#else\nbroken !!\n#endif\n" +
                "int main(){ printf(\"CUDACC:%d\\n\", mode()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("CUDACC:7\n", result.Output);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainSeedsCplusplusPerSelectedDialect()
        {
            // __cplusplus tracks the selected dialect: a C++11 guard is inactive
            // under c++98 and active under c++17.
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_cplusplus_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(source,
                "#if __cplusplus >= 201103L\nint era(){ return 11; }\n#else\nint era(){ return 98; }\n#endif\n" +
                "int main(){ printf(\"ERA:%d\\n\", era()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult modern =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true,
                        "main", true, "17", "17");
                Assert.IsTrue(modern.CompileSucceeded, modern.FailureReason);
                Assert.AreEqual("ERA:11\n", modern.Output);

                CudaCppEmulatorToolchainResult legacy =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true,
                        "main", true, "98", "98");
                Assert.IsTrue(legacy.CompileSucceeded, legacy.FailureReason);
                Assert.AreEqual("ERA:98\n", legacy.Output);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainExpandsMacroNamedHeaderFromConfiguredPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_macro_include_" +
                Guid.NewGuid().ToString("N"));
            string include = Path.Combine(dir, "include");
            Directory.CreateDirectory(include);
            string header = Path.Combine(include, "macro_api.hpp");
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(header, "int macro_answer(){ return 31; }\n");
            File.WriteAllText(source,
                "#define API_HEADER <macro_api.hpp>\n#include API_HEADER\n" +
                "int main(){ printf(\"MACRO:%d\\n\", macro_answer()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true,
                        "main", true, "20", "20", new[] { include });
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("MACRO:31\n", result.Output);
                Assert.AreEqual(1, result.MacroIncludeCount);
                Assert.AreEqual(1, result.IncludedFiles.Count);
                Assert.AreEqual(Path.GetFullPath(header), result.IncludedFiles[0]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainEvaluatesDefinedAndIntegerConditionalExpressions()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_if_expr_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string header = Path.Combine(dir, "selected.hpp");
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(header, "int selected(){ return 37; }\n");
            File.WriteAllText(source,
                "#define FEATURE 0x2U\n" +
                "#if defined(FEATURE) && ((FEATURE + 3) * 2 == 10) && " +
                "((8 >> 1) >= 4) && ((3 & 1) == 1) && " +
                "((FEATURE == 2) ? ((0b1 << 3) == 010) : 0) && !0\n" +
                "#include \"selected.hpp\"\n" +
                "#else\n#include \"must-not-be-opened.hpp\"\n#endif\n" +
                "int main(){ printf(\"EXPR:%d\\n\", selected()); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("EXPR:37\n", result.Output);
                Assert.AreEqual(0, result.UnknownConditionalExpressionCount);
                Assert.AreEqual(1, result.SkippedConditionalIncludes);
                Assert.AreEqual(1, result.IncludedFiles.Count);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainExpandsSafeObjectMacroInOrdinaryHostExpression()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_object_macro_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(source,
                "#define ANSWER 6\n#define TWICE ANSWER + ANSWER\n" +
                "int main(){ int value = TWICE; printf(\"OBJ:%d\\n\", value); return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true);
                Assert.IsTrue(result.CompileSucceeded, result.FailureReason);
                Assert.IsTrue(result.RunSucceeded, result.FailureReason);
                Assert.AreEqual("OBJ:12\n", result.Output);
                Assert.IsTrue(result.ExpandedMacroTokenCount >= 2);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainRejectsHeaderGraphsBeyondDepthLimit()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_depth_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "main.cpp");
            int count = CudaCppEmulatorToolchain.MaxIncludeDepth + 2;
            for (int i = 0; i < count; i++)
            {
                string next = i + 1 < count ? "#include \"h" + (i + 1) + ".hpp\"\n" : "";
                File.WriteAllText(Path.Combine(dir, "h" + i + ".hpp"), next);
            }
            File.WriteAllText(source, "#include \"h0.hpp\"\nint main(){return 0;}\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, false);
                Assert.IsFalse(result.CompileSucceeded);
                StringAssert.Contains(result.FailureReason, "include depth limit exceeded");
                Assert.IsTrue(result.MaximumIncludeDepthReached >=
                    CudaCppEmulatorToolchain.MaxIncludeDepth);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainHonorsCancellationBeforePreprocessing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_cancel_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "main.cpp");
            File.WriteAllText(source, "int main(){return 0;}\n");
            try
            {
                var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { source }, true,
                        "main", true, "20", "20", null, cancellation.Token);
                Assert.IsFalse(result.CompileSucceeded);
                Assert.IsFalse(result.RunAttempted);
                StringAssert.Contains(result.FailureReason, "compilation cancelled");
                Assert.AreEqual(1, result.ExitCode);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainMapsLinkedDiagnosticsBackToOwningSourceFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_map_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string good = Path.Combine(dir, "kernel.cu");
            string bad = Path.Combine(dir, "main.cpp");
            File.WriteAllText(good, "__global__ void k() { }\n");
            File.WriteAllText(bad, "int main( { return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { good, bad }, false);
                Assert.IsFalse(result.CompileSucceeded);
                bool mapped = false;
                foreach (CudaCompileDiagnostic diagnostic in result.Compilation.Diagnostics)
                    if (string.Equals(diagnostic.FilePath, bad, StringComparison.OrdinalIgnoreCase))
                    {
                        mapped = true;
                        Assert.IsTrue(diagnostic.Span.Start.Line <= 2);
                    }
                string details = string.Empty;
                foreach (CudaCompileDiagnostic diagnostic in result.Compilation.Diagnostics)
                    details += diagnostic + Environment.NewLine;
                Assert.IsTrue(mapped,
                    "Expected malformed C++ diagnostic to map to main.cpp." +
                    Environment.NewLine + details);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ToolchainCompileOnlySurfacesCompilerDiagnostics()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_emu_bad_" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "bad.cpp");
            File.WriteAllText(file, "int main( { return 0; }\n");
            try
            {
                CudaCppEmulatorToolchainResult result =
                    CudaCppEmulatorToolchain.CompileAndRun(new[] { file }, false);
                Assert.IsFalse(result.CompileSucceeded);
                Assert.IsFalse(result.RunAttempted);
                Assert.AreEqual(1, result.ExitCode);
                Assert.IsTrue(result.Compilation.Diagnostics.Count > 0);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void ElementwiseKernelComputesAndCopiesBack()
        {
            RunOutcome o = Run(
                "__global__ void fill(int* a) { int i = threadIdx.x; a[i] = i; }\n" +
                "__global__ void addOne(int* a) { int i = threadIdx.x; a[i] = a[i] + 1; }\n" +
                "int main() {\n" +
                "  int* d; cudaMalloc(&d, 16);\n" +
                "  fill<<<1, 4>>>(d);\n" +
                "  addOne<<<1, 4>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  int h[4];\n" +
                "  cudaMemcpy(h, d, 16, cudaMemcpyDeviceToHost);\n" +
                "  printf(\"%d,%d,%d,%d\\n\", h[0], h[1], h[2], h[3]);\n" +
                "  cudaFree(d);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("1,2,3,4\n", o.Output);
        }

        [TestMethod]
        public void AddressOfLocalScalarEnablesReadback()
        {
            // cudaMemcpy(&h, d, ...) into a LOCAL scalar, then read h — the ubiquitous
            // host-readback idiom. Regression: was 'address-of a non-memory value'.
            RunOutcome o = Run(
                "__global__ void fill(int* a) { a[0] = 42; }\n" +
                "int main() {\n" +
                "  int* d; cudaMalloc(&d, 4);\n" +
                "  fill<<<1, 1>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  int h = 0;\n" +
                "  cudaMemcpy(&h, d, 4, cudaMemcpyDeviceToHost);\n" +
                "  printf(\"%d\\n\", h);\n" +
                "  cudaFree(d);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("42\n", o.Output);
        }

        [TestMethod]
        public void AddressTakenLocalStaysCoherentBothWays()
        {
            // After &x, writes via the pointer are visible by name and vice-versa —
            // the promoted local is one storage location.
            RunOutcome o = Run(
                "int main() {\n" +
                "  int x = 5;\n" +
                "  int* p = &x;\n" +
                "  *p = 9;\n" +            // write via pointer
                "  x = x + 1;\n" +         // read (9) + write via name -> 10
                "  printf(\"%d\\n\", *p);\n" +  // read via pointer -> 10
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("10\n", o.Output);
        }

        [TestMethod]
        public void DeviceGlobalSharedAcrossThreadsAndLaunches()
        {
            // A __device__ global is ONE storage location: atomicAdd from all 16
            // threads of one launch accumulates into it, and it persists into a
            // second kernel launch. Regression for cuda_lab/thread_id_test_matrix.cu.
            RunOutcome o = Run(
                "__device__ int g;\n" +
                "__global__ void inc() { atomicAdd(&g, 1); }\n" +
                "__global__ void show() { printf(\"%d\\n\", g); }\n" +
                "int main() {\n" +
                "  inc<<<1, 16>>>();\n" +
                "  show<<<1, 1>>>();\n" +
                "  cudaDeviceSynchronize();\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("16\n", o.Output);
        }

        [TestMethod]
        public void DeviceGlobalWithInitializerIsReadable()
        {
            // A __device__ global with an initializer keeps its value; a file-scope
            // const is likewise readable from a kernel.
            RunOutcome o = Run(
                "__device__ int base = 100;\n" +
                "const int bonus = 7;\n" +
                "__global__ void k(int* out) { out[0] = base + bonus; }\n" +
                "int main() {\n" +
                "  int* d; cudaMalloc(&d, 4);\n" +
                "  k<<<1, 1>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  int h[1]; cudaMemcpy(h, d, 4, cudaMemcpyDeviceToHost);\n" +
                "  printf(\"%d\\n\", h[0]);\n" +
                "  cudaFree(d);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("107\n", o.Output);
        }

        [TestMethod]
        public void HostMallocAndNewArrayAllocateUsableBuffers()
        {
            // malloc / new[] host buffers must be usable as ordinary memory: written,
            // read back from a device copy, and freed. Regression for cuda_lab kernels
            // that allocate host mirrors (matrix_add_3d malloc, stride_test new[]).
            RunOutcome o = Run(
                "__global__ void sq(int* d) { int i = threadIdx.x; d[i] = i * i; }\n" +
                "int main() {\n" +
                "  int n = 4;\n" +
                "  int* h = (int*)malloc(n * sizeof(int));\n" +
                "  int* h2 = new int[n];\n" +
                "  int* d; cudaMalloc(&d, n * sizeof(int));\n" +
                "  sq<<<1, 4>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  cudaMemcpy(h, d, n * sizeof(int), cudaMemcpyDeviceToHost);\n" +
                "  for (int i = 0; i < n; i++) { h2[i] = h[i] + 1; }\n" +
                "  printf(\"%d,%d,%d,%d\\n\", h2[0], h2[1], h2[2], h2[3]);\n" +
                "  free(h); delete[] h2; cudaFree(d);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("1,2,5,10\n", o.Output);   // (i*i)+1 for i=0..3
        }

        [TestMethod]
        public void SaxpyFloatKernel()
        {
            RunOutcome o = Run(
                "__global__ void saxpy(float a, float* x, float* y) {\n" +
                "  int i = threadIdx.x;\n" +
                "  y[i] = a * x[i] + y[i];\n" +
                "}\n" +
                "int main() {\n" +
                "  float* x; float* y;\n" +
                "  cudaMalloc(&x, 12); cudaMalloc(&y, 12);\n" +
                "  for (int i = 0; i < 3; i++) { x[i] = 1.0f; y[i] = 2.0f; }\n" +
                "  saxpy<<<1, 3>>>(3.0f, x, y);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  float h[3];\n" +
                "  cudaMemcpy(h, y, 12, cudaMemcpyDeviceToHost);\n" +
                "  printf(\"%.1f %.1f %.1f\\n\", h[0], h[1], h[2]);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("5.0 5.0 5.0\n", o.Output);
        }

        [TestMethod]
        public void SharedMemoryReductionWithSyncthreads()
        {
            // Uses __shared__, __syncthreads (threaded path) and atomicAdd.
            RunOutcome o = Run(
                "__global__ void reduce(int* in, int* out) {\n" +
                "  __shared__ int s[4];\n" +
                "  int t = threadIdx.x;\n" +
                "  s[t] = in[t];\n" +
                "  __syncthreads();\n" +
                "  if (t == 0) {\n" +
                "    int sum = 0;\n" +
                "    for (int i = 0; i < 4; i++) { sum = sum + s[i]; }\n" +
                "    atomicAdd(out, sum);\n" +
                "  }\n" +
                "}\n" +
                "int main() {\n" +
                "  int* din; int* dout;\n" +
                "  cudaMalloc(&din, 16); cudaMalloc(&dout, 4);\n" +
                "  cudaMemset(dout, 0, 4);\n" +
                "  for (int i = 0; i < 4; i++) { din[i] = i + 1; }\n" +
                "  reduce<<<1, 4>>>(din, dout);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  int h[1];\n" +
                "  cudaMemcpy(h, dout, 4, cudaMemcpyDeviceToHost);\n" +
                "  printf(\"%d\\n\", h[0]);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("10\n", o.Output);
        }

        [TestMethod]
        public void OutOfBoundsAccessFaults()
        {
            RunOutcome o = Run(
                "__global__ void oob(int* a) { a[100] = 1; }\n" +
                "int main() {\n" +
                "  int* d; cudaMalloc(&d, 16);\n" +
                "  oob<<<1, 1>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsFalse(o.Result.Ok);
            Assert.IsNotNull(o.Result.Error);
            Assert.AreEqual(RuntimeError.Kind.OutOfBounds, o.Result.Error.ErrorKind);
        }

        [TestMethod]
        public void IntegerDivideByZeroFaults()
        {
            RunOutcome o = Run(
                "int main() {\n" +
                "  int a[1]; a[0] = 5;\n" +
                "  int z = 0;\n" +
                "  int r = a[0] / z;\n" +
                "  a[0] = r;\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsFalse(o.Result.Ok);
            Assert.IsNotNull(o.Result.Error);
            Assert.AreEqual(RuntimeError.Kind.DivideByZero, o.Result.Error.ErrorKind);
        }

        [TestMethod]
        public void DivergentBarrierFaults()
        {
            RunOutcome o = Run(
                "__global__ void bad(int* a) {\n" +
                "  if (threadIdx.x < 2) { __syncthreads(); }\n" +
                "}\n" +
                "int main() {\n" +
                "  int* d; cudaMalloc(&d, 16);\n" +
                "  bad<<<1, 4>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsFalse(o.Result.Ok);
            Assert.IsNotNull(o.Result.Error);
            Assert.AreEqual(RuntimeError.Kind.DivergentBarrier, o.Result.Error.ErrorKind);
        }

        [TestMethod]
        public void ConvergentBarrierRunsCleanly()
        {
            // All threads reach the barrier → no divergence.
            RunOutcome o = Run(
                "__global__ void ok(int* a) {\n" +
                "  int t = threadIdx.x;\n" +
                "  a[t] = t;\n" +
                "  __syncthreads();\n" +
                "  a[t] = a[t] + 1;\n" +
                "}\n" +
                "int main() {\n" +
                "  int* d; cudaMalloc(&d, 16);\n" +
                "  ok<<<1, 4>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  int h[4];\n" +
                "  cudaMemcpy(h, d, 16, cudaMemcpyDeviceToHost);\n" +
                "  printf(\"%d%d%d%d\\n\", h[0], h[1], h[2], h[3]);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("1234\n", o.Output);
        }

        [TestMethod]
        public void EmulationStatsRecordsKernelLaunches()
        {
            RunOutcome o = Run(
                "__global__ void k(int* a) { a[threadIdx.x] = threadIdx.x; }\n" +
                "int main() {\n" +
                "  int* d; cudaMalloc(&d, 32);\n" +
                "  k<<<2, 4>>>(d);\n" +
                "  cudaDeviceSynchronize();\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual(1, o.Emu.Stats.Kernels.Count);
            Assert.AreEqual("k", o.Emu.Stats.Kernels[0].Name);
            // grid 2 * block 4 = 8 threads.
            Assert.AreEqual(8UL, o.Emu.Stats.Kernels[0].TotalThreads);
        }

        [TestMethod]
        public void MathIntrinsicAndPrintf()
        {
            RunOutcome o = Run(
                "int main() {\n" +
                "  float r[1];\n" +
                "  r[0] = sqrtf(16.0f);\n" +
                "  printf(\"%.1f\\n\", r[0]);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("4.0\n", o.Output);
        }

        [TestMethod]
        public void CoutString()
        {
            RunOutcome o = Run(
                "#include <iostream>\n" +
                "int main() {\n" +
                "  std::cout << \"hello\";\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("hello", o.Output);
        }

        [TestMethod]
        public void CoutInt()
        {
            RunOutcome o = Run(
                "#include <iostream>\n" +
                "int main() {\n" +
                "  std::cout << 42;\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("42", o.Output);
        }

        [TestMethod]
        public void CoutChainedStringAndInt()
        {
            // Exact bug repro: runtime error (6:5): unsupported construct: reference to 'cout'.
            RunOutcome o = Run(
                "#include <iostream>\n" +
                "int main() {\n" +
                "    std::cout << \"ParticleLab GCC C++ execution succeeded\\n\";\n" +
                "    int my_var = 0;\n" +
                "    std::cout << \"my_var\" << my_var;\n" +
                "    return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual(0, o.Result.ExitCode);
            StringAssert.Contains(o.Output, "ParticleLab GCC C++ execution succeeded");
            // No separator between chained << operands, matching real std::cout.
            StringAssert.Contains(o.Output, "my_var0");
        }

        [TestMethod]
        public void CoutEndl()
        {
            RunOutcome o = Run(
                "#include <iostream>\n" +
                "int main() {\n" +
                "  std::cout << \"a\" << std::endl;\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("a\n", o.Output);
        }

        [TestMethod]
        public void CoutBoolAndDouble()
        {
            RunOutcome o = Run(
                "#include <iostream>\n" +
                "int main() {\n" +
                "  std::cout << true << \" \" << 3.5;\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.IsTrue(o.Output.StartsWith("1"), "output: " + o.Output);
            StringAssert.Contains(o.Output, "3.5");
        }

        [TestMethod]
        public void ShlIntegerRegression()
        {
            // Plain integer left-shift must be unaffected by the cout << special-case.
            RunOutcome o = Run(
                "int main() {\n" +
                "  int x = 1;\n" +
                "  x = x << 2;\n" +
                "  printf(\"%d\\n\", x);\n" +
                "  return 0;\n" +
                "}\n");
            Assert.IsTrue(o.Result.Ok, "run failed: " + (o.Result.Error != null ? o.Result.Error.ErrorMessage : ""));
            Assert.AreEqual("4\n", o.Output);
        }

        [TestMethod]
        public void MissingEntryReportsError()
        {
            CudaCompileResult compiled = CudaVirtualCompiler.CompileSource("__global__ void k() { }\n", "test.cu");
            StringOutputSink sink = new StringOutputSink();
            CudaEmulator emu = new CudaEmulator(compiled, sink);
            EmulationResult res = emu.RunProgram("main");
            Assert.IsFalse(res.Ok);
            Assert.IsNotNull(res.Error);
        }
    }
}
