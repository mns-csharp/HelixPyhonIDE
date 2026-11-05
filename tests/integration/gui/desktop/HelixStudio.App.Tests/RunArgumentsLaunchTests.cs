using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Python;
using HelixPythonNamespace.Workspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Run arguments, end to end: written to a package's <c>pyproject.toml</c>, read back by the
    /// production <see cref="SolutionService.LoadAsync"/> path, carried on the run preflight, and
    /// appended to the command line by both launch routes.
    ///
    /// The gap this closes: every launch path composed <c>python -u &lt;entryFile&gt;</c> with
    /// nothing after it, so a CLI package with required positional arguments could not be run from
    /// the IDE at all — it exited 2 on its own argparse no matter what.
    ///
    /// Fixtures are built in a temp directory and deliberately NOT deleted, per this suite's rule
    /// about async solution-load continuations outliving the test that started them.
    /// </summary>
    [TestClass]
    public class RunArgumentsLaunchTests
    {
        private static string NewWorkspace(string runArgsLine)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_runargs_e2e_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"solo\"\nversion = \"1.0.0\"\n\n" +
                "[tool.helixstudio]\nkind = \"app\"\nstartup = true\n" + runArgsLine);
            File.WriteAllText(Path.Combine(root, "main.py"),
                "import sys\n\n\ndef main() -> int:\n    print(sys.argv[1:])\n    return 0\n\n\n" +
                "if __name__ == \"__main__\":\n    raise SystemExit(main())\n");
            return root;
        }

        private static SolutionModel Load(string root)
        {
            var load = new SolutionService().LoadAsync(Path.Combine(root, "pyproject.toml"));
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            return load.Result.Solution;
        }

        [TestMethod]
        public void RunArgsInPyproject_ReachTheRunPreflight()
        {
            string root = NewWorkspace("run-args = \"input.tsv output.tsv\"\n");
            SolutionModel sln = Load(root);

            Assert.AreEqual("input.tsv output.tsv", sln.Projects[0].RunArguments,
                "the loader must surface [tool.helixstudio].run-args on the project model");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.AreEqual("input.tsv output.tsv", pre.RunArguments);
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void NoRunArgs_LeavesThePreflightArgumentsEmpty()
        {
            SolutionModel sln = Load(NewWorkspace(string.Empty));
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.IsTrue(string.IsNullOrEmpty(pre.RunArguments));
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void QuotedPathArgument_SurvivesTheWriteLoadSplitRoundTrip()
        {
            string root = NewWorkspace(string.Empty);
            const string typed = "in.tsv \"C:\\My Data\\out.tsv\"";
            Assert.IsTrue(PyProjectRunArgsWriter.SetRunArgs(Path.Combine(root, "pyproject.toml"), typed));

            SolutionModel sln = Load(root);
            Assert.AreEqual(typed, sln.Projects[0].RunArguments);

            // What the Run path actually hands the process.
            string[] argv = CommandLineArguments.Split(sln.Projects[0].RunArguments);
            CollectionAssert.AreEqual(new[] { "in.tsv", @"C:\My Data\out.tsv" }, argv,
                "the quoted path must arrive as ONE argv entry");
        }

        [TestMethod]
        public void TerminalCommand_AppendsTheArgumentsAfterTheEntryFile()
        {
            // ComposeTerminalRunCommand is private and static; this is the only route to it.
            MethodInfo compose = ComposeTerminalRunCommand();
            Assert.IsNotNull(compose,
                "ComposeTerminalRunCommand(shell, python, module, entry, pythonPath, runArgs) expected");

            string cmd = (string)compose.Invoke(null,
                new object[] { @"C:\Py\python.exe", null, @"C:\ws\main.py", @"C:\ws", "in.tsv \"C:\\My Data\\out.tsv\"" });

            StringAssert.Contains(cmd, "'C:\\ws\\main.py' in.tsv \"C:\\My Data\\out.tsv\"");
            StringAssert.Contains(cmd, "PYTHONPATH='C:\\ws'");
        }

        [TestMethod]
        public void TerminalCommand_WithoutArguments_IsUnchanged()
        {
            string cmd = (string)ComposeTerminalRunCommand().Invoke(null,
                new object[] { @"C:\Py\python.exe", null, @"C:\ws\main.py", null, null });

            Assert.AreEqual("'C:\\Py\\python.exe' -u 'C:\\ws\\main.py'", cmd,
                "the STT shell receives a quoted executable and entry path");
        }

        /// <summary>
        /// An entry module inside a package is launched as `-m pkg.mod`, unquoted, with the run
        /// arguments after it — the terminal half of the relative-import fix.
        /// </summary>
        [TestMethod]
        public void TerminalCommand_ForAPackageEntryModule_UsesDashM()
        {
            string cmd = (string)ComposeTerminalRunCommand().Invoke(null,
                new object[] { @"C:\Py\python.exe", "rouse_model_verification.main",
                               @"C:\ws\rouse_model_verification\main.py", @"C:\ws", "exp13" });

            Assert.AreEqual(
                "PYTHONPATH='C:\\ws' 'C:\\Py\\python.exe' -u -m rouse_model_verification.main exp13",
                cmd);
            StringAssert.DoesNotMatch(cmd, new System.Text.RegularExpressions.Regex(@"main\.py"),
                "the file path must not appear: a path launch is what broke relative imports");
        }

        private static MethodInfo ComposeTerminalRunCommand()
        {
            return typeof(HelixPythonNamespace.App.Coordinators.BuildRunCoordinator)
                .GetMethod("ComposeTerminalRunCommand", BindingFlags.NonPublic | BindingFlags.Static);
        }

        /// <summary>
        /// The reported failure: the manifest said <c>run-args = "…"</c> and Run still launched
        /// with none, because the value was added AFTER the solution was loaded (hand-edited in
        /// the IDE's own editor) and nothing invalidated the cached ProjectModel. The launch must
        /// read the manifest, not the cache.
        /// </summary>
        [TestMethod]
        public void RunArgsAddedToTheManifestAfterLoad_AreStillPickedUp()
        {
            string root = NewWorkspace(string.Empty);
            SolutionModel sln = Load(root);
            Assert.IsNull(sln.Projects[0].RunArguments, "precondition: loaded with no arguments");

            // Simulate editing pyproject.toml in the editor while the solution stays open.
            File.AppendAllText(Path.Combine(root, "pyproject.toml"), "run-args = \"added.tsv\"\n");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.AreEqual("added.tsv", pre.RunArguments,
                    "a run-args line added after load must reach the launch without a reload");
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void RunArgsRemovedFromTheManifestAfterLoad_StopBeingUsed()
        {
            string root = NewWorkspace("run-args = \"stale.tsv\"\n");
            SolutionModel sln = Load(root);
            Assert.AreEqual("stale.tsv", sln.Projects[0].RunArguments);

            // The user deletes the line by hand.
            string toml = Path.Combine(root, "pyproject.toml");
            File.WriteAllLines(toml,
                File.ReadAllLines(toml).Where(l => !l.TrimStart().StartsWith("run-args")).ToArray());

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                Assert.IsTrue(string.IsNullOrEmpty(form.ComputeRunPreflight(sln).RunArguments),
                    "a cleared manifest must clear the launch arguments, not keep the cached value");
            }
            finally { form.Dispose(); }
        }

        /// <summary>
        /// Visual Studio parity: a program runs in its OWN project folder. Run and Run in Terminal
        /// previously used the SOLUTION root, so a relative path in run-args resolved against a
        /// different directory than the same program run from its project folder.
        /// </summary>
        [TestMethod]
        public void WorkingDirectory_IsTheProjectFolder_NotTheSolutionRoot()
        {
            // A workspace whose member sits one level below the solution root, so the two differ.
            string root = Path.Combine(Path.GetTempPath(), "helix_workdir_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"ws\"\nversion = \"1.0.0\"\n\n" +
                "[tool.uv.workspace]\nmembers = [\"app\"]\n");
            string appDir = Path.Combine(root, "app");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "pyproject.toml"),
                "[project]\nname = \"app\"\nversion = \"1.0.0\"\n\n" +
                "[tool.helixstudio]\nkind = \"app\"\nstartup = true\nrun-args = \"data.txt\"\n");
            File.WriteAllText(Path.Combine(appDir, "main.py"),
                "def main() -> int:\n    return 0\n\n\nif __name__ == \"__main__\":\n    raise SystemExit(main())\n");

            SolutionModel sln = Load(root);
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan pre = form.ComputeRunPreflight(sln);
                Assert.IsTrue(pre.CanRun, pre.BlockReason);
                Assert.AreEqual(Path.GetFullPath(appDir), Path.GetFullPath(pre.WorkingDirectory),
                    "Run must launch in the startup project's folder, the way Visual Studio does");
                Assert.AreNotEqual(Path.GetFullPath(root), Path.GetFullPath(pre.WorkingDirectory));
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void WorkingDirectory_PrefersTheDeepestContainingProject()
        {
            // The workspace root is itself a project here; a member's file must still get the
            // member's folder, not the root's.
            string root = Path.Combine(Path.GetTempPath(), "helix_workdir2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"ws\"\nversion = \"1.0.0\"\n\n" +
                "[tool.helixstudio]\nkind = \"app\"\n\n" +
                "[tool.uv.workspace]\nmembers = [\"inner\"]\n");
            File.WriteAllText(Path.Combine(root, "main.py"),
                "def main() -> int:\n    return 0\n\n\nif __name__ == \"__main__\":\n    raise SystemExit(main())\n");
            string innerDir = Path.Combine(root, "inner");
            Directory.CreateDirectory(innerDir);
            File.WriteAllText(Path.Combine(innerDir, "pyproject.toml"),
                "[project]\nname = \"inner\"\nversion = \"1.0.0\"\n\n[tool.helixstudio]\nkind = \"app\"\n");
            // Nested entry point: the script's own folder is NOT the project folder.
            string pkgDir = Path.Combine(innerDir, "pkg");
            Directory.CreateDirectory(pkgDir);
            File.WriteAllText(Path.Combine(pkgDir, "__init__.py"), "");
            string nested = Path.Combine(pkgDir, "__main__.py");
            File.WriteAllText(nested, "raise SystemExit(0)\n");

            SolutionModel sln = Load(root);
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                string resolved = form.ResolveWorkingDirectoryForScript(sln, nested);
                Assert.AreEqual(Path.GetFullPath(innerDir), Path.GetFullPath(resolved),
                    "the deepest containing project wins, and a nested entry point still gets the "
                    + "PROJECT folder rather than its own package folder");
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void WorkingDirectory_FallsBackToTheFilesOwnFolder_WhenNoProjectClaimsIt()
        {
            SolutionModel sln = Load(NewWorkspace(string.Empty));
            string stray = Path.Combine(Path.GetTempPath(),
                "helix_stray_" + Guid.NewGuid().ToString("N"), "loose.py");
            Directory.CreateDirectory(Path.GetDirectoryName(stray));
            File.WriteAllText(stray, "pass\n");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                Assert.AreEqual(Path.GetFullPath(Path.GetDirectoryName(stray)),
                    Path.GetFullPath(form.ResolveWorkingDirectoryForScript(sln, stray)));
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void DebugArguments_AreMatchedToTheScriptBeingLaunched_NotAlwaysTheStartup()
        {
            // Two runnable packages; only one carries run-args. Debug launches whatever file is
            // active, so the arguments must follow the script, not the startup designation.
            string root = Path.Combine(Path.GetTempPath(), "helix_runargs_multi_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"ws\"\nversion = \"1.0.0\"\n\n" +
                "[tool.uv.workspace]\nmembers = [\"alpha\", \"beta\"]\n");
            foreach (string member in new[] { "alpha", "beta" })
            {
                string dir = Path.Combine(root, member);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "pyproject.toml"),
                    "[project]\nname = \"" + member + "\"\nversion = \"1.0.0\"\n\n" +
                    "[tool.helixstudio]\nkind = \"app\"\n" +
                    (member == "alpha" ? "run-args = \"--alpha-only\"\n" : string.Empty));
                File.WriteAllText(Path.Combine(dir, "main.py"),
                    "def main() -> int:\n    return 0\n\n\nif __name__ == \"__main__\":\n    raise SystemExit(main())\n");
            }

            SolutionModel sln = Load(root);
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                string alphaMain = Path.Combine(root, "alpha", "main.py");
                string betaMain = Path.Combine(root, "beta", "main.py");

                Assert.AreEqual("--alpha-only", form.ResolveRunArgumentsForScript(sln, alphaMain));
                Assert.IsNull(form.ResolveRunArgumentsForScript(sln, betaMain),
                    "beta declares no run-args — alpha's must not leak onto it");
                Assert.IsNull(form.ResolveRunArgumentsForScript(sln, Path.Combine(root, "alpha", "helper.py")),
                    "a module that is nobody's entry point gets no arguments");
            }
            finally { form.Dispose(); }
        }
    }
}
