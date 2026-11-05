using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PythonProjectScaffolderTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_scaffold_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        [TestMethod]
        public void ClassLibraryScaffolderWritesMinimalFilesAndLibraryKind()
        {
            SolutionModel sln = PythonProjectScaffolder.CreateNewProject(_root, "MyLib", ProjectKind.ClassLibrary);
            Assert.AreEqual(1, sln.Projects.Count);
            Assert.AreEqual(ProjectKind.ClassLibrary, sln.Projects[0].Kind);

            string pyproject = Path.Combine(_root, "pyproject.toml");
            Assert.IsTrue(File.Exists(pyproject));
            string toml = File.ReadAllText(pyproject);
            StringAssert.Contains(toml, "kind = \"library\"");

            Assert.IsTrue(File.Exists(Path.Combine(_root, "mylib", "__init__.py")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "mylib", "core.py")));
            Assert.IsFalse(File.Exists(Path.Combine(_root, "mylib", "__main__.py")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "src")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "README.md")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, ".gitignore")));
        }

        [TestMethod]
        public void ScaffolderEmitsNoTestsFolder_SoTheNewProjectIsNotBornBlocked()
        {
            // Solution policy rule 3 (PY8003) bars every kind except a test project from
            // containing test files. The scaffolder used to write `tests/test_<name>.py` into the
            // project itself, which under that rule would hand the user a brand-new solution that
            // cannot compile, run or debug. Tests belong in a sibling test project instead.
            PythonProjectScaffolder.CreateNewProject(_root, "MyLib", ProjectKind.ClassLibrary);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "tests")),
                "a scaffolded project must not be born in violation of the policy it is judged by");
            Assert.AreEqual(0,
                Directory.GetFiles(_root, "test_*.py", SearchOption.AllDirectories).Length);
        }

        [TestMethod]
        public void AppScaffolderWritesMainPyAndAppKind()
        {
            SolutionModel sln = PythonProjectScaffolder.CreateNewProject(_root, "MyApp", ProjectKind.App);
            Assert.AreEqual(ProjectKind.App, sln.Projects[0].Kind);

            string toml = File.ReadAllText(Path.Combine(_root, "pyproject.toml"));
            StringAssert.Contains(toml, "kind = \"app\"");

            string mainPy = Path.Combine(_root, "main.py");
            Assert.IsTrue(File.Exists(mainPy));
            string mainContent = File.ReadAllText(mainPy);
            StringAssert.Contains(mainContent, "def main()");
            StringAssert.Contains(mainContent, "if __name__ == \"__main__\":");
            StringAssert.Contains(mainContent, "raise SystemExit(main())");
        }

        [TestMethod]
        public void FlaskScaffolderWritesAppPyTemplatesAndFlaskDep()
        {
            SolutionModel sln = PythonProjectScaffolder.CreateNewProject(_root, "MyWeb", ProjectKind.FlaskWebApp);
            Assert.AreEqual(ProjectKind.FlaskWebApp, sln.Projects[0].Kind);

            string toml = File.ReadAllText(Path.Combine(_root, "pyproject.toml"));
            StringAssert.Contains(toml, "kind = \"flask\"");
            StringAssert.Contains(toml, "flask>=3.0");

            string appPy = Path.Combine(_root, "myweb", "app.py");
            Assert.IsTrue(File.Exists(appPy));
            string appContent = File.ReadAllText(appPy);
            StringAssert.Contains(appContent, "from flask import Flask");
            Assert.IsFalse(appContent.Contains("def main("), "Flask scaffolder must not emit def main().");

            Assert.IsTrue(File.Exists(Path.Combine(_root, "myweb", "templates", "index.html")));
        }

        [TestMethod]
        public void KindRoundtripsThroughPyProject()
        {
            PythonProjectScaffolder.CreateNewProject(_root, "MyApp", ProjectKind.App);
            PyProjectMetadata meta = PyProjectReader.Read(Path.Combine(_root, "pyproject.toml"));
            string tok;
            Assert.IsTrue(meta.ToolHelixStudio.TryGetValue("kind", out tok));
            Assert.AreEqual(ProjectKind.App, ProjectKinds.FromToken(tok));
        }

        [TestMethod]
        public void TomlFormatAlwaysWritesBothFiles()
        {
            SolutionModel sln = PythonProjectScaffolder.CreateNewProject(_root, "MyLib", ProjectKind.ClassLibrary, SolutionFormat.Toml);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "pyproject.toml")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "mylib.sln")));
            StringAssert.EndsWith(sln.FilePath, "pyproject.toml");
        }

        [TestMethod]
        public void SlnFormatAlwaysWritesBothFilesAndReturnsSlnAnchor()
        {
            SolutionModel sln = PythonProjectScaffolder.CreateNewProject(_root, "MyLib", ProjectKind.ClassLibrary, SolutionFormat.Sln);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "pyproject.toml")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "mylib.sln")));
            StringAssert.EndsWith(sln.FilePath, "mylib.sln");
            // The ProjectModel.FilePath still points at pyproject.toml (metadata lives there).
            StringAssert.EndsWith(sln.Projects[0].FilePath, "pyproject.toml");
        }

        [TestMethod]
        public void ScaffolderEmitsRealPyProjAndReferencesItFromSln()
        {
            PythonProjectScaffolder.CreateNewProject(_root, "MyApp", ProjectKind.App);
            string pyprojPath = Path.Combine(_root, "myapp", "myapp.pyproj");
            Assert.IsTrue(File.Exists(pyprojPath), ".pyproj must be emitted per package");

            string pyproj = File.ReadAllText(pyprojPath);
            StringAssert.Contains(pyproj, "<Project");
            StringAssert.Contains(pyproj, "<Name>myapp</Name>");
            StringAssert.Contains(pyproj, "<Compile Include=\"__init__.py\"");
            StringAssert.Contains(pyproj, "<Compile Include=\"..\\main.py\"");

            string sln = File.ReadAllText(Path.Combine(_root, "myapp.sln"));
            // PTVS project-type GUID + the .pyproj path must both appear in the .sln.
            StringAssert.Contains(sln, "{888888A0-9F3D-457C-B088-3A5042F75D52}");
            StringAssert.Contains(sln, "myapp.pyproj");
        }

        [TestMethod]
        public void FlaskScaffolderEmitsTemplatesFolderInPyProj()
        {
            PythonProjectScaffolder.CreateNewProject(_root, "MyWeb", ProjectKind.FlaskWebApp);
            string pyproj = File.ReadAllText(Path.Combine(_root, "myweb", "myweb.pyproj"));
            StringAssert.Contains(pyproj, "<Compile Include=\"app.py\"");
            StringAssert.Contains(pyproj, "<Folder Include=\"templates\\\"");
        }

        [TestMethod]
        public void TkinterScaffolderWritesMainAndNoDeps()
        {
            SolutionModel sln = PythonProjectScaffolder.CreateNewProject(
                _root, "MyGui", ProjectKind.GuiApp, SolutionFormat.Toml, GuiFramework.Tkinter);
            Assert.AreEqual(ProjectKind.GuiApp, sln.Projects[0].Kind);

            string toml = File.ReadAllText(Path.Combine(_root, "pyproject.toml"));
            StringAssert.Contains(toml, "kind = \"gui\"");
            StringAssert.Contains(toml, "gui-framework = \"tkinter\"");
            StringAssert.Contains(toml, "dependencies = []");

            string main = File.ReadAllText(Path.Combine(_root, "main.py"));
            StringAssert.Contains(main, "import tkinter");
            StringAssert.Contains(main, "def main()");
            StringAssert.Contains(main, "if __name__ == \"__main__\":");
        }

        [TestMethod]
        public void PySide6ScaffolderWritesMainAndDep()
        {
            PythonProjectScaffolder.CreateNewProject(
                _root, "MyGui", ProjectKind.GuiApp, SolutionFormat.Toml, GuiFramework.PySide6);

            string toml = File.ReadAllText(Path.Combine(_root, "pyproject.toml"));
            StringAssert.Contains(toml, "gui-framework = \"pyside6\"");
            StringAssert.Contains(toml, "pyside6>=6.6");

            string main = File.ReadAllText(Path.Combine(_root, "main.py"));
            StringAssert.Contains(main, "from PySide6.QtWidgets import");
        }

        [TestMethod]
        public void PyQt6ScaffolderWritesMainAndDep()
        {
            PythonProjectScaffolder.CreateNewProject(
                _root, "MyGui", ProjectKind.GuiApp, SolutionFormat.Toml, GuiFramework.PyQt6);

            string toml = File.ReadAllText(Path.Combine(_root, "pyproject.toml"));
            StringAssert.Contains(toml, "gui-framework = \"pyqt6\"");
            StringAssert.Contains(toml, "pyqt6>=6.6");

            string main = File.ReadAllText(Path.Combine(_root, "main.py"));
            StringAssert.Contains(main, "from PyQt6.QtWidgets import");
        }

        [TestMethod]
        public void WxPythonScaffolderWritesMainAndDep()
        {
            PythonProjectScaffolder.CreateNewProject(
                _root, "MyGui", ProjectKind.GuiApp, SolutionFormat.Toml, GuiFramework.WxPython);

            string toml = File.ReadAllText(Path.Combine(_root, "pyproject.toml"));
            StringAssert.Contains(toml, "gui-framework = \"wxpython\"");
            StringAssert.Contains(toml, "wxpython>=4.2");

            string main = File.ReadAllText(Path.Combine(_root, "main.py"));
            StringAssert.Contains(main, "import wx");
        }

        [TestMethod]
        public void GuiAppKindRoundtripsThroughPyProject()
        {
            PythonProjectScaffolder.CreateNewProject(
                _root, "MyGui", ProjectKind.GuiApp, SolutionFormat.Toml, GuiFramework.PySide6);
            PyProjectMetadata meta = PyProjectReader.Read(Path.Combine(_root, "pyproject.toml"));
            string kindTok, fwTok;
            Assert.IsTrue(meta.ToolHelixStudio.TryGetValue("kind", out kindTok));
            Assert.IsTrue(meta.ToolHelixStudio.TryGetValue("gui-framework", out fwTok));
            Assert.AreEqual(ProjectKind.GuiApp, ProjectKinds.FromToken(kindTok));
            Assert.AreEqual(GuiFramework.PySide6, GuiFrameworks.FromToken(fwTok));
        }
    }
}
