using System;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// WHERE the AI Terminal lives in the shell.
    ///
    /// It must sit in a horizontal splitter beneath the editor so both are on screen at once. It
    /// was originally registered as a peer tab next to Editor, which meant every exchange with the
    /// agent hid the code being discussed — the agent is used *while* reading that code, so a tab
    /// is the wrong container no matter how good the panel is.
    ///
    /// Asserted structurally rather than by screenshot: <c>DrawToBitmap</c> does not reliably
    /// render a composed <c>MainForm</c> off-screen (and renders <c>RichTextBox</c> content blank
    /// regardless), so a capture cannot distinguish "docked below" from "missing".
    /// </summary>
    [TestClass]
    public class AiTerminalDockingTests
    {
        [TestMethod]
        public void TheAiTerminalIsDockedBelowTheEditor_NotBesideItAsATab()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SplitContainer split = form._editorAiSplitter;
                Assert.IsNotNull(split, "the editor/AI splitter must exist");
                Assert.AreEqual(Orientation.Horizontal, split.Orientation,
                    "horizontal orientation means one pane ABOVE the other");

                Assert.IsTrue(split.Panel1.Controls.Contains(form._mainTabControl),
                    "the editor tabs occupy the TOP pane");
                Assert.IsTrue(split.Panel2.Controls.Contains(form._aiTerminalDock),
                    "the AI Terminal occupies the BOTTOM pane");
            }
        }

        [TestMethod]
        public void TheAiTerminalIsNotAnEditorPeerTab()
        {
            // The regression this exists to catch: putting it back in the tab strip.
            using (var form = TestMainForm.CreateComposed())
            {
                foreach (TabPage page in form._mainTabControl.TabPages)
                {
                    Assert.IsFalse(page.Text.IndexOf("AI Terminal", StringComparison.OrdinalIgnoreCase) >= 0,
                        "the AI Terminal must not be a tab — it would hide the editor it is discussing");
                    Assert.IsFalse(page.Controls.OfType<HelixPythonNamespace.App.Controls.AiTerminalPanel>().Any(),
                        "the panel must not be hosted inside any editor tab page");
                }
            }
        }

        [TestMethod]
        public void TheDockHasATitleBar_LikeTheOtherDockedPanels()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                Assert.IsNotNull(form._aiTerminalDock);
                Assert.AreEqual("AI Terminal", form._aiTerminalDock.Title);
            }
        }

        [TestMethod]
        public void TheEditorRemainsTheDominantPane()
        {
            // The console earns its height while in use; it must not crowd out the editor at rest.
            using (var form = TestMainForm.CreateComposed())
            {
                form.Size = new System.Drawing.Size(1280, 900);
                form.CreateControl();
                SplitContainer split = form._editorAiSplitter;
                Assert.IsTrue(split.Panel2MinSize <= 120,
                    "the bottom pane must be collapsible to a usable minimum");
                Assert.IsTrue(split.Panel1.Height >= split.Panel2.Height,
                    "the editor gets at least half the height");
            }
        }
    }
}
