using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// CUDA vs C++ is a per-PROJECT property persisted in each project's own
    /// CMakeLists (`project(... LANGUAGES ...)`, with .cu/.cuh sources as the
    /// fallback) — not a workspace-wide stamp. Before this rule, a pure C++
    /// library added to a CUDA solution was labeled "CUDA Library".
    /// </summary>
    [TestClass]
    public class CMakeLanguageDerivationTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_cmlang_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void W(string relPath, string content)
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        private SolutionModel Load()
        {
            CMakeImportResult result = new CMakeProjectImporter().Import(_root);
            Assert.IsTrue(result.Success, result.Error);
            return CMakeSolutionLoader.BuildSolution(result.Project);
        }

        [TestMethod]
        public void PureCppProjects_InACudaSolution_AreNotMarkedCuda()
        {
            // The reported shape: CUDA executable at the root, plain C++ library added below it.
            W("CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\n" +
              "add_executable(GpuApp main.cu)\nadd_subdirectory(\"cpp_lib\")\n");
            W("main.cu", "int main() { return 0; }\n");
            W(@"cpp_lib\CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(cpp_lib LANGUAGES CXX)\n" +
              "add_library(cpp_lib STATIC lib.cpp lib.h)\n");
            W(@"cpp_lib\lib.cpp", "int answer() { return 42; }\n");
            W(@"cpp_lib\lib.h", "#pragma once\nint answer();\n");

            SolutionModel sln = Load();
            var exe = sln.Projects.Single(p => p.Name == "GpuApp");
            var lib = sln.Projects.Single(p => p.Name == "cpp_lib");

            Assert.IsTrue(exe.HasCudaSources, "root project declares LANGUAGES CUDA and owns main.cu");
            Assert.IsFalse(lib.HasCudaSources,
                "the C++ library's own CMakeLists declares LANGUAGES CXX only — it must not inherit CUDA");
            Assert.AreEqual("CUDA C++", exe.TargetFramework);
            Assert.AreEqual("C++", lib.TargetFramework);
        }

        [TestMethod]
        public void GtestProject_SurfacesItsTestMacros_AsVisibleSymbols()
        {
            // Regression for "I see no scaffold for the unit tests": TEST(...) is a macro the
            // C++ parser can't see as a function, so a gtest file contributed zero symbols and
            // (file nodes being symbol-derived) was invisible in Solution Explorer.
            W("CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(App LANGUAGES CXX)\n" +
              "add_executable(App main.cpp)\nadd_subdirectory(\"my_tests\")\n");
            W("main.cpp", "int main() { return 0; }\n");
            W(@"my_tests\CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(my_tests LANGUAGES CXX)\n" +
              "add_executable(my_tests test_main.cpp)\n");
            W(@"my_tests\test_main.cpp",
              "#include <gtest/gtest.h>\n\nTEST(Math, Addition) {\n    EXPECT_EQ(1 + 1, 2);\n}\n\n" +
              "TEST_F(Fixture, Subtraction) {\n    EXPECT_EQ(2 - 1, 1);\n}\n");

            SolutionModel sln = Load();
            var tests = sln.Projects.Single(p => p.Name == "my_tests");
            Assert.IsTrue(tests.Functions.Any(f => f.Name == "TEST(Math, Addition)"),
                "gtest TEST macros surface as functions; got: " +
                string.Join(", ", tests.Functions.Select(f => f.Name)));
            Assert.IsTrue(tests.Functions.Any(f => f.Name == "TEST_F(Fixture, Subtraction)"));
        }

        [TestMethod]
        public void CuSources_MarkCuda_EvenWithoutALanguagesClause()
        {
            // Fallback: a hand-written CMakeLists with no LANGUAGES clause but real kernels.
            W("CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(Kern)\nadd_library(Kern STATIC kern.cu)\n");
            W("kern.cu", "__global__ void k() {}\n");

            SolutionModel sln = Load();
            Assert.IsTrue(sln.Projects.Single(p => p.Name == "Kern").HasCudaSources,
                ".cu sources are the persisted CUDA signal when no language clause exists");
        }
    }
}
