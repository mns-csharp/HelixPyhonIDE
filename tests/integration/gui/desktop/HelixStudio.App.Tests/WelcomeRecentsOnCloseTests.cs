using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Closing a solution must surface it on the Welcome page IMMEDIATELY. The old
    /// path relied on the tab-selection-changed event to refresh the recents list,
    /// but re-inserting the Welcome tab at slot 0 can shift the TabControl's
    /// index-based selection onto it silently — the subsequent SelectedTab
    /// assignment becomes a no-op, the event never fires, and the panel kept its
    /// construction-time list until an IDE restart.
    /// </summary>
    [TestClass]
    public class WelcomeRecentsOnCloseTests
    {
        private string _stateDir;
        private string _prevState;

        [TestInitialize]
        public void Setup()
        {
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_wrc_state_" + Guid.NewGuid().ToString("N"));
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

        [TestMethod]
        public void SelectEntryTabAfterSolutionClose_RefreshesTheRecentsList_EvenWithoutATabChangeEvent()
        {
            string slnDir = Path.Combine(_stateDir, "just_closed");
            Directory.CreateDirectory(slnDir);
            string toml = Path.Combine(slnDir, "pyproject.toml");
            File.WriteAllText(toml, "[project]\nname = \"just_closed\"\n");

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                var panelField = typeof(MainForm).GetField("_welcomePanel",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var panel = (WelcomePanel)panelField.GetValue(form);
                Assert.IsNotNull(panel);

                // Mid-session: a solution was opened (AddRecent) AFTER the panel's
                // construction-time refresh — exactly the state a close ends in.
                form._recentFilesManager.AddRecent(toml);

                // The Welcome tab is already the selected tab on a fresh form, so the
                // close path's SelectedTab assignment fires no SelectedIndexChanged —
                // the refresh must not depend on it.
                form.SelectEntryTabAfterSolutionClose();

                var recentField = typeof(WelcomePanel).GetField("_recent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var list = (System.Collections.Generic.List<string>)recentField.GetValue(panel);
                CollectionAssert.Contains(list, toml,
                    "the just-closed solution must appear on the Welcome page immediately, not after a restart");

                form.Close();
            }
        }
    }
}
