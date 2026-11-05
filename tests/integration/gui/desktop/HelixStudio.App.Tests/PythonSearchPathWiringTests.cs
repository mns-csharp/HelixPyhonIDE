using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.VirtualCompiler.Imports;
using HelixPythonNamespace.Workspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The IDE-side wiring for Python import resolution, driven against a temporary multi-project
    /// workspace through the production <see cref="SolutionService.LoadAsync"/> path.
    ///
    /// Reported failure: running the solution's startup project raised
    /// <c>ImportError: cannot import name 'TsvPreparationWorkflow' from 'sample_common'
    /// (unknown location)</c>. <c>BuildPythonPathEnv</c> put only the PARENT of each project
    /// directory on <c>PYTHONPATH</c>, so the interpreter saw <c>projects/</c> and bound the
    /// <c>__init__</c>-free <c>projects/sample_common/</c> folder as an empty PEP 420 namespace
    /// package, shadowing the real package one level down. These tests pin both halves of the fix:
    /// the launcher's path now includes each project directory, and the compiler resolves imports
    /// against that same path.
    ///
    /// </summary>
    [TestClass]
    public class PythonSearchPathWiringTests
    {
        private string _fixtureRoot;

        [TestCleanup]
        public void Cleanup()
        {
            if (!string.IsNullOrEmpty(_fixtureRoot) && Directory.Exists(_fixtureRoot))
                Directory.Delete(_fixtureRoot, true);
        }

        private string FixtureRoot()
        {
            _fixtureRoot = Path.Combine(Path.GetTempPath(), "helix-python-search-path-" + Guid.NewGuid().ToString("N"));
            string commonProject = Path.Combine(_fixtureRoot, "src", "common");
            string appProject = Path.Combine(_fixtureRoot, "src", "app");
            string package = Path.Combine(commonProject, "sample_common");
            Directory.CreateDirectory(Path.Combine(package, "inference"));
            Directory.CreateDirectory(Path.Combine(package, "onnx"));
            Directory.CreateDirectory(appProject);
            File.WriteAllText(Path.Combine(_fixtureRoot, "pyproject.toml"),
                "[project]\nname = \"workspace\"\nversion = \"1.0\"\n\n" +
                "[tool.uv.workspace]\nmembers = [\"src/common\", \"src/app\"]\n");
            File.WriteAllText(Path.Combine(commonProject, "pyproject.toml"),
                "[project]\nname = \"common\"\nversion = \"1.0\"\n");
            File.WriteAllText(Path.Combine(appProject, "pyproject.toml"),
                "[project]\nname = \"app\"\nversion = \"1.0\"\n");
            File.WriteAllText(Path.Combine(package, "__init__.py"), "");
            File.WriteAllText(Path.Combine(package, "inference", "__init__.py"), "");
            File.WriteAllText(Path.Combine(package, "onnx", "__init__.py"), "");
            File.WriteAllText(Path.Combine(package, "onnx", "export_onnx.py"), "def export_checkpoint():\n    pass\n");
            File.WriteAllText(Path.Combine(appProject, "main.py"),
                "from sample_common.onnx.export_onnx import export_checkpoint\n\nexport_checkpoint()\n");
            return _fixtureRoot;
        }

        private SolutionModel LoadFixtureSolution()
        {
            var load = new SolutionService().LoadAsync(Path.Combine(FixtureRoot(), "pyproject.toml"));
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            Assert.IsTrue(load.Result.Solution.Projects.Count > 1,
                "the fixture is a multi-project workspace");
            return load.Result.Solution;
        }

        /// <summary>
        /// The member project whose directory holds the <c>sample_common</c> package, found in the
        /// LOADED solution rather than spelled out here.
        ///
        /// It used to be hardcoded as <c>projects/sample_common</c>, and the fixture now lays the
        /// same solution out as <c>00-sample-common/</c>. What these tests are about is the
        /// relationship between a project's own directory and its parent on the search path —
        /// nothing about that depends on what the directory is CALLED, so naming it was the test
        /// asserting a fact it does not hold.
        /// </summary>
        private static string CommonProjectDir(SolutionModel solution)
        {
            foreach (ProjectModel project in solution.Projects)
            {
                string dir = Path.GetDirectoryName(project.FilePath);
                if (!string.IsNullOrEmpty(dir)
                    && File.Exists(Path.Combine(dir, "sample_common", "__init__.py")))
                    return dir;
            }
            Assert.Inconclusive("no project in the fixture holds the sample_common package");
            return null;
        }

        [TestMethod]
        public void PythonPathEnv_IncludesEachProjectDirectory_NotJustItsParent()
        {
            SolutionModel sln = LoadFixtureSolution();
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                string pythonPath = form.BuildPythonPathEnv(sln)["PYTHONPATH"];
                List<string> entries = pythonPath.Split(Path.PathSeparator).ToList();

                CollectionAssert.Contains(entries, CommonProjectDir(sln),
                    "without the library project's own directory on PYTHONPATH, `import sample_common` "
                    + "binds the parent folder as an empty namespace package");
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void CompileSearchPath_ResolvesSampleCommonToTheRealPackage()
        {
            SolutionModel sln = LoadFixtureSolution();
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                PythonSearchPath path = form.BuildPythonSearchPath(sln, null);
                ResolvedModule m = new ModuleResolver(path).Resolve("sample_common");

                Assert.AreEqual(ModuleKind.RegularPackage, m.Kind,
                    "the compiler must see the same package the fixed launcher will import");
                Assert.AreEqual(Path.Combine(CommonProjectDir(sln), "sample_common", "__init__.py"),
                    m.Location);
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void LauncherPathAndCompilerPath_AgreeExactly()
        {
            SolutionModel sln = LoadFixtureSolution();
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                // The whole point of routing both through PythonSearchPath: the roots the compiler
                // checks against and the roots python.exe is handed cannot drift apart.
                var launcher = form.BuildPythonPathEnv(sln)["PYTHONPATH"]
                    .Split(Path.PathSeparator)
                    .Where(e => e.Length > 0).ToList();
                var compiler = form.BuildPythonSearchPath(sln, null).Roots.ToList();

                foreach (string root in compiler)
                    CollectionAssert.Contains(launcher, root,
                        "every root the compiler resolves against must also reach the interpreter");
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void SolutionCompile_ReportsNoImportProblemsForTheValidFixture()
        {
            SolutionModel sln = LoadFixtureSolution();
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                PythonSearchPath path = form.BuildPythonSearchPath(sln, null);
                var files = new List<WorkspaceFile>();
                foreach (ProjectModel p in sln.Projects)
                {
                    string dir = Path.GetDirectoryName(p.FilePath);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir, "*.py", SearchOption.AllDirectories))
                    {
                        if (f.IndexOf("__pycache__", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        files.Add(new WorkspaceFile(f, path.ModuleFqnOf(f)));
                    }
                }
                Assert.IsTrue(files.Count > 0, "the fixture solution has Python files");

                var options = new CompileOptions
                {
                    EnableTypeChecker = false,
                    EnableCompileValidator = false,
                    SearchPath = path,
                };
                var problems = VirtualCompiler.VirtualCompiler.CompileSolution(files, options)
                    .SelectMany(r => r.Result.Diagnostics)
                    .Where(d => d.Code == VirtualCompilerDiagnosticCodes.ImportModuleNotFound
                                || d.Code == VirtualCompilerDiagnosticCodes.ImportNameNotFound
                                || d.Code == VirtualCompilerDiagnosticCodes.ImportRelativeBeyondTopLevel)
                    .ToList();

                Assert.AreEqual(0, problems.Count,
                    "Build ▸ Compile must be clean on a valid solution: "
                    + string.Join(" | ", problems.Select(d => d.Code + " " + d.Message)));
            }
            finally { form.Dispose(); }
        }
    }
}
