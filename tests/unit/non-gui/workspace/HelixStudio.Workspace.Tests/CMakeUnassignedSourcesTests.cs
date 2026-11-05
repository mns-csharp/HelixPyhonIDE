using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The "Unassigned Sources" bucket must hold genuinely orphaned COMPILATION
    /// units only. A header sitting next to a target's own sources belongs to that
    /// target — CMake convention lists only compilation units, so before this rule
    /// every `add_library(x STATIC lib.cpp)` with a sibling lib.h sprouted a
    /// redundant "Unassigned Sources" project in Solution Explorer.
    /// </summary>
    [TestClass]
    public class CMakeUnassignedSourcesTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_cmun_" + Guid.NewGuid().ToString("N"));
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

        [TestMethod]
        public void SiblingHeader_IsAttributedToItsTarget_NotToUnassignedSources()
        {
            // The exact shape "Add New Project → C++ Library into a CUDA solution" produces.
            W("CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\n" +
              "add_executable(GpuApp main.cu)\nadd_subdirectory(\"mylib\")\n");
            W("main.cu", "int main() { return 0; }\n");
            W(@"mylib\CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(mylib LANGUAGES CXX)\n" +
              "add_library(mylib STATIC lib.cpp)\n");   // header deliberately NOT listed
            W(@"mylib\lib.cpp", "#include \"lib.h\"\nint answer() { return 42; }\n");
            W(@"mylib\lib.h", "#pragma once\nint answer();\n");

            CMakeImportResult result = new CMakeProjectImporter().Import(_root);
            Assert.IsTrue(result.Success, result.Error);

            Assert.IsFalse(result.Project.Targets.Any(t => t.Name == CMakeConventions.UnassignedSourcesTargetName),
                "no bucket: the only unclaimed file is a header next to mylib's own source");
            var mylib = result.Project.Targets.Single(t => t.Name == "mylib");
            Assert.IsTrue(mylib.SourceFiles.Any(f => f.EndsWith("lib.h", StringComparison.OrdinalIgnoreCase)),
                "the sibling header is attributed to the target that owns its directory");
        }

        [TestMethod]
        public void OrphanCompilationUnits_AndOwnerlessHeaders_StillLandInTheBucket()
        {
            W("CMakeLists.txt",
              "cmake_minimum_required(VERSION 3.18)\nproject(App LANGUAGES CXX)\nadd_executable(App main.cpp)\n");
            W("main.cpp", "int main() { return 0; }\n");
            W(@"stray\orphan.cpp", "int nobody_builds_me() { return 0; }\n");   // unclaimed compilation unit
            W(@"stray\orphan.h", "#pragma once\n");                             // header with NO owning target in its dir

            CMakeImportResult result = new CMakeProjectImporter().Import(_root);
            Assert.IsTrue(result.Success, result.Error);

            var bucket = result.Project.Targets.SingleOrDefault(
                t => t.Name == CMakeConventions.UnassignedSourcesTargetName);
            Assert.IsNotNull(bucket, "genuinely orphaned files must stay visible");
            Assert.IsTrue(bucket.SourceFiles.Any(f => f.EndsWith("orphan.cpp", StringComparison.OrdinalIgnoreCase)),
                "an unclaimed compilation unit is an honest may-not-build signal");
            Assert.IsTrue(bucket.SourceFiles.Any(f => f.EndsWith("orphan.h", StringComparison.OrdinalIgnoreCase)),
                "a header in a directory no target owns has nowhere better to go");
        }
    }
}
