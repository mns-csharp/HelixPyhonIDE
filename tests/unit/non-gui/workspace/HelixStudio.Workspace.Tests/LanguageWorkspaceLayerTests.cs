using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.LanguageServices;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The language-services layer: per-language workspace recognition/loading behind
    /// <see cref="LanguageWorkspaceRegistry"/>, and language-intrinsic explorer rules behind
    /// <see cref="LanguageDescriptors"/>. These tests pin the dispatch semantics that used to be
    /// inline in MainForm.IsProjectPath / SolutionCoordinator.OpenSolutionAsync, so a change to
    /// one language's arm shows up here before it can break another language's arm.
    /// </summary>
    [TestClass]
    public class LanguageWorkspaceLayerTests
    {
        private string _root;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "hs-langsvc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { /* best-effort */ }
        }

        // ---------------------------------------------------------------- descriptors

        [TestMethod]
        public void Descriptors_ExistForEveryProjectLanguage_AndNeverReturnNull()
        {
            foreach (ProjectLanguage lang in Enum.GetValues(typeof(ProjectLanguage)))
            {
                LanguageWorkspaceDescriptor d = LanguageDescriptors.For(lang);
                Assert.IsNotNull(d, lang + " must have a descriptor");
                Assert.AreEqual(lang, d.Language);
            }
        }

        [TestMethod]
        public void Descriptors_PythonAndCpp_DisagreeWhereTheLanguagesDo()
        {
            LanguageWorkspaceDescriptor py = LanguageDescriptors.For(ProjectLanguage.Python);
            LanguageWorkspaceDescriptor cpp = LanguageDescriptors.For(ProjectLanguage.Cpp);

            // Python: namespace == package directory → package rename is real; package-manager
            // verbs are live and keep their historical labels.
            Assert.IsTrue(py.SupportsPackageRename);
            Assert.IsTrue(py.SupportsPackageManager);
            Assert.IsNotNull(py.RestorePackagesMenuLabel);
            Assert.IsNotNull(py.ManagePackagesMenuLabel);
            CollectionAssert.Contains(py.SolutionManifestFileNames, "pyproject.toml");
            CollectionAssert.Contains(py.SolutionFileExtensions, ".sln");

            // C++: no namespace↔directory convention, no wired package manager — the verbs must
            // be hidden, not mislabeled.
            Assert.IsFalse(cpp.SupportsPackageRename);
            Assert.IsFalse(cpp.SupportsPackageManager);
            CollectionAssert.Contains(cpp.SolutionManifestFileNames, "CMakeLists.txt");
        }

        // ---------------------------------------------------------------- name-only recognition

        // ---------------------------------------------------------------- disk-aware detection

        [TestMethod]
        public void TryDetect_CMakeDirectoryAndFile_GoToTheCppProvider()
        {
            string cmake = Path.Combine(_root, "CMakeLists.txt");
            File.WriteAllText(cmake,
                "cmake_minimum_required(VERSION 3.20)\nproject(demo LANGUAGES CXX)\nadd_executable(app main.cpp)\n");
            File.WriteAllText(Path.Combine(_root, "main.cpp"), "int main() { return 0; }\n");

            ILanguageWorkspaceProvider byDir = LanguageWorkspaceRegistry.TryDetect(_root);
            ILanguageWorkspaceProvider byFile = LanguageWorkspaceRegistry.TryDetect(cmake);
            Assert.IsNotNull(byDir, "a CMakeLists.txt-bearing directory is a C++ workspace");
            Assert.AreEqual(ProjectLanguage.Cpp, byDir.Language);
            Assert.IsNotNull(byFile);
            Assert.AreEqual(ProjectLanguage.Cpp, byFile.Language);
        }

        [TestMethod]
        public void TryDetect_PyprojectFile_GoesToThePythonProvider_ButBareDirectoryDoesNot()
        {
            string toml = Path.Combine(_root, "pyproject.toml");
            File.WriteAllText(toml, "[project]\nname = \"demo\"\n");

            ILanguageWorkspaceProvider byFile = LanguageWorkspaceRegistry.TryDetect(toml);
            Assert.IsNotNull(byFile);
            Assert.AreEqual(ProjectLanguage.Python, byFile.Language);

            // A directory holding a pyproject.toml is NOT directly openable — that path belongs
            // to the Import wizard (only a CMake folder opens as a directory).
            Assert.IsNull(LanguageWorkspaceRegistry.TryDetect(_root));
        }

        [TestMethod]
        public void TryDetect_UnknownOrMissingPaths_ReturnNull()
        {
            Assert.IsNull(LanguageWorkspaceRegistry.TryDetect(Path.Combine(_root, "nope", "CMakeLists.txt")));
            Assert.IsNull(LanguageWorkspaceRegistry.TryDetect(Path.Combine(_root, "readme.md")));
            Assert.IsNull(LanguageWorkspaceRegistry.TryDetect(null));
        }

        // ---------------------------------------------------------------- end-to-end opens

        [TestMethod]
        public void PythonProvider_Opens_APyprojectSolution()
        {
            string toml = Path.Combine(_root, "pyproject.toml");
            File.WriteAllText(toml, "[project]\nname = \"demo\"\nrequires-python = \">=3.12\"\n");
            string pkg = Path.Combine(_root, "src", "demo");
            Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, "shapes.py"), "class Circle:\n    def area(self):\n        return 0\n");

            WorkspaceOpenResult res = LanguageWorkspaceRegistry.For(ProjectLanguage.Python)
                .OpenAsync(toml).GetAwaiter().GetResult();

            Assert.IsTrue(res.Success, res.ErrorMessage);
            Assert.IsNotNull(res.Solution);
            Assert.AreEqual(1, res.Solution.Projects.Count);
            Assert.AreEqual(ProjectLanguage.Python, res.Solution.Projects[0].Language);
            Assert.IsNull(res.NativeProject, "Python has no native payload");
        }

        [TestMethod]
        public void CppProvider_Opens_ACMakeWorkspace_WithNativeDescriptorPayload()
        {
            File.WriteAllText(Path.Combine(_root, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.20)\nproject(demo LANGUAGES CXX)\nadd_executable(app main.cpp)\n");
            File.WriteAllText(Path.Combine(_root, "main.cpp"),
                "class Greeter { public: int greet() { return 1; } };\nint main() { return 0; }\n");

            WorkspaceOpenResult res = LanguageWorkspaceRegistry.For(ProjectLanguage.Cpp)
                .OpenAsync(_root).GetAwaiter().GetResult();

            Assert.IsTrue(res.Success, res.ErrorMessage);
            Assert.IsNotNull(res.Solution);
            Assert.IsTrue(res.Solution.Projects.Count >= 1, "one ProjectModel per CMake target");
            Assert.AreEqual(ProjectLanguage.Cpp, res.Solution.Projects[0].Language);
            Assert.IsInstanceOfType(res.NativeProject, typeof(HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor),
                "the App layer needs the CMake descriptor for real-toolchain build/run");
        }

        [TestMethod]
        public void CppProvider_RejectsNonCMakePaths_WithAFailedResult_NotAThrow()
        {
            WorkspaceOpenResult res = LanguageWorkspaceRegistry.For(ProjectLanguage.Cpp)
                .OpenAsync(Path.Combine(_root, "not-there")).GetAwaiter().GetResult();
            Assert.IsFalse(res.Success);
            Assert.IsNotNull(res.ErrorMessage);
        }
    }
}
