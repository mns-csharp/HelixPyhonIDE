using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Welcome page's recent-solutions list holds up to 100 entries and
    /// SCROLLS (it used to stop at 10, and only drew the rows that fit).
    /// RecentFilesManager also honors HELIXSTUDIO_STATE_DIR now, like
    /// UserSettingsManager always did — test runs must never touch the real MRU.
    /// </summary>
    [TestClass]
    public class RecentSolutionsCapacityTests
    {
        private string _stateDir;
        private string _prevState;

        [TestInitialize]
        public void Setup()
        {
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_mru_state_" + Guid.NewGuid().ToString("N"));
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
        public void Manager_Keeps100Entries_NewestFirst_InTheIsolatedStateDir()
        {
            var recents = new RecentFilesManager();
            for (int i = 1; i <= 120; i++)
                recents.AddRecent(Path.Combine(_stateDir, "sln" + i, "pyproject.toml"));

            Assert.AreEqual(100, recents.GetRecentFiles().Count, "the MRU cap is 100");
            StringAssert.Contains(recents.GetRecentFiles()[0], "sln120", "newest entry first");
            Assert.IsTrue(File.Exists(Path.Combine(_stateDir, "recent-solutions.json")),
                "the list persists under HELIXSTUDIO_STATE_DIR, never the real AppData");
        }

        [TestMethod]
        public void WelcomePage_ListsBeyondTen_AndGrowsAScrollbar()
        {
            var recents = new RecentFilesManager();
            for (int i = 1; i <= 30; i++)
                recents.AddRecent(Path.Combine(_stateDir, "sln" + i, "pyproject.toml"));

            using (var panel = new WelcomePanel(recents, new UserSettingsManager())
                   { Size = new System.Drawing.Size(900, 560) })
            {
                panel.CreateControl();
                using (var bmp = new System.Drawing.Bitmap(panel.Width, panel.Height))
                    panel.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, panel.Width, panel.Height));

                var recentField = typeof(WelcomePanel).GetField("_recent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var list = (System.Collections.Generic.List<string>)recentField.GetValue(panel);
                Assert.AreEqual(30, list.Count, "the old 10-entry cap is gone");

                var scrollField = typeof(WelcomePanel).GetField("_recentScroll",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var scroll = (VScrollBar)scrollField.GetValue(panel);
                Assert.IsTrue(scroll.Visible, "more rows than fit → the column scrolls");
                Assert.AreEqual(29, scroll.Maximum);

                // Scroll to the end: the top visible row follows, and a repaint keeps
                // hit-regions consistent with the offset.
                int maxTop = 30 - Math.Max(1, scroll.LargeChange);
                scroll.Value = maxTop;
                using (var bmp = new System.Drawing.Bitmap(panel.Width, panel.Height))
                    panel.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, panel.Width, panel.Height));

                var firstField = typeof(WelcomePanel).GetField("_firstVisibleRecent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.AreEqual(maxTop, (int)firstField.GetValue(panel),
                    "painting starts at the scrolled-to row");
            }
        }
    }
}
