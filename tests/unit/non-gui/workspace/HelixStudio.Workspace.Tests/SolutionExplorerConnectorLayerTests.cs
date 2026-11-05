using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.LanguageServices;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The CodebaseSolutionToSolutionExplorerPanelConnector layer's Workspace half: the
    /// language providers this product ships, their descriptors' explorer traits, and the
    /// detection routing that keeps Python and CMake from claiming each other's
    /// workspaces. Mirrors <see cref="LanguageWorkspaceLayerTests"/>' style: real temp
    /// fixtures, never-throw semantics, one pin per historical inline decision.
    /// </summary>
    [TestClass]
    public class SolutionExplorerConnectorLayerTests
    {
        private string _root;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "hs-connector-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { /* best-effort */ }
        }

        // ---------------------------------------------------------------- descriptors

        [TestMethod]
        public void Descriptors_ExposeOnlyBackedVerbs()
        {
            // Python and C++ keep the tree's historical icons — no per-language set.
            Assert.IsNull(LanguageDescriptors.For(ProjectLanguage.Python).ExplorerIcons);
            Assert.IsNull(LanguageDescriptors.For(ProjectLanguage.Cpp).ExplorerIcons);
            // The C++ debug-tests fix: the verb used to appear and launch pytest on .cpp dirs.
            Assert.IsFalse(LanguageDescriptors.For(ProjectLanguage.Cpp).SupportsDebugTests);
            Assert.IsTrue(LanguageDescriptors.For(ProjectLanguage.Python).SupportsDebugTests);
        }


        // ---------------------------------------------------------------- detection routing

        [TestMethod]
        public void TryDetect_HistoricalRouting_IsUnchangedByTheNewProviders()
        {
            // A pyproject file still goes to Python, and a bare pyproject-bearing directory is
            // still Import-wizard territory (no directory-open for Python).
            string toml = Path.Combine(_root, "pyproject.toml");
            File.WriteAllText(toml, "[project]\nname = \"demo\"\n");
            Assert.AreEqual(ProjectLanguage.Python, LanguageWorkspaceRegistry.TryDetect(toml).Language);
            Assert.IsNull(LanguageWorkspaceRegistry.TryDetect(_root));

            // CMake beats everything in a mixed directory (fixed detection order).
            File.WriteAllText(Path.Combine(_root, "CMakeLists.txt"), "project(x)\n");
            File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname = \"x\"\n");
            Assert.AreEqual(ProjectLanguage.Cpp, LanguageWorkspaceRegistry.TryDetect(_root).Language);
        }

        // ---------------------------------------------------------------- end-to-end opens

    }
}
