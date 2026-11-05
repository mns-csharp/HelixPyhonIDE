using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.Services;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// SRS §5.1 (loop 531): dragging a class onto a different file row physically
    /// moves its source text there. PythonTypeMover's text surgery — including the
    /// duplicate-name guard — is covered directly in HelixPythonNamespace.Workspace.Tests
    /// (no UI, no risk); this only exercises MainForm's routing: disk I/O on the
    /// success path and the same-file no-op.
    ///
    /// Every test body runs on an explicitly-created STA thread (<see cref="RunOnSta"/>)
    /// since MSTest does not guarantee the executing thread is STA (nothing in
    /// HelixPythonNamespace.runsettings sets ExecutionThreadApartmentState) and
    /// Control.SetAcceptDrops throws the moment a Form with an AllowDrop control
    /// (the Solution Explorer tree) gets its window handle created off-STA.
    ///
    /// Deliberately NOT covered here: the duplicate-name-refusal path, because
    /// OnTreeMoveType reports that failure via a real MessageBox.Show — calling it
    /// from an automated test blocks forever waiting for a click nobody can give it
    /// (confirmed the hard way: it produced an indefinite hang, not a crash, that
    /// looked identical to one from outside the process). No test in this suite
    /// exercises a MessageBox-showing code path for exactly this reason.
    /// </summary>
    [TestClass]
    public class MoveTypeDragTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_movetype_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // Runs `body` on a dedicated STA thread and re-throws whatever it threw
        // (assertion failure or otherwise) on the calling thread, with the
        // original stack trace preserved, so MSTest reports it exactly as if it
        // had been thrown directly in the test method.
        private static void RunOnSta(Action body)
        {
            ExceptionDispatchInfo captured = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { captured = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            captured?.Throw();
        }

        private string Write(string relPath, string content)
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            return full;
        }

        private (MainForm form, ProjectModel project) LoadComposed()
        {
            File.WriteAllText(Path.Combine(_root, "pyproject.toml"),
                "[project]\nname = \"demo\"\nrequires-python = \">=3.12\"\ndependencies = []\n");
            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(Path.Combine(_root, "pyproject.toml"));
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);

            MainForm form = TestMainForm.CreateComposed();
            TestWindow.ShowOffScreen(form);
            // OnTreeMoveType only guards on a loaded solution being present; setting
            // the internal field directly avoids driving the full async
            // OpenSolutionAsync pipeline (fire-and-forget `async void`, no signal
            // this test could wait on) just to satisfy that one null-check.
            form._currentSolution = load.Result.Solution;
            return (form, load.Result.Solution.Projects[0]);
        }

        [TestMethod]
        public void MoveType_ExtractsFromSource_AndAppendsToTarget()
        {
            RunOnSta(() =>
            {
                string a = Write(@"src\demo\a.py",
                    "class Alpha:\n    def greet(self):\n        return 'alpha'\n");
                string b = Write(@"src\demo\b.py", "class Beta:\n    pass\n");
                var (form, project) = LoadComposed();
                using (form)
                {
                    var alpha = new TypeModel("Alpha", "demo", TypeKind.ConcreteClass, project) { SourceFilePath = a };
                    var targetFg = new ProjectTreePanel.FileGroup { Project = project, FilePath = b };

                    form.OnTreeMoveType(form, new MoveTypeEventArgs(alpha, targetFg));

                    string sourceNow = File.ReadAllText(a);
                    string targetNow = File.ReadAllText(b);
                    Assert.IsFalse(sourceNow.Contains("class Alpha"), "the class must be gone from its old file");
                    StringAssert.Contains(targetNow, "class Beta:");
                    StringAssert.Contains(targetNow, "class Alpha:");
                    StringAssert.Contains(targetNow, "return 'alpha'");
                }
            });
        }

        [TestMethod]
        public void MoveType_SameFile_IsANoOp()
        {
            RunOnSta(() =>
            {
                string a = Write(@"src\demo\a.py", "class Alpha:\n    pass\n");
                string original = File.ReadAllText(a);
                var (form, project) = LoadComposed();
                using (form)
                {
                    var alpha = new TypeModel("Alpha", "demo", TypeKind.ConcreteClass, project) { SourceFilePath = a };
                    var sameFg = new ProjectTreePanel.FileGroup { Project = project, FilePath = a };

                    form.OnTreeMoveType(form, new MoveTypeEventArgs(alpha, sameFg));

                    Assert.AreEqual(original, File.ReadAllText(a));
                }
            });
        }
    }
}
