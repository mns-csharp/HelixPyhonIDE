using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// User-reported: the Terminal panel's toolbar was rendered as a dark strip with light text
    /// (didn't match the rest of the IDE's controls), and it had a separate input textbox instead of
    /// behaving like a real interactive terminal (type directly into the console buffer). Covers
    /// both: the toolbar now uses normal Luna control colors, and the console output RichTextBox is
    /// itself the input surface — no standalone TextBox, with edits/navigation clamped so the user
    /// can't type into or delete past prior output.
    ///
    /// The toolbar has since become icon-only (tooltips instead of captions), so the old
    /// "Integrated Terminal" label it used to carry is gone — the tab header already names the panel.
    /// </summary>
    [TestClass]
    public class TerminalPanelInteractivityTests
    {
        private static object GetField(object o, string name)
        {
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return f.GetValue(o);
        }

        private static void InvokePrivate(object o, string name, object[] args)
        {
            MethodInfo m = o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, name + " method expected");
            m.Invoke(o, args);
        }

        private static bool ContainsTextBox(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TextBox) return true;
                if (ContainsTextBox(c)) return true;
            }
            return false;
        }

        [TestMethod]
        public void Toolbar_UsesNormalControlColors_NotADarkStrip()
        {
            using (var panel = new TerminalPanel())
            {
                Panel toolbar = null;
                foreach (Control c in panel.Controls)
                    if (c is Panel p && p.Dock == DockStyle.Top) toolbar = p;
                Assert.IsNotNull(toolbar, "toolbar panel expected");
                Assert.AreEqual(HelixPythonNamespace.AddIns.Kit.LunaColors.ToolbarBackground, toolbar.BackColor,
                    "the toolbar strip must use the same background as every other toolbar, not a dark strip");

                // The toolbar is icon-only now (tooltip labels), and the "  Integrated Terminal"
                // caption it used to carry is gone: the tab header already names the panel, so the
                // caption only ate horizontal room next to the shell picker. What this test is
                // actually about is the colour contract — assert that for every child that has
                // visible text, plus the shell picker.
                Button restart = null;
                foreach (Control c in toolbar.Controls)
                    if (c is Button b) restart = b;
                Assert.IsNotNull(restart, "Restart button expected");

                // Dark ink on light chrome, asserted by brightness rather than against one exact
                // palette entry: the buttons are icon-only and inherit the system control ink,
                // which is every bit as "normal" as LunaColors.TextPrimary. What must never come
                // back is the light-on-dark styling (white / gainsboro) of the old dark strip.
                foreach (Control c in toolbar.Controls)
                    Assert.IsTrue(c.ForeColor.GetBrightness() < 0.5f,
                        c.GetType().Name + " uses light text (" + c.ForeColor +
                        ") — that is dark-strip styling, not a normal toolbar");
            }
        }

        [TestMethod]
        public void ConsoleBuffer_HasNoSeparateInputBox_AndIsDirectlyTypable()
        {
            using (var panel = new TerminalPanel())
            {
                var output = (RichTextBox)GetField(panel, "_output");
                Assert.IsFalse(output.ReadOnly,
                    "the console buffer must be directly typable — no separate input box to relay through");
                Assert.IsFalse(ContainsTextBox(panel),
                    "a standalone input TextBox must not exist any more");
            }
        }

        [TestMethod]
        public void EnterKey_SubmitsTheTypedLine_AndAdvancesInputStart()
        {
            var panel = new TerminalPanel { Dock = DockStyle.Fill };
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000); // off-screen so nothing flashes
                form.ShowInTaskbar = false;
                form.Size = new Size(500, 320);
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                var output = (RichTextBox)GetField(panel, "_output");
                int before = (int)GetField(panel, "_inputStart");

                // Simulate the user having typed "hello" directly into the buffer (real local echo —
                // no separate input box to read from).
                output.Select(output.TextLength, 0);
                output.SelectedText = "hello";
                Assert.AreEqual(before + "hello".Length, output.TextLength);

                InvokePrivate(panel, "OnOutputKeyDown", new object[] { panel, new KeyEventArgs(Keys.Enter) });

                int after = (int)GetField(panel, "_inputStart");
                Assert.AreEqual(output.TextLength, after,
                    "_inputStart must advance to end-of-buffer once the line is submitted");
                Assert.IsTrue(output.Text.Contains("hello"),
                    "the typed command stays visible in scrollback exactly as typed (real local echo)");

                form.Close();
            }
        }

        [TestMethod]
        public void Backspace_CannotDeleteIntoScrollback_AtTheStartOfTheLiveLine()
        {
            using (var panel = new TerminalPanel())
            {
                var output = (RichTextBox)GetField(panel, "_output");
                int inputStart = (int)GetField(panel, "_inputStart");
                output.SelectionStart = inputStart;
                output.SelectionLength = 0;

                var e = new KeyEventArgs(Keys.Back);
                InvokePrivate(panel, "OnOutputKeyDown", new object[] { panel, e });

                Assert.IsTrue(e.SuppressKeyPress,
                    "backspace at the start of the live line must be suppressed — a real console never lets you delete into scrollback");
            }
        }
    }
}
