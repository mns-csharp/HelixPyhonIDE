using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Cuda;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Project/package references persist in each language's NATIVE manifest form — the
    /// same element the language's loader and real toolchain consume — so a reference
    /// added in the IDE survives reload (they used to be in-memory only, all languages).
    /// </summary>
    [TestClass]
    public class ReferenceWritersTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_refw_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string W(string relPath, string content)
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            return full;
        }

        [TestMethod]
        public void CMake_TargetLink_AppendsOnce_AgainstThePrimaryTarget()
        {
            string cmake = W(@"gpu\CMakeLists.txt",
                "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\nadd_executable(GpuApp main.cu)\n");
            Assert.IsTrue(CMakeReferenceWriter.AddTargetLink(cmake, "cpp_lib"));
            Assert.IsFalse(CMakeReferenceWriter.AddTargetLink(cmake, "cpp_lib"), "idempotent");
            StringAssert.Contains(File.ReadAllText(cmake), "target_link_libraries(GpuApp PRIVATE cpp_lib)");
        }

        [TestMethod]
        public void EveryWriter_CanAlsoRemoveWhatItAdded()
        {
            // References were add-only ("can add but can't remove") — every writer HelixPython
            // ships now removes. (csproj/cargo/maven/julia writers moved to their own products.)
            string cmake = W(@"rm\gpu\CMakeLists.txt",
                "cmake_minimum_required(VERSION 3.18)\nproject(Gpu LANGUAGES CXX)\nadd_executable(Gpu main.cpp)\n");
            CMakeReferenceWriter.AddTargetLink(cmake, "cpp_lib");
            Assert.IsTrue(CMakeReferenceWriter.RemoveTargetLink(cmake, "cpp_lib"));
            Assert.IsFalse(File.ReadAllText(cmake).Contains("cpp_lib"));

            string py = W(@"rm\pyapp\pyproject.toml", "[project]\nname = \"pyapp\"\n");
            PyProjectReferenceWriter.SetReferences(py, new[] { "a", "b" });
            PyProjectReferenceWriter.SetReferences(py, new[] { "a" });   // removal = rewrite the list
            string pyText = File.ReadAllText(py);
            StringAssert.Contains(pyText, "references = [\"a\"]");
            Assert.IsFalse(pyText.Contains("\"b\""));
        }

        [TestMethod]
        public void RootProject_MemberScanExclusion_IsNotAUserVisibleExclusion()
        {
            // Reported: an ADDED member project showed under the root project's "Excluded
            // Files" node. Member subtrees are a scan concern, never ExcludedPaths content.
            string rootToml = W(@"excl\pyproject.toml",
                "[project]\nname = \"root\"\n\n[tool.uv.workspace]\nmembers = [\"mylib\"]\n\n" +
                "[tool.helixstudio]\nkind = \"app\"\nstartup = true\n");
            W(@"excl\main.py", "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");
            W(@"excl\mylib\pyproject.toml", "[project]\nname = \"mylib\"\n\n[tool.helixstudio]\nkind = \"library\"\n");
            W(@"excl\mylib\util.py", "class U:\n    pass\n");

            var load = new HelixPythonNamespace.Workspace.Services.SolutionService().LoadAsync(rootToml);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            ProjectModel root = load.Result.Solution.Projects.Single(p => p.Name == "root");
            ProjectModel mylib = load.Result.Solution.Projects.Single(p => p.Name == "mylib");

            Assert.IsFalse((root.ExcludedPaths ?? new System.Collections.Generic.List<string>())
                    .Any(x => x.Contains("mylib")),
                "the member is scan-excluded, NOT user-excluded — no Excluded Files entry");
            Assert.IsFalse(root.Types.Any(t => t.Name == "U"), "root still must not double-claim member sources");
            Assert.IsTrue(mylib.Types.Any(t => t.Name == "U"));
        }

        [TestMethod]
        public void Python_References_RoundTripThroughReaderAndSolutionLoad()
        {
            string rootToml = W("pyproject.toml",
                "[project]\nname = \"ws\"\n\n[tool.uv.workspace]\nmembers = [\"app\", \"mylib\"]\n");
            string appToml = W(@"app\pyproject.toml",
                "[project]\nname = \"app\"\n\n[tool.helixstudio]\nkind = \"app\"\n");
            W(@"app\main.py", "def main():\n    pass\n");
            W(@"mylib\pyproject.toml", "[project]\nname = \"mylib\"\n\n[tool.helixstudio]\nkind = \"library\"\n");
            W(@"mylib\util.py", "class U:\n    pass\n");

            PyProjectReferenceWriter.SetReferences(appToml, new[] { "mylib" });
            StringAssert.Contains(File.ReadAllText(appToml), "references = [\"mylib\"]");

            PyProjectMetadata meta = PyProjectReader.Read(appToml);
            CollectionAssert.AreEqual(new[] { "mylib" }, meta.References);

            var load = new HelixPythonNamespace.Workspace.Services.SolutionService().LoadAsync(rootToml);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            ProjectModel app = load.Result.Solution.Projects.Single(p => p.Name == "app");
            Assert.IsTrue(app.ProjectReferences.Any(r => r.Name == "mylib"),
                "the persisted reference resolves to a ProjectReferences edge on load");
        }
    }
}
