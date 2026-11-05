using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 392 (PSM Slice 3): the Run/Debug pre-flight (PSM-08). With more than one executable
    /// package and none set as startup, Run is blocked with a clear reason; when a package is the
    /// startup (or is the sole executable), Run resolves to it. ComputeRunPreflight is the pure
    /// decision the Run handler and the Build Output surface both use.
    /// </summary>
    [TestClass]
    public class RunPreflightTests
    {
        private static SolutionModel BuildTwoExecutableSolution(out string root)
        {
            root = Path.Combine(Path.GetTempPath(), "helix_run_" + System.Guid.NewGuid().ToString("N"));
            string cliDir = Path.Combine(root, "src", "app_cli");
            string srvDir = Path.Combine(root, "src", "app_srv");
            Directory.CreateDirectory(cliDir);
            Directory.CreateDirectory(srvDir);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"ws\"\n");
            File.WriteAllText(Path.Combine(cliDir, "pyproject.toml"), "[project]\nname = \"app_cli\"\n");
            File.WriteAllText(Path.Combine(srvDir, "pyproject.toml"), "[project]\nname = \"app_srv\"\n");
            // Each executable package has a main.py so TryFindEntryFile treats it as runnable.
            const string main =
                "def main() -> None:\n    pass\n\n" +
                "if __name__ == \"__main__\":\n    raise SystemExit(main())\n";
            File.WriteAllText(Path.Combine(cliDir, "main.py"), main);
            File.WriteAllText(Path.Combine(srvDir, "main.py"), main);

            var sln = new SolutionModel(Path.Combine(root, "pyproject.toml"), "ws");
            var cli = new ProjectModel(Path.Combine(cliDir, "pyproject.toml"), "app_cli", sln) { Kind = ProjectKind.App };
            var srv = new ProjectModel(Path.Combine(srvDir, "pyproject.toml"), "app_srv", sln) { Kind = ProjectKind.App };
            sln.Projects.Add(cli);
            sln.Projects.Add(srv);
            return sln;
        }

        [TestMethod]
        public void TwoExecutablesNoStartup_RunBlocked_WithReason()
        {
            SolutionModel sln = BuildTwoExecutableSolution(out _);
            using (var form = new MainForm())
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsFalse(pre.CanRun, "Run must block with two executables and no startup.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(pre.BlockReason));
                StringAssert.Contains(pre.BlockReason, "Set as Startup Package");
            }
        }

        [TestMethod]
        public void StartupMarked_RunResolvesToIt()
        {
            SolutionModel sln = BuildTwoExecutableSolution(out _);
            sln.Projects[1].IsStartup = true;   // app_srv is the startup
            using (var form = new MainForm())
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.AreEqual("app_srv", pre.StartupName);
                StringAssert.EndsWith(pre.EntryFile, Path.Combine("app_srv", "main.py"));
            }
        }

        // ------------------------------------------------------------------ how Run launches it
        // Reported during testing (BugMap_oop): Run launched the startup package's entry module by FILE
        // PATH, so `rouse_model_verification/main.py`'s own `from .experiments.cli import Cli` died
        // with "attempted relative import with no known parent package" before the program ran. An
        // entry module inside a package has to be started as `python -m <pkg>.<mod>`.

        [TestMethod]
        public void EntryModuleInsideAPackage_ResolvesToItsDottedModule()
        {
            SolutionModel sln = BuildTwoExecutableSolution(out string root);
            sln.Projects[1].IsStartup = true;
            // What makes it a package — and therefore what makes its relative imports legal.
            File.WriteAllText(Path.Combine(root, "src", "app_srv", "__init__.py"), string.Empty);

            using (var form = new MainForm())
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.AreEqual("app_srv.main", pre.RunModule);
            }
        }

        [TestMethod]
        public void PackageDunderMain_CollapsesToThePackageItself()
        {
            SolutionModel sln = BuildTwoExecutableSolution(out string root);
            sln.Projects[1].IsStartup = true;
            string srvDir = Path.Combine(root, "src", "app_srv");
            File.WriteAllText(Path.Combine(srvDir, "__init__.py"), string.Empty);
            File.Move(Path.Combine(srvDir, "main.py"), Path.Combine(srvDir, "__main__.py"));

            using (var form = new MainForm())
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.AreEqual("app_srv", pre.RunModule, "python -m app_srv, not app_srv.__main__");
            }
        }

        [TestMethod]
        public void EntryModuleNotInAPackage_StaysAScriptLaunch()
        {
            // No __init__.py: there are no relative imports to preserve, and every other Python
            // tool runs such a file by path. Nothing about the old launch changes here.
            SolutionModel sln = BuildTwoExecutableSolution(out _);
            sln.Projects[1].IsStartup = true;

            using (var form = new MainForm())
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.IsNull(pre.RunModule);
            }
        }

        [TestMethod]
        public void PerPackageRun_ResolvesTheModuleTheSameWay()
        {
            // Run on a package's own context menu must not launch it differently from Run on the
            // toolbar — same target, same import semantics.
            SolutionModel sln = BuildTwoExecutableSolution(out string root);
            File.WriteAllText(Path.Combine(root, "src", "app_cli", "__init__.py"), string.Empty);

            using (var form = new MainForm())
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflightFor(sln, sln.Projects[0]);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.AreEqual("app_cli.main", pre.RunModule);
            }
        }
    }
}
