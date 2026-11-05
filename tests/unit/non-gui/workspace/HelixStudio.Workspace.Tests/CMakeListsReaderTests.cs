using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CMakeListsReaderTests
    {
        // Mirrors the real cuda_lab fixture: variables, multi-line source lists,
        // ${VAR} references in add_executable, a subdirectory, and Polish comments.
        private const string RealisticCMake =
            "cmake_minimum_required(VERSION 3.8)\n" +
            "project(cuda_lab)   #nazwa projektu\n" +
            "\n" +
            "#standard C++ 14\n" +
            "set(CMAKE_CXX_STANDARD 14)\n" +
            "\n" +
            "include_directories(include\n" +
            "        googletest-master/googletest/include)\n" +
            "\n" +
            "set(SOURCE_FILES\n" +
            "        src/matlab.cpp)\n" +
            "\n" +
            "add_subdirectory(googletest-master)\n" +
            "\n" +
            "add_executable(exe_run ${SOURCE_FILES} main.cpp)\n";

        [TestMethod]
        public void ParsesProjectAndMinimumVersion()
        {
            var m = CMakeListsReader.Parse(RealisticCMake);
            Assert.AreEqual("3.8", m.CMakeMinimumVersion);
            Assert.AreEqual("cuda_lab", m.ProjectName);
        }

        [TestMethod]
        public void ParsesCxxStandardFromSet()
        {
            var m = CMakeListsReader.Parse(RealisticCMake);
            Assert.AreEqual("14", m.CxxStandard);
        }

        [TestMethod]
        public void ExpandsVariableReferenceInExecutableSources()
        {
            var m = CMakeListsReader.Parse(RealisticCMake);
            var exe = m.Targets.Single(t => t.Name == "exe_run");
            Assert.AreEqual(CMakeTargetKind.Executable, exe.Kind);
            // ${SOURCE_FILES} expanded to src/matlab.cpp, then the literal main.cpp.
            CollectionAssert.AreEqual(new[] { "src/matlab.cpp", "main.cpp" }, exe.ResolvedSources.ToArray());
            // Sources-as-written keep the ${...} form.
            CollectionAssert.Contains(exe.Sources, "${SOURCE_FILES}");
        }

        [TestMethod]
        public void CapturesIncludeDirectoriesAndSubdirectory()
        {
            var m = CMakeListsReader.Parse(RealisticCMake);
            CollectionAssert.Contains(m.IncludeDirectories, "include");
            CollectionAssert.Contains(m.IncludeDirectories, "googletest-master/googletest/include");
            CollectionAssert.Contains(m.Subdirectories, "googletest-master");
        }

        [TestMethod]
        public void ParsesLibraryKindAndLanguages()
        {
            string src =
                "cmake_minimum_required(VERSION 3.20)\n" +
                "project(demo LANGUAGES CUDA CXX)\n" +
                "set(CMAKE_CUDA_ARCHITECTURES 75;86)\n" +
                "add_library(mathkernels STATIC ops.cu util.cu)\n" +
                "add_executable(app main.cu)\n";
            var m = CMakeListsReader.Parse(src);
            CollectionAssert.AreEquivalent(new[] { "CUDA", "CXX" }, m.Languages.ToArray());
            Assert.AreEqual("75;86", m.CudaArchitectures);
            Assert.IsTrue(m.UsesCuda, "CUDA language + .cu sources => UsesCuda");
            var lib = m.Targets.Single(t => t.Name == "mathkernels");
            Assert.AreEqual(CMakeTargetKind.StaticLibrary, lib.Kind);
            CollectionAssert.AreEqual(new[] { "ops.cu", "util.cu" }, lib.ResolvedSources.ToArray());
        }

        [TestMethod]
        public void ParsesAndRoundTripsLinkDependencies()
        {
            string src =
                "cmake_minimum_required(VERSION 3.20)\nproject(demo LANGUAGES CXX)\n" +
                "add_library(core STATIC core.cpp)\nadd_executable(app main.cpp)\n" +
                "target_link_libraries(app PRIVATE core pthread)\n";
            var m = CMakeListsReader.Parse(src);
            var app = m.Targets.Single(t => t.Name == "app");
            CollectionAssert.AreEquivalent(new[] { "core", "pthread" }, app.LinkDependencies.ToArray());

            // Round-trip: writer emits target_link_libraries, reader re-parses the same deps.
            var m2 = CMakeListsReader.Parse(CMakeListsWriter.WriteToString(m));
            var app2 = m2.Targets.Single(t => t.Name == "app");
            CollectionAssert.AreEquivalent(new[] { "core", "pthread" }, app2.LinkDependencies.ToArray());
        }

        [TestMethod]
        public void RoundTripsThroughWriter()
        {
            var m1 = CMakeListsReader.Parse(RealisticCMake);
            string emitted = CMakeListsWriter.WriteToString(m1);
            var m2 = CMakeListsReader.Parse(emitted);

            Assert.AreEqual(m1.CMakeMinimumVersion, m2.CMakeMinimumVersion);
            Assert.AreEqual(m1.ProjectName, m2.ProjectName);
            Assert.AreEqual(m1.CxxStandard, m2.CxxStandard);
            Assert.AreEqual(m1.Targets.Count, m2.Targets.Count);
            var e1 = m1.Targets.Single(t => t.Name == "exe_run");
            var e2 = m2.Targets.Single(t => t.Name == "exe_run");
            CollectionAssert.AreEqual(e1.ResolvedSources.ToArray(), e2.ResolvedSources.ToArray());
            CollectionAssert.Contains(m2.Subdirectories, "googletest-master");
        }
    }
}
