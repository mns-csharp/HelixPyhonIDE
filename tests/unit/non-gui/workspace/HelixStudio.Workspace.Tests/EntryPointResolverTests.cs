using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.LanguageServices;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// "What does Run launch?" used to be a two-way choice: native projects resolved through the
    /// CMake target graph and EVERYTHING ELSE was assumed to be Python — so a project of any other
    /// language was told it "needs a main.py with a def main()". Each language now answers for
    /// itself, and a language with no rule refuses rather than borrowing Python's. HelixPython
    /// ships Python plus the C++/CUDA it converts to, so the surviving arms are: Python resolves,
    /// Cpp defers to its CMake target graph, and neither borrows the other's advice.
    /// </summary>
    [TestClass]
    public class EntryPointResolverTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "helix_entry_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private ProjectModel Project(ProjectLanguage language, string manifestName,
                                     ProjectKind kind = ProjectKind.App, string name = "demo")
        {
            string manifest = Path.Combine(_dir, manifestName);
            if (!File.Exists(manifest)) File.WriteAllText(manifest, string.Empty);
            var sln = new SolutionModel(manifest, name);
            var p = new ProjectModel(manifest, name, sln) { Language = language, Kind = kind };
            sln.Projects.Add(p);
            return p;
        }

        private void Write(string relative, string content)
        {
            string full = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        [TestMethod]
        public void Python_ExecutablePackage_ResolvesItsMainPy()
        {
            Write("pyproject.toml", "[project]\nname = \"demo\"\n\n[tool.helixstudio]\nkind = \"app\"\n");
            Write("main.py", "def main():\n    pass\n\n\nif __name__ == \"__main__\":\n    main()\n");

            string entry;
            Assert.IsTrue(EntryPointResolver.TryFindEntryFile(Project(ProjectLanguage.Python, "pyproject.toml"), out entry),
                          "a Python app with a main.py has an entry point");
            Assert.AreEqual("main.py", Path.GetFileName(entry));
        }

        [TestMethod]
        public void Cpp_DefersToItsCMakeTargetGraph_RatherThanGuessing()
        {
            // A CMake target's entry point is decided by the target graph, not by a file scan, so
            // this resolver deliberately answers "no" instead of nominating a plausible .cpp. The
            // regression it guards is the opposite: falling through to the Python arm and pointing
            // a native project at a main.py it must not have.
            Write("CMakeLists.txt", "cmake_minimum_required(VERSION 3.18)\nproject(demo)\n");
            Write("main.cpp", "int main() { return 0; }\n");

            string entry;
            Assert.IsFalse(EntryPointResolver.TryFindEntryFile(Project(ProjectLanguage.Cpp, "CMakeLists.txt", ProjectKind.CudaExecutable), out entry));
            Assert.IsNull(entry);
        }

        [TestMethod]
        public void AdviceIsPhrasedInTheProjectsOwnLanguage()
        {
            StringAssert.Contains(EntryPointResolver.MissingEntryPointAdvice(ProjectLanguage.Python), "main.py");

            // The bug in one line: a non-Python project must never be told to add a main.py.
            // Cpp has no advice of its own, and null is the honest answer — not Python's.
            Assert.IsNull(EntryPointResolver.MissingEntryPointAdvice(ProjectLanguage.Cpp));
        }
    }
}
