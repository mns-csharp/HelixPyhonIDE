using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Welcome page "Clear list": forgets the whole recent-solutions history from the start page.
    /// Tests drive the dialog-free internal layer (suite convention — the button itself confirms
    /// with a MessageBox, and a modal dialog in a test run hangs the host), plus the painted hit
    /// region that makes the button reachable at all.
    /// </summary>
    [TestClass]
    public class WelcomeClearRecentTests
    {
        private string _stateDir;
        private string _prevState;

        [TestInitialize]
        public void Setup()
        {
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_wpclear_state_" + Guid.NewGuid().ToString("N"));
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

        // Recent entries are only kept if the file exists (RecentFilesManager prunes), so make
        // real files rather than inventing paths.
        private static string MakeSolution()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_wpclear_sln_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string toml = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(toml, "[project]\nname = \"sample\"\n");
            return toml;
        }

        private static void Paint(WelcomePanel panel)
        {
            panel.CreateControl();
            using (var bmp = new Bitmap(panel.Width, panel.Height))
                panel.DrawToBitmap(bmp, new Rectangle(0, 0, panel.Width, panel.Height));
        }

        private static Rectangle ClearBounds(WelcomePanel panel)
        {
            FieldInfo f = typeof(WelcomePanel).GetField("_clearRecentBounds",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return (Rectangle)f.GetValue(panel);
        }

        private static List<Rectangle> RowBounds(WelcomePanel panel)
        {
            FieldInfo f = typeof(WelcomePanel).GetField("_recentBounds",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return (List<Rectangle>)f.GetValue(panel);
        }

        private static bool HitClear(WelcomePanel panel, Point p)
        {
            MethodInfo m = typeof(WelcomePanel).GetMethod("HitClearRecent",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return (bool)m.Invoke(panel, new object[] { p });
        }

        [TestMethod]
        public void ClearRecentList_EmptiesTheHistory_RepaintsEmpty_AndAnnouncesIt()
        {
            var recents = new RecentFilesManager();
            recents.AddRecent(MakeSolution());
            recents.AddRecent(MakeSolution());

            using (var panel = new WelcomePanel(recents, new UserSettingsManager()))
            {
                int announced = 0;
                panel.RecentsCleared += delegate { announced++; };

                panel.ClearRecentList();

                Assert.AreEqual(0, recents.GetRecentFiles().Count, "the persisted history is emptied");
                var shown = (List<string>)typeof(WelcomePanel)
                    .GetField("_recent", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                Assert.AreEqual(0, shown.Count, "the painted list is emptied too, not just the store");
                Assert.AreEqual(1, announced, "the host is told so File > Recent Files can be rebuilt");
            }
        }

        [TestMethod]
        public void ClearRecentList_SurvivesAnAlreadyEmptyHistory()
        {
            var recents = new RecentFilesManager();
            using (var panel = new WelcomePanel(recents, new UserSettingsManager()))
            {
                panel.ClearRecentList();
                Assert.AreEqual(0, recents.GetRecentFiles().Count);
            }
        }

        [TestMethod]
        public void ClearButton_IsPaintedAndClickable_WhenThereIsHistory()
        {
            var recents = new RecentFilesManager();
            recents.AddRecent(MakeSolution());

            using (var panel = new WelcomePanel(recents, new UserSettingsManager())
                   { Size = new Size(900, 600) })
            {
                Paint(panel);

                Rectangle box = ClearBounds(panel);
                Assert.IsFalse(box.IsEmpty, "the button must have a painted hit region");

                var center = new Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2));
                Assert.IsTrue(HitClear(panel, center), "a click at its centre hits the button");
                Assert.IsFalse(HitClear(panel, new Point(box.Left - 40, box.Top)),
                    "a click beside it does not");

                // It sits in the heading row, ABOVE the solution rows: overlapping one would mean a
                // mis-aimed click on the button opened a solution instead.
                foreach (Rectangle row in RowBounds(panel))
                    Assert.IsFalse(row.IntersectsWith(box), "the button must not overlap a recent row");
            }
        }

        [TestMethod]
        public void ClearButton_IsNotOfferedWhenThereIsNothingToClear()
        {
            var recents = new RecentFilesManager();   // empty, isolated by HELIXSTUDIO_STATE_DIR

            using (var panel = new WelcomePanel(recents, new UserSettingsManager())
                   { Size = new Size(900, 600) })
            {
                Paint(panel);

                Assert.IsTrue(ClearBounds(panel).IsEmpty, "no history, no button");
                Assert.IsFalse(HitClear(panel, new Point(500, 130)),
                    "and nothing in the heading row swallows a click");
            }
        }

        [TestMethod]
        public void ClearButton_HitRegionIsDroppedAsSoonAsTheListIsCleared()
        {
            // The repaint is asynchronous (Invalidate), so a stale rectangle would keep accepting
            // clicks — and re-open the confirmation dialog — after the list was already emptied.
            var recents = new RecentFilesManager();
            recents.AddRecent(MakeSolution());

            using (var panel = new WelcomePanel(recents, new UserSettingsManager())
                   { Size = new Size(900, 600) })
            {
                Paint(panel);
                Rectangle box = ClearBounds(panel);
                Assert.IsFalse(box.IsEmpty);

                panel.ClearRecentList();

                Assert.IsTrue(ClearBounds(panel).IsEmpty);
                Assert.IsFalse(HitClear(panel, new Point(box.Left + (box.Width / 2), box.Top + (box.Height / 2))));
            }
        }
    }
}
