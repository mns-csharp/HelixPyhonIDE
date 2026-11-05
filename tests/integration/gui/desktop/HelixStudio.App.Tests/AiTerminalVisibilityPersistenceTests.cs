using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Closing the AI Terminal survives a restart.
    ///
    /// The bug: <c>_aiTerminalHidden</c> lived only in memory. Closing the terminal collapsed the
    /// splitter and ticked the menu off, and every one of those effects was gone the next time the
    /// IDE started — so the panel the user deliberately dismissed came back on every launch. Every
    /// sibling dockable (tools palette, minimap, floating tree) already persisted; this one had a
    /// field where the other three had a settings property.
    ///
    /// The oracle is deliberately the SETTINGS FILE, not the in-memory flag: the flag was always
    /// correct within a session, and asserting on it would have passed while the bug was live.
    ///
    /// <para><b>Two of these branch on <see cref="MainForm.AiTerminalSuppressed"/>.</b> While the
    /// panel is suppressed by instruction the restore contract cannot hold — the terminal starts
    /// hidden whatever the file says — so those tests assert the suppression contract instead: it
    /// starts hidden, and the user's saved preference is left untouched so that un-suppressing
    /// gives them back what they last chose. Branching rather than deleting means the original
    /// coverage returns the moment the constant is flipped back, which is the whole point of the
    /// constant being the only thing to flip.</para>
    /// </summary>
    [TestClass]
    public class AiTerminalVisibilityPersistenceTests
    {
        private string _stateDir;
        private string _prevState;

        [TestInitialize]
        public void Setup()
        {
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_aiterm_" + Guid.NewGuid().ToString("N"));
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
        public void TheSettingDefaultsToShowing()
        {
            // A first run, and — just as importantly — a settings file written by an older build
            // that has no such key at all. Defaulting to false there would hide the terminal from
            // every existing user the moment they upgraded.
            Assert.IsTrue(new UserSettingsManager().Settings.AiTerminalVisible,
                "a fresh profile shows the terminal");

            File.WriteAllText(Path.Combine(_stateDir, "user-settings.json"),
                "{ \"Theme\": \"Blue\", \"SnapToGrid\": true }");
            Assert.IsTrue(new UserSettingsManager().Settings.AiTerminalVisible,
                "a settings file predating this key must still show the terminal");
        }

        [TestMethod]
        public void ClosingTheTerminalIsWrittenToDisk_AndReadBackOnRestart()
        {
            // The user's report, end to end: close it, restart, it should still be closed.
            var settings = new UserSettingsManager();
            Assert.IsTrue(settings.Settings.AiTerminalVisible);

            settings.Settings.AiTerminalVisible = false;
            settings.Save();

            string json = File.ReadAllText(Path.Combine(_stateDir, "user-settings.json"));
            StringAssert.Contains(json, "\"AiTerminalVisible\": false",
                "the choice must reach the file, not just the in-memory object");

            Assert.IsFalse(new UserSettingsManager().Settings.AiTerminalVisible,
                "a fresh session — the restart — must read the terminal back as closed");
        }

        // ---- the real thing: a composed MainForm writes it, a second one reads it back ---------
        //
        // TestMainForm.CreateComposed() builds the form the way Program.Main does. The parameterless
        // MainForm() is the DESIGNER constructor - it runs InitializeComponent() and nothing else,
        // so _settingsManager, _editorAiSplitter and _aiTerminalDock are null and every assertion
        // against them passes vacuously. An earlier draft of this file used it and "passed" three
        // tests that exercised nothing.

        [TestMethod]
        public void AClosedTerminalStaysClosedWhenTheIdeStartsAgain()
        {
            // The reported bug, end to end: close it, restart, it must still be closed.
            using (MainForm first = Realized())
            {
                Assert.AreEqual(!MainForm.AiTerminalSuppressed, first.IsAiTerminalVisible,
                    MainForm.AiTerminalSuppressed
                        ? "suppressed, so it must start hidden"
                        : "it starts out showing");

                first.HideAiTerminal();                     // the user clicks the X
                Assert.IsFalse(first.IsAiTerminalVisible);

                PersistOnClose(first);                      // the user quits the IDE
            }

            using (MainForm second = Realized())            // the reboot
            {
                Assert.IsFalse(second.IsAiTerminalVisible,
                    "the terminal the user closed came back on restart - the reported bug");
            }
        }

        [TestMethod]
        public void ATerminalLeftOpenIsStillOpenOnTheNextStart()
        {
            // The other direction, so the fix cannot be "always hide it" - which would satisfy the
            // test above while being just as wrong.
            //
            // While the panel is suppressed, "always hide it" is precisely what was asked for, so
            // the assertion becomes the suppression contract: hidden on the next launch, and the
            // user's own preference still recorded as showing underneath.
            using (MainForm first = Realized())
            {
                first.HideAiTerminal();
                first.ShowAiTerminal();                     // the user brings it back
                Assert.IsTrue(first.IsAiTerminalVisible);
                PersistOnClose(first);
            }

            using (MainForm second = Realized())
            {
                if (MainForm.AiTerminalSuppressed)
                {
                    Assert.IsFalse(second.IsAiTerminalVisible,
                        "suppressed: it must stay hidden whatever the file says");
                    Assert.IsTrue(new UserSettingsManager().Settings.AiTerminalVisible,
                        "suppression must not overwrite the preference it is overriding, or "
                        + "un-suppressing would hand the user a decision that was never theirs");
                }
                else
                {
                    Assert.IsTrue(second.IsAiTerminalVisible,
                        "a terminal left open must not be hidden on the next launch");
                }
            }
        }

        [TestMethod]
        public void TheViewMenuTickMatchesTheRestoredState()
        {
            // The tick is how the user discovers they can get it back. Restoring it hidden while
            // leaving View > AI Terminal ticked would leave no signposted way back.
            using (MainForm first = Realized())
            {
                first.HideAiTerminal();
                PersistOnClose(first);
            }

            using (MainForm second = Realized())
            {
                var item = (ToolStripMenuItem)typeof(MainForm)
                    .GetField("_aiTerminalMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(second);
                Assert.IsNotNull(item, "the View menu item must exist");
                Assert.IsFalse(item.Checked, "the tick must agree with the restored state");
            }
        }

        /// <summary>
        /// The two steps quitting the IDE takes, in order. The whole of OnFormClosing is not invoked
        /// because it also tears down floating panes and the debug session; those are covered by
        /// ShutdownRobustnessTests, and dragging them in here would make this test fail for reasons
        /// unrelated to what it checks.
        /// </summary>
        private static void PersistOnClose(MainForm form)
        {
            form.PersistAiTerminalVisibility();

            var settings = (UserSettingsManager)typeof(MainForm)
                .GetField("_settingsManager", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(form);
            Assert.IsNotNull(settings, "a composed form owns a settings manager");
            settings.Save();
        }

        /// <summary>
        /// A composed MainForm shown far off-screen. It must be SHOWN: settings are applied from the
        /// Load event, so an unshown form restores nothing and every assertion here would read
        /// construction defaults rather than persisted state.
        /// </summary>
        private static MainForm Realized()
        {
            MainForm form = TestMainForm.CreateComposed();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            form.ShowInTaskbar = false;
            TestWindow.ShowOffScreen(form);
            TestMainForm.PumpUntilIdle();
            return form;
        }

    }
}
