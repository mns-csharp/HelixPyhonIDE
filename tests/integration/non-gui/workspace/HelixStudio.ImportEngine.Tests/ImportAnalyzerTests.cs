using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.ImportEngine.Analysis;

namespace HelixPythonNamespace.ImportEngine.Tests
{
    [TestClass]
    public class ImportAnalyzerTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_ie_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void W(string relPath, string content = "")
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        [TestMethod]
        public void MissingDirectory_Fails()
        {
            ImportAnalysis a = new ImportAnalyzer().Analyze(Path.Combine(_root, "nope"));
            Assert.IsFalse(a.Success);
            StringAssert.Contains(a.ErrorMessage, "does not exist");
        }

        [TestMethod]
        public void DetectsPackages_Venv_Artifacts_Tests_Docs()
        {
            W(@"pkg_a\__init__.py");
            W(@"pkg_a\mod.py", "x = 1\n");
            W(@".venv\pyvenv.cfg", "home = c:\\python\n");
            W(@".venv\Lib\site.py");
            W(@"build\lib\junk.py");
            W(@"tests\test_mod.py", "def test_x():\n    pass\n");
            W(@"docs\index.rst", "Title\n=====\n");
            W(@"docs\api.rst", "API\n===\n");

            ImportAnalysis a = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(a.Success, a.ErrorMessage);
            Assert.IsTrue(a.Packages.Any(p => p.Name == "pkg_a"));
            Assert.AreEqual(1, a.VirtualEnvironments.Count, "venv detected once, not descended");
            StringAssert.Contains(a.VirtualEnvironments[0].Reason, "pyvenv.cfg");
            Assert.IsTrue(a.BuildArtifacts.Any(d => d.Path.EndsWith("build")));
            Assert.IsTrue(a.TestDirectories.Any(d => d.Path.EndsWith("tests")));
            Assert.IsTrue(a.DocumentationDirectories.Any(d => d.Path.EndsWith("docs")));
            // Nothing inside the venv leaked into any detection.
            Assert.IsFalse(a.NamespaceDirectories.Any(d => d.Path.Contains(".venv")));
            Assert.IsFalse(a.OrphanFiles.Any(f => f.Contains(".venv")));
        }

        [TestMethod]
        public void DetectsNamespaceDirsAndOrphans()
        {
            W(@"pkg\__init__.py");
            W(@"scripts\tool.py", "print('x')\n");  // no __init__.py -> namespace/orphan

            ImportAnalysis a = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(a.Success, a.ErrorMessage);
            Assert.IsTrue(a.NamespaceDirectories.Any(d => d.Path.EndsWith("scripts")));
            Assert.IsTrue(a.OrphanFiles.Any(f => f.EndsWith("tool.py")));
        }

        [TestMethod]
        public void DetectsDuplicatePackageNames()
        {
            W(@"one\core\__init__.py");
            W(@"two\core\__init__.py");

            ImportAnalysis a = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(a.Success, a.ErrorMessage);
            Assert.AreEqual(1, a.DuplicateNames.Count);
            Assert.AreEqual("core", a.DuplicateNames[0].Name);
            Assert.AreEqual(2, a.DuplicateNames[0].DirectoryPaths.Count);
        }

        [TestMethod]
        public void ReportsMultipleMainModulesAndFunctions()
        {
            W(@"app1\__init__.py");
            W(@"app1\main.py", "def main():\n    pass\n");
            W(@"app2\__init__.py");
            W(@"app2\main.py", "def main():\n    pass\n");

            ImportAnalysis a = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(a.Success, a.ErrorMessage);
            Assert.AreEqual(2, a.MainModules.Count);
            Assert.AreEqual(2, a.MainFunctionFiles.Count);
            Assert.IsNotNull(a.AuditReport);
        }

        [TestMethod]
        public void AnalysisIsReadOnly()
        {
            W(@"pkg\__init__.py");
            W(@"pkg\mod.py", "x = 1\n");
            string[] before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .OrderBy(f => f).ToArray();

            new ImportAnalyzer().Analyze(_root);

            string[] after = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .OrderBy(f => f).ToArray();
            CollectionAssert.AreEqual(before, after, "analysis must not create or move files");
        }
    }
}
