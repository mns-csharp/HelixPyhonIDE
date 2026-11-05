using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HelixPythonNamespace.AddIns;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Spec 004 §4.3, the SDK members the PDB Viewer add-in needed (Task C): a declining file
    /// opener (also asked by the Solution Explorer), File ▸ Open filter rows, the recent list, the
    /// foreground job, tab deactivation, the explorer stepping aside, and a data folder. Each is
    /// recorded per add-in and taken back by <c>UnloadAddIn</c>.
    /// </summary>
    [TestClass]
    public class AddInHostSdkTests
    {
        /// <summary>An add-in that does whatever the test hands it in Initialize.</summary>
        public sealed class HookAddIn : IHelixAddIn
        {
            public string IdValue = "hook-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            public Action<IAddInHost> OnInitialize;
            public IAddInHost Host;

            public string Id { get { return IdValue; } }
            public string DisplayName { get { return "Hook " + IdValue; } }

            public void Initialize(IAddInHost host)
            {
                Host = host;
                if (OnInitialize != null) OnInitialize(host);
            }

            public void Shutdown() { }
        }

        private static bool OpenDesignSurfaceFor(MainForm form, string path)
        {
            MethodInfo m = typeof(MainForm).GetMethod("OpenDesignSurfaceFor", BindingFlags.Instance | BindingFlags.NonPublic);
            return (bool)m.Invoke(form, new object[] { path });
        }

        private static string TempFile(string name, string text)
        {
            // Left in %TEMP% on purpose (Chapter 4, §4.6.4).
            string dir = Path.Combine(Path.GetTempPath(), "helix-addin-sdk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        [TestMethod]
        public void AddInProjectExecutionSurface_IsEmptyWithoutSolutionAndRoutesNullSafely()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                var addIn = new HookAddIn();
                form.LoadAddIn(addIn, AddInKind.User, null);
                Assert.IsNull(addIn.Host.SolutionProjects());
                Assert.IsTrue(addIn.Host.RunSolutionProjectAsync(null).IsCompleted);
                addIn.Host.RunSolutionProjectTests(null);
            }
        }

        [TestMethod]
        public void ADecliningOpener_PassesThePathOn_FromFileOpenAndFromTheSolutionExplorer()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                string pickyOpened = null, greedyOpened = null;
                // Loaded first: claims only the files whose content says so; throws on "boom".
                var picky = new HookAddIn
                {
                    OnInitialize = h => h.RegisterFileOpener(".twofmt",
                        path =>
                        {
                            string text = File.ReadAllText(path);
                            if (text == "boom") throw new InvalidOperationException("cannot tell");
                            return text == "mine";
                        },
                        path => pickyOpened = path),
                };
                var greedy = new HookAddIn { OnInitialize = h => h.RegisterFileOpener(".twofmt", path => greedyOpened = path) };
                Assert.IsNotNull(form.LoadAddIn(picky, AddInKind.User, null));
                Assert.IsNotNull(form.LoadAddIn(greedy, AddInKind.User, null));

                string mine = TempFile("a.twofmt", "mine");
                string other = TempFile("b.twofmt", "symbols");
                string boom = TempFile("c.twofmt", "boom");

                Assert.IsTrue(OpenDesignSurfaceFor(form, mine));
                Assert.AreEqual(mine, pickyOpened, "the opener that claims the content opens it");
                Assert.IsNull(greedyOpened, "and the next is not asked");

                Assert.IsTrue(OpenDesignSurfaceFor(form, other));
                Assert.AreEqual(other, greedyOpened, "a declined path goes on to the next opener");

                Assert.IsTrue(OpenDesignSurfaceFor(form, boom));
                Assert.AreEqual(boom, greedyOpened, "an opener that throws while deciding counts as declining");
                StringAssert.Contains(form._buildOutputTextBox.Text, "cannot tell", "and the failure is reported");

                // The Solution Explorer's double-click / Open (MainForm.OpenDocument) asks the same
                // openers first (spec 004 §4.3 ruling).
                pickyOpened = null;
                form.OpenDocument(mine);
                Assert.AreEqual(mine, pickyOpened, "a double-click in the Solution Explorer reaches the add-in");

                // With nobody claiming, the shell's own route runs again.
                form.UnloadAddIn(greedy.Id);
                Assert.IsFalse(OpenDesignSurfaceFor(form, other), "declined by everyone: the shell decides");
                form.UnloadAddIn(picky.Id);
                Assert.IsFalse(OpenDesignSurfaceFor(form, mine), "an unloaded add-in's opener is gone");
            }
        }

        [TestMethod]
        public void AnOpenFileFilter_JoinsFileOpen_AndLeavesWithItsAddIn()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                var addIn = new HookAddIn
                {
                    OnInitialize = h =>
                    {
                        h.AddOpenFileFilter("Widget files (*.wdg)", "*.wdg");
                        h.AddOpenFileFilter("Broken|row", "*.bad");   // a '|' would shift every row after it
                    },
                };
                form.LoadAddIn(addIn, AddInKind.User, null);

                string[] fields = form.OpenFileFilter().Split('|');
                Assert.AreEqual(0, fields.Length % 2, "description/pattern pairs: " + form.OpenFileFilter());
                int row = Array.IndexOf(fields, "Widget files (*.wdg)");
                Assert.IsTrue(row > 0 && row % 2 == 0, "its row is in the dialog: " + form.OpenFileFilter());
                Assert.AreEqual("*.wdg", fields[row + 1]);
                CollectionAssert.Contains(fields[1].Split(';'), "*.wdg", "and the first, everything row offers it too");
                CollectionAssert.Contains(fields[1].Split(';'), "*.canvas", "beside the shell's own types");
                Assert.IsFalse(form.OpenFileFilter().Contains("*.bad"), "a row with '|' in it is refused");
                Assert.AreEqual("All Files (*.*)", fields[fields.Length - 2], "All Files stays last");

                form.UnloadAddIn(addIn.Id);
                Assert.IsFalse(form.OpenFileFilter().Contains("*.wdg"), "the row leaves with its add-in");
            }
        }

        [TestMethod]
        public void AddRecentFile_ListsAnExistingFile_IgnoresAMissingOne_AndStopsWithItsAddIn()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                var addIn = new HookAddIn();
                form.LoadAddIn(addIn, AddInKind.User, null);
                string real = TempFile("recent.twofmt", "x");
                string missing = Path.Combine(Path.GetDirectoryName(real), "never-written.twofmt");

                addIn.Host.AddRecentFile(real);
                addIn.Host.AddRecentFile(missing);
                addIn.Host.AddRecentFile(null);

                var recent = form._recentFilesManager.GetRecentFiles();
                Assert.IsTrue(recent.Any(p => string.Equals(p, real, StringComparison.OrdinalIgnoreCase)), "an existing file is listed");
                Assert.IsFalse(recent.Any(p => string.Equals(p, missing, StringComparison.OrdinalIgnoreCase)), "a missing one is not");

                string later = TempFile("later.twofmt", "x");
                form.UnloadAddIn(addIn.Id);
                addIn.Host.AddRecentFile(later);
                Assert.IsFalse(form._recentFilesManager.GetRecentFiles().Any(p => string.Equals(p, later, StringComparison.OrdinalIgnoreCase)),
                    "a removed add-in's leftover objects no longer reach the list");
            }
        }

        [TestMethod]
        public void RunJob_WorksOffTheUiThread_FinishesOnIt_RefusesWhileBusy_AndHandsBackAFailure()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);   // a handle, so the job marshals back as in the IDE
                var addIn = new HookAddIn();
                form.LoadAddIn(addIn, AddInKind.User, null);
                int ui = Thread.CurrentThread.ManagedThreadId;
                int workThread = -1, finishThread = -1;
                bool finished = false;
                var release = new ManualResetEventSlim(false);

                Assert.IsTrue(addIn.Host.RunJob("Hook job",
                    token => { workThread = Thread.CurrentThread.ManagedThreadId; release.Wait(5000); },
                    () => { finishThread = Thread.CurrentThread.ManagedThreadId; finished = true; },
                    error => Assert.Fail("unexpected failure: " + error)));
                Assert.IsTrue(form._jobRunner.IsBusy, "it is the IDE's foreground job");
                Assert.IsFalse(addIn.Host.RunJob("Second", token => { }, null, null), "one job at a time");

                release.Set();
                for (int i = 0; i < 200 && !finished; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Assert.IsTrue(finished, "onFinished ran");
                Assert.AreNotEqual(ui, workThread, "the work ran off the UI thread");
                Assert.AreEqual(ui, finishThread, "onFinished ran on the UI thread");

                Exception failure = null;
                Assert.IsTrue(addIn.Host.RunJob("Failing job", token => { throw new IOException("disk gone"); }, null, e => failure = e));
                for (int i = 0; i < 200 && failure == null; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Assert.IsInstanceOfType(failure, typeof(IOException), "onFailed gets the exception");
            }
        }

        [TestMethod]
        public void Deactivated_FiresWhenAnotherTabIsSelected_AndWhenTheTabCloses()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);
                var page = new TabPage("Hook Tab");
                int activated = 0, deactivated = 0;
                var addIn = new HookAddIn
                {
                    OnInitialize = h => h.RegisterTab("hookTab", page, false, new AddInTabCallbacks
                    {
                        Activated = () => activated++,
                        Deactivated = () => deactivated++,
                    }),
                };
                form.LoadAddIn(addIn, AddInKind.User, null);

                addIn.Host.ShowTab("hookTab");
                Assert.AreSame(page, form._mainTabControl.SelectedTab);
                Assert.IsTrue(activated >= 1);
                Assert.AreEqual(0, deactivated, "nothing left it yet");

                TabPage other = form._mainTabControl.TabPages.Cast<TabPage>().First(p => p != page);
                form._mainTabControl.SelectedTab = other;
                Assert.AreEqual(1, deactivated, "selecting another tab deactivates it");

                form._mainTabControl.SelectedTab = page;
                Assert.AreEqual(1, deactivated);
                addIn.Host.CloseTab("hookTab");
                Assert.AreEqual(2, deactivated, "closing the selected tab deactivates it at once");

                form._mainTabControl.SelectedTab = other;
                Assert.AreEqual(2, deactivated, "once, not on every later selection");
            }
        }

        [TestMethod]
        public void SetExplorerCollapsed_StepsTheExplorerAside_AndUnloadingTheAddInBringsItBack()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);
                var addIn = new HookAddIn();
                form.LoadAddIn(addIn, AddInKind.User, null);
                int width = form._leftSplitter.SplitterDistance;
                Assert.IsFalse(form._leftSplitter.Panel1Collapsed, "precondition: the explorer column is there");

                addIn.Host.SetExplorerCollapsed(true);
                Assert.IsTrue(form._leftSplitter.Panel1Collapsed, "the column is handed over");
                addIn.Host.SetExplorerCollapsed(false);
                Assert.IsFalse(form._leftSplitter.Panel1Collapsed, "and comes back");
                Assert.AreEqual(width, form._leftSplitter.SplitterDistance, "at its own width");

                addIn.Host.SetExplorerCollapsed(true);
                form.UnloadAddIn(addIn.Id);
                Assert.IsFalse(form._leftSplitter.Panel1Collapsed, "an explorer an add-in put aside comes back with its removal");
            }
        }

        [TestMethod]
        public void DataDirectory_IsTheAddInsOwnFolder_UnderTheStateDirectory_OutsideTheAddInRoot()
        {
            string state = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            Assert.IsFalse(string.IsNullOrWhiteSpace(state), "run with /Settings:HelixStudio.runsettings");
            using (MainForm form = TestMainForm.CreateComposed())
            {
                var a = new HookAddIn();
                var b = new HookAddIn();
                form.LoadAddIn(a, AddInKind.User, null);
                form.LoadAddIn(b, AddInKind.User, null);

                string folder = a.Host.DataDirectory;
                Assert.IsTrue(Directory.Exists(folder), "created on first use");
                Assert.AreEqual(Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(state), "addin-data", a.Id)),
                    Path.GetFullPath(folder));
                Assert.AreNotEqual(folder, b.Host.DataDirectory, "one folder per add-in");
                Assert.IsFalse(Path.GetFullPath(folder).StartsWith(Path.GetFullPath(AddInLoader.UserRoot) + "\\", StringComparison.OrdinalIgnoreCase),
                    "never inside the add-in root, which the loader scans for add-ins");
            }
        }
    }
}
