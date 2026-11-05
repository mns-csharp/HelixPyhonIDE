using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.Ai.Agent;
using HelixPythonNamespace.App.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The AI Terminal as a KEYBOARD-ONLY console.
    ///
    /// The design rule these pin: there are no Send/Stop/Undo buttons and no modal approval
    /// dialogs, because either forces a hand off the keyboard mid-thought. Typing lands in the
    /// console buffer, Enter submits, Esc interrupts, slash commands replace the toolbar, and a
    /// tool approval is one keystroke. A regression that reintroduces a button would pass a
    /// "does it work" test and still be the wrong product, so the absence is asserted directly.
    ///
    /// These assert in TEXT, not pixels: <c>Control.DrawToBitmap</c> renders a
    /// <see cref="RichTextBox"/> blank (it ignores <c>WM_PRINT</c>), so a screenshot of this panel
    /// shows an empty rectangle whether or not it works.
    /// </summary>
    [TestClass]
    public class AiTerminalPanelTests
    {
        private static AiTerminalPanel Realize(out Form host)
        {
            var panel = new AiTerminalPanel(new AiProviderRegistry()) { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),   // never flash onto the user's desktop
                ClientSize = new Size(900, 300),
            };
            host.Controls.Add(panel);
            TestWindow.ShowOffScreen(host);
            Application.DoEvents();
            return panel;
        }

        /// <summary>
        /// Type a line and press Enter, the way a user does — no button involved.
        ///
        /// Deliberately NOT <c>SendKeys.SendWait</c>: that routes through the OS to the focused
        /// window, and a form parked off-screen never has focus. In a shared test host those
        /// keystrokes land somewhere else entirely — the tests then pass alone and fail in a full
        /// run, and at worst type into the developer's own desktop. This drives the same handler a
        /// real keystroke reaches.
        /// </summary>
        private static void TypeLine(AiTerminalPanel panel, string text)
        {
            panel.SimulateTypeLine(text);
            Application.DoEvents();
        }

        // ---- it is a console, not a form ----------------------------------------

        [TestMethod]
        public void ThereAreNoButtons_TheConsoleIsTheWholeSurface()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                Assert.AreEqual(0, panel.Controls.OfType<Button>().Count(),
                    "a button means reaching for the mouse mid-thought — everything is a key or a slash command");
                Assert.AreEqual(0, panel.Controls.OfType<ComboBox>().Count(),
                    "provider selection is /provider, not a dropdown");
                Assert.AreEqual(1, panel.Controls.Count, "the console fills the panel");
                Assert.AreSame(panel.ConsoleBox, panel.Controls[0]);
            }
        }

        [TestMethod]
        public void TheConsoleIsWritable_SoTypingLandsInTheBuffer()
        {
            // A read-only box would force a separate input TextBox — the design this replaced.
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                Assert.IsFalse(panel.ConsoleBox.ReadOnly);
                Assert.AreEqual(Color.FromArgb(12, 12, 12), panel.ConsoleBox.BackColor);
            }
        }

        [TestMethod]
        public void ThereIsAPromptWaitingForInput()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                StringAssert.Contains(panel.ConsoleBox.Text, "#",
                    "a console shows a prompt, not an empty pane");
                StringAssert.Contains(panel.ConsoleBox.Text, "Esc interrupts");
                StringAssert.Contains(panel.ConsoleBox.Text, "/help");
            }
        }

        [TestMethod]
        public void TheBannerStatesTheApprovalContract_BeforeAnythingRuns()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                StringAssert.Contains(panel.ConsoleBox.Text, "reads, edits and runs commands");
                StringAssert.Contains(panel.ConsoleBox.Text, "y/n/a",
                    "the user should know approvals are keystrokes before the first one appears");
            }
        }

        // ---- slash commands replace the toolbar ---------------------------------

        [TestMethod]
        public void SlashHelp_ListsTheCommandsThatReplacedTheButtons()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                TypeLine(panel, "/help");
                string text = panel.ConsoleBox.Text;
                StringAssert.Contains(text, "/provider");
                StringAssert.Contains(text, "/undo");
                StringAssert.Contains(text, "Esc");
            }
        }

        [TestMethod]
        public void SlashProvider_ListsEveryRegisteredProvider()
        {
            // Proves the abstraction reaches the UI: registering a vendor surfaces it with no
            // panel change.
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                TypeLine(panel, "/provider");
                string text = panel.ConsoleBox.Text;
                foreach (var provider in new AiProviderRegistry().Providers)
                    StringAssert.Contains(text, provider.Id);
            }
        }

        [TestMethod]
        public void SlashProvider_SwitchingChangesThePrompt()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                TypeLine(panel, "/provider openai");
                StringAssert.Contains(panel.ConsoleBox.Text, "openai #",
                    "the prompt names the provider, as a shell prompt names the shell");
            }
        }

        [TestMethod]
        public void AnUnknownCommand_SaysSoInsteadOfBeingSentToTheModel()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                TypeLine(panel, "/nonsense");
                StringAssert.Contains(panel.ConsoleBox.Text, "unknown command");
                Assert.IsFalse(panel.IsRunning);
            }
        }

        [TestMethod]
        public void SlashUndo_WithNothingToUndo_SaysSo()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                TypeLine(panel, "/undo");
                StringAssert.Contains(panel.ConsoleBox.Text, "nothing to undo");
            }
        }

        // ---- prompt / input discipline -------------------------------------------

        [TestMethod]
        public void SubmittingWithNoSolution_ExplainsRatherThanFailing()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                TypeLine(panel, "fix the failing tests");
                StringAssert.Contains(panel.ConsoleBox.Text, "no solution open",
                    "the agent's sandbox IS the solution directory");
                Assert.IsFalse(panel.IsRunning);
            }
        }

        [TestMethod]
        public void AnEmptyLine_JustRedrawsThePrompt()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                TypeLine(panel, string.Empty);
                Assert.IsFalse(panel.IsRunning);
                Assert.AreEqual(string.Empty, panel.CurrentLine);
            }
        }

        [TestMethod]
        public void SetSolution_ShowsTheSandboxRootTheAgentIsConfinedTo()
        {
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                string root = System.IO.Path.GetTempPath().TrimEnd('\\');
                panel.SetSolution(root, "demo");
                Application.DoEvents();
                StringAssert.Contains(panel.ConsoleBox.Text, root,
                    "the user should see exactly which directory the agent can touch");
            }
        }

        [TestMethod]
        public void OutputArrivingMidTyping_DoesNotEatTheTypedLine()
        {
            // Agent output lands while the user may be mid-sentence; splicing it in without
            // lifting the typed text would interleave the two.
            Form host;
            AiTerminalPanel panel = Realize(out host);
            using (host)
            {
                panel.ConsoleBox.Focus();
                panel.ConsoleBox.SelectionStart = panel.ConsoleBox.TextLength;
                panel.ConsoleBox.SelectedText = "half a thought";

                panel.SetSolution(System.IO.Path.GetTempPath().TrimEnd('\\'), "demo");
                Application.DoEvents();

                Assert.AreEqual("half a thought", panel.CurrentLine,
                    "the half-typed line survives output written above it");
            }
        }
    }
}
