using System;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// View ▸ Word Wrap. The toggle must reach the Output pane so long lines fit the margin
    /// instead of running off the right edge behind a horizontal scrollbar.
    /// </summary>
    [TestClass]
    public class WordWrapMenuTests
    {
        // Settings are redirected to a throwaway directory: these tests both read and write the
        // persisted preference, so against the real %APPDATA% file they would depend on the
        // developer's own setting and would overwrite it.
        private static string _stateDir;
        private static string _previousStateDir;

        [ClassInitialize]
        public static void ClassInit(TestContext context)
        {
            _previousStateDir = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            _stateDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                               "helix-wordwrap-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(_stateDir);
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _stateDir);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _previousStateDir);
            try { System.IO.Directory.Delete(_stateDir, true); } catch { }
        }

        // Each test starts from factory defaults, so none of them depends on the order the others
        // ran in — or on what they persisted.
        [TestInitialize]
        public void ResetPersistedSettings()
        {
            try { System.IO.File.Delete(System.IO.Path.Combine(_stateDir, "user-settings.json")); } catch { }
        }

        private static ToolStripMenuItem FindWordWrap(MainForm form)
        {
            foreach (ToolStripItem item in form.ViewMenuForTests.DropDownItems)
            {
                var menu = item as ToolStripMenuItem;
                if (menu != null && menu.Text != null && menu.Text.Replace("&", "") == "Word Wrap")
                    return menu;
            }
            return null;
        }

        [TestMethod]
        public void ViewMenu_ContainsACheckableWordWrapItem()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                ToolStripMenuItem item = FindWordWrap(form);
                Assert.IsNotNull(item, "View menu must contain a 'Word Wrap' item");
                Assert.IsTrue(item.CheckOnClick, "it is a state toggle, not a command");
                Assert.IsFalse(item.Checked, "off by default, matching the editor's current behaviour");
                form.Close();
            }
        }

        [TestMethod]
        public void TogglingIt_WrapsTheOutputPane_AndDropsTheHorizontalScrollbar()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                Assert.IsFalse(form._buildOutputTextBox.WordWrap, "precondition: unwrapped");

                ToolStripMenuItem item = FindWordWrap(form);
                item.PerformClick();
                Application.DoEvents();

                Assert.IsTrue(form.WordWrapEnabled);
                Assert.IsTrue(form._buildOutputTextBox.WordWrap, "Output must wrap");
                Assert.AreEqual(ScrollBars.Vertical, form._buildOutputTextBox.ScrollBars,
                    "the horizontal scrollbar must go, or the text still runs past the margin");
                form.Close();
            }
        }

        [TestMethod]
        public void TogglingItBack_RestoresUnwrappedOutput()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                ToolStripMenuItem item = FindWordWrap(form);
                item.PerformClick();
                Application.DoEvents();
                item.PerformClick();
                Application.DoEvents();

                Assert.IsFalse(form.WordWrapEnabled);
                Assert.IsFalse(form._buildOutputTextBox.WordWrap);
                Assert.AreEqual(ScrollBars.Both, form._buildOutputTextBox.ScrollBars);
                form.Close();
            }
        }

        /// <summary>
        /// The whole point of the preference: turn it on, restart, and it is still on — checked in
        /// the menu AND applied to the panes, not merely remembered in the settings file.
        /// </summary>
        [TestMethod]
        public void ItSurvivesARestart()
        {
            using (var first = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(first);
                Application.DoEvents();
                FindWordWrap(first).PerformClick();      // user turns it on
                Application.DoEvents();
                Assert.IsTrue(first.WordWrapEnabled, "precondition: on before the restart");
                first.Close();
            }

            // A second composed MainForm is what a relaunch produces: settings re-read from disk.
            using (var relaunched = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(relaunched);
                Application.DoEvents();

                Assert.IsTrue(relaunched.WordWrapEnabled,
                    "Word Wrap must come back on after a restart");
                Assert.IsTrue(FindWordWrap(relaunched).Checked,
                    "and the menu must show it ticked, or the state and the UI disagree");
                Assert.IsTrue(relaunched._buildOutputTextBox.WordWrap,
                    "the restored setting must reach the panes, not just the flag");
                Assert.AreEqual(ScrollBars.Vertical, relaunched._buildOutputTextBox.ScrollBars);
                relaunched.Close();
            }
        }

        /// <summary>Turning it off persists too — the off state must not read as "never set".</summary>
        [TestMethod]
        public void TurningItOffAlsoSurvivesARestart()
        {
            using (var first = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(first);
                Application.DoEvents();
                ToolStripMenuItem item = FindWordWrap(first);
                item.PerformClick();
                Application.DoEvents();
                item.PerformClick();                     // and back off again
                Application.DoEvents();
                first.Close();
            }

            using (var relaunched = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(relaunched);
                Application.DoEvents();
                Assert.IsFalse(relaunched.WordWrapEnabled);
                Assert.IsFalse(FindWordWrap(relaunched).Checked);
                Assert.IsFalse(relaunched._buildOutputTextBox.WordWrap);
                relaunched.Close();
            }
        }

        [TestMethod]
        public void ApplyWordWrap_IsSafeWithNoEditorTabsOpen()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                form.ApplyWordWrap();     // must not throw with an empty tab strip
                form.Close();
            }
        }
    }
}
