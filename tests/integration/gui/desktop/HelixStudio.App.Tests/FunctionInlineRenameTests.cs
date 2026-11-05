using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.UI.Projection;
using HelixPythonNamespace.Workspace.Services;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// SRS §5 (loop 533): F2 on a FunctionModel tree node is now an inline rename —
    /// same ref-aware SymbolRenameService "Rename Symbol..." already used, just
    /// reached via the quick tree-label-edit path (matching how TypeModel already
    /// works) instead of only the modal. Test methods are plain (non-async) and use
    /// .Wait()/.Result, never await, before creating/showing the Form — the
    /// established convention in this suite (see winforms-test-hazards memory).
    /// </summary>
    [TestClass]
    public class FunctionInlineRenameTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_fnrename_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            // The rename marks the layout dirty (ReloadSolutionAsync rebuilds the projection),
            // and form.Close() below would otherwise show a blocking "Save layout before
            // closing?" MessageBox with nobody to click it (see winforms-test-hazards memory).
            System.Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");
        }

        [TestCleanup]
        public void Cleanup()
        {
            System.Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");   // restore the SAFE default, never null
            try { Directory.Delete(_root, true); } catch { }
        }

        [TestMethod]
        public void OnTreeRename_FunctionModel_RenamesDefinitionAndCallSite()
        {
            File.WriteAllText(Path.Combine(_root, "pyproject.toml"),
                "[project]\nname = \"demo\"\nrequires-python = \">=3.12\"\ndependencies = []\n");
            string modulePath = Path.Combine(_root, "src", "demo", "app.py");
            Directory.CreateDirectory(Path.GetDirectoryName(modulePath));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(modulePath), "__init__.py"), "");
            File.WriteAllText(modulePath,
                "def compute_total(items):\n" +
                "    return sum(items)\n" +
                "\n" +
                "def main():\n" +
                "    return compute_total([1, 2, 3])\n");

            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(Path.Combine(_root, "pyproject.toml"));
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            ProjectModel project = load.Result.Solution.Projects[0];
            FunctionModel fn = project.Functions.Find(f => f.Name == "compute_total");
            Assert.IsNotNull(fn, "the target function must be found in the loaded project");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                TestWindow.ShowOffScreen(form);
                form._currentSolution = load.Result.Solution;
                // ExecuteScopedRename ends with a ReloadSolutionAsync(), which rebuilds the
                // projection via a non-null _layoutStore — the same pairing OpenSolutionAsync
                // always makes. Without this, the reload crashes with "Value cannot be null.
                // Parameter name: layoutStore" and pops a blocking MessageBox (found the hard
                // way: an 8m55s test run stuck on operator dialogs).
                form._layoutStore = new JsonLayoutMetadataStore(load.Result.Solution.FilePath);
                form._layoutStore.Load();

                var args = new TreeRenameEventArgs(null, fn, "sum_of_items");
                form.OnTreeRename(form, args);

                string sourceNow = File.ReadAllText(modulePath);
                StringAssert.Contains(sourceNow, "def sum_of_items(items):", "the definition must be renamed");
                StringAssert.Contains(sourceNow, "sum_of_items([1, 2, 3])", "the call site must follow");
                Assert.IsFalse(sourceNow.Contains("compute_total"), "the old name must be gone everywhere");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        [TestMethod]
        public void OnTreeRename_FunctionModel_SameName_IsANoOp()
        {
            File.WriteAllText(Path.Combine(_root, "pyproject.toml"),
                "[project]\nname = \"demo\"\nrequires-python = \">=3.12\"\ndependencies = []\n");
            string modulePath = Path.Combine(_root, "src", "demo", "app.py");
            Directory.CreateDirectory(Path.GetDirectoryName(modulePath));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(modulePath), "__init__.py"), "");
            File.WriteAllText(modulePath, "def helper():\n    pass\n");
            string original = File.ReadAllText(modulePath);

            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(Path.Combine(_root, "pyproject.toml"));
            load.Wait();
            ProjectModel project = load.Result.Solution.Projects[0];
            FunctionModel fn = project.Functions.Find(f => f.Name == "helper");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                TestWindow.ShowOffScreen(form);
                form._currentSolution = load.Result.Solution;
                form._layoutStore = new JsonLayoutMetadataStore(load.Result.Solution.FilePath);
                form._layoutStore.Load();
                form.OnTreeRename(form, new TreeRenameEventArgs(null, fn, "helper"));
                Assert.AreEqual(original, File.ReadAllText(modulePath));
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }
    }
}
