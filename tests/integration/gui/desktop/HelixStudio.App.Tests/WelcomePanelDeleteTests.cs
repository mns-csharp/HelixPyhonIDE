using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Welcome page "Delete from Disk" (recent-row context menu): deletes the
    /// solution's folder, forgets the recent entry, and announces the deletion.
    /// Tests exercise the dialog-free internal layer (suite convention) and use
    /// permanent deletion so nothing lands in the user's Recycle Bin; the UI path
    /// itself always recycles.
    /// </summary>
    [TestClass]
    public class WelcomePanelDeleteTests
    {
        private string _stateDir;
        private string _prevState;

        [TestInitialize]
        public void Setup()
        {
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_wpdel_state_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_stateDir);
            _prevState = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _stateDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _prevState);
            try { Directory.Delete(_stateDir, true); } catch { }
        }

        private static string MakeSolution(out string toml)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_wpdel_sln_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            toml = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(toml, "[project]\nname = \"doomed\"\n");
            File.WriteAllText(Path.Combine(dir, "main.py"), "def main():\n    pass\n");
            return dir;
        }

        [TestMethod]
        public void DeleteSolutionAndForget_DeletesFolder_ForgetsRecent_AndRaisesTheEvent()
        {
            string toml;
            string dir = MakeSolution(out toml);
            var recents = new RecentFilesManager();
            recents.AddRecent(toml);

            using (var panel = new WelcomePanel(recents, new UserSettingsManager()))
            {
                string announced = null;
                panel.SolutionDeletedFromDisk += (s, p) => announced = p;

                string error;
                Assert.IsTrue(panel.DeleteSolutionAndForget(toml, toRecycleBin: false, error: out error), error);

                Assert.IsFalse(Directory.Exists(dir), "the solution folder is gone from disk");
                Assert.IsFalse(recents.GetRecentFiles().Contains(toml), "the recent entry is forgotten");
                Assert.AreEqual(toml, announced, "the deletion is announced so an open solution can close");
            }
        }

        [TestMethod]
        public void DeleteSolutionAndForget_StaleEntry_JustForgetsIt()
        {
            string toml = Path.Combine(Path.GetTempPath(),
                "helix_wpdel_gone_" + Guid.NewGuid().ToString("N"), "pyproject.toml");  // never created
            var recents = new RecentFilesManager();
            recents.AddRecent(toml);

            using (var panel = new WelcomePanel(recents, new UserSettingsManager()))
            {
                string error;
                Assert.IsTrue(panel.DeleteSolutionAndForget(toml, toRecycleBin: false, error: out error),
                    "an already-gone folder counts as done — the goal state is reached");
                Assert.IsFalse(recents.GetRecentFiles().Contains(toml));
            }
        }

        [TestMethod]
        public void RecentRow_RightClick_DoesNotTriggerTheOpenCommand()
        {
            // Regression: MouseClick fires for every button — before the left-button guard,
            // right-clicking a row for the context menu ALSO opened the solution.
            string toml;
            string dir = MakeSolution(out toml);
            try
            {
                var recents = new RecentFilesManager();
                recents.AddRecent(toml);

                using (var panel = new WelcomePanel(recents, new UserSettingsManager())
                       { Size = new System.Drawing.Size(900, 600) })
                {
                    panel.CreateControl();
                    // Row hit-regions are rebuilt during paint — force one.
                    using (var bmp = new System.Drawing.Bitmap(panel.Width, panel.Height))
                        panel.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, panel.Width, panel.Height));

                    var boundsField = typeof(WelcomePanel).GetField("_recentBounds",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var bounds = (System.Collections.Generic.List<System.Drawing.Rectangle>)boundsField.GetValue(panel);
                    Assert.IsTrue(bounds.Count > 0, "the recent row must have a painted hit-region");
                    var rowCenter = new System.Drawing.Point(
                        bounds[0].Left + (bounds[0].Width / 2), bounds[0].Top + (bounds[0].Height / 2));

                    string opened = null;
                    panel.OpenRecentClicked += (s, p) => opened = p;

                    var click = typeof(WelcomePanel).GetMethod("OnMouseClick",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    click.Invoke(panel, new object[] {
                        new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Right, 1, rowCenter.X, rowCenter.Y, 0) });
                    Assert.IsNull(opened, "a right-click (context menu) must NOT trigger the open command");

                    click.Invoke(panel, new object[] {
                        new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, rowCenter.X, rowCenter.Y, 0) });
                    Assert.AreEqual(toml, opened, "a left-click still opens the row");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void RecentContextMenu_SurvivesRepeatedOpenCloseCycles()
        {
            // Regression for crash-20260801-195854: the strip was disposed from its own Closed
            // event; ToolStripManager's modal filter then touched the dead control on the next
            // outside-click and took the whole app down. The strip is now reusable and panel-owned.
            string toml;
            string dir = MakeSolution(out toml);
            try
            {
                var recents = new RecentFilesManager();
                recents.AddRecent(toml);
                using (var panel = new WelcomePanel(recents, new UserSettingsManager())
                       { Size = new System.Drawing.Size(900, 600) })
                {
                    panel.CreateControl();
                    var show = typeof(WelcomePanel).GetMethod("ShowRecentContextMenu",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var menuField = typeof(WelcomePanel).GetField("_recentMenu",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    for (int cycle = 0; cycle < 3; cycle++)
                    {
                        show.Invoke(panel, new object[] { toml, new System.Drawing.Point(10, 10) });
                        var menu = (System.Windows.Forms.ContextMenuStrip)menuField.GetValue(panel);
                        Assert.IsNotNull(menu);
                        Assert.IsFalse(menu.IsDisposed, "the strip must stay alive across open/close cycles");
                        Assert.AreEqual(4, menu.Items.Count, "Open / Remove / separator / Delete");
                        menu.Close();
                        System.Windows.Forms.Application.DoEvents();
                        Assert.IsFalse(menu.IsDisposed, "closing must never dispose the strip");
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void TryDeleteSolutionDirectory_RefusesADriveRoot()
        {
            string rootToml = Path.Combine(Path.GetPathRoot(Path.GetTempPath()), "pyproject.toml");
            string error;
            Assert.IsFalse(WelcomePanel.TryDeleteSolutionDirectory(rootToml, toRecycleBin: false, error: out error));
            StringAssert.Contains(error, "drive root");
        }

        [TestMethod]
        public void TryDeleteSolutionDirectory_ReportsFailure_InsteadOfThrowing()
        {
            string toml;
            string dir = MakeSolution(out toml);
            try
            {
                // Hold a file open with no sharing so the recursive delete fails midway.
                using (new FileStream(Path.Combine(dir, "main.py"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    string error;
                    Assert.IsFalse(WelcomePanel.TryDeleteSolutionDirectory(toml, toRecycleBin: false, error: out error));
                    StringAssert.Contains(error, "Could not delete");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
