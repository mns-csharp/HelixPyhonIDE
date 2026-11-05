using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The GUI Designer Canvas's Toolbox and Properties are tool windows, and they sit on screen beside
    /// the docked ones. They wore a plain bold label where their neighbours wore a title bar with a
    /// ✕ — near-identical chrome that isn't identical reads worse than chrome that is plainly
    /// different, and the two could not be closed at all.
    /// </summary>
    [TestClass]
    public class GuiDesignerToolWindowTests
    {
        private static GuiDesignerForm NewDesigner()
        {
            var designer = new GuiDesignerForm();
            TestWindow.ShowOffScreen(designer);
            Application.DoEvents();
            return designer;
        }

        private static Panel PanelNamed(Control root, string title)
        {
            foreach (ToolWindowHeader header in Headers(root))
            {
                var label = header.Controls.OfType<Label>().FirstOrDefault();
                if (label != null && label.Text == title) return header.Parent as Panel;
            }
            return null;
        }

        // A tool-window header: a Top-docked panel of the shared height carrying the Marlett ✕.
        private static IEnumerable<ToolWindowHeader> Headers(Control root)
        {
            var header = root as ToolWindowHeader;
            if (header != null) yield return header;
            foreach (Control child in root.Controls)
                foreach (ToolWindowHeader found in Headers(child))
                    yield return found;
        }

        private static Button CloseButton(ToolWindowHeader header)
        {
            return header.CloseButton;
        }

        [TestMethod]
        public void BothDesignerToolWindowsWearTheDockedPanelsTitleBar()
        {
            using (GuiDesignerForm designer = NewDesigner())
            {
                foreach (string title in new[] { "Toolbox", "Properties" })
                {
                    Panel owner = PanelNamed(designer, title);
                    Assert.IsNotNull(owner, title + " has no tool-window title bar with a close button");

                    ToolWindowHeader header = Headers(designer).First(h => ReferenceEquals(h.Parent, owner));
                    Assert.AreEqual(ToolWindowChrome.HeaderBack, header.BackColor,
                        title + "'s title bar is not the docked panels' colour");
                    Assert.AreEqual(ToolWindowChrome.HeaderHeight, header.Height,
                        title + "'s title bar is not the docked panels' height");

                    Button close = CloseButton(header);
                    Assert.AreEqual("Marlett", close.Font.Name, title + "'s ✕ is not the system glyph");
                    Assert.IsTrue(close.Right > header.Width - ToolWindowChrome.ButtonWidth - 8,
                        title + "'s ✕ is not on the right-hand edge of the bar");
                }
                designer.Close();
            }
        }

        /// <summary>
        /// Closing hides; the strip's toggle brings it back. A tool window that closes and cannot be
        /// reopened is a trap, and hiding rather than disposing is also what keeps the search text
        /// and the selection across the round trip.
        ///
        /// <para>This used to go through View ▸ Toolbox on the designer's own menu bar. That bar is
        /// gone — the commands are icons on the shared design strip now — so the switch is a button
        /// that stays pressed while the window is open. What is asserted has not changed.</para>
        /// </summary>
        [TestMethod]
        public void AClosedDesignerToolWindowComesBackFromItsToggle()
        {
            using (GuiDesignerForm designer = NewDesigner())
            {
                Panel toolbox = PanelNamed(designer, "Toolbox");
                Assert.IsNotNull(toolbox);
                Assert.IsTrue(toolbox.Visible, "precondition: the Toolbox starts open");

                CloseButton(Headers(designer).First(h => ReferenceEquals(h.Parent, toolbox))).PerformClick();
                Application.DoEvents();
                Assert.IsFalse(toolbox.Visible, "the ✕ did not close the Toolbox");

                ToolStripButton item = ToolWindowToggle(designer, "Toolbox");
                Assert.IsNotNull(item, "there is no toggle to bring the Toolbox back");
                Assert.IsFalse(item.Checked, "the toggle still claims the Toolbox is open");

                // CheckOnClick flips Checked before the Click handler runs, so a click is the whole
                // gesture — setting Checked first would flip it straight back off.
                item.PerformClick();
                Application.DoEvents();
                Assert.IsTrue(toolbox.Visible, "the toggle did not bring the Toolbox back");

                designer.Close();
            }
        }

        /// <summary>The strip's on/off button for a named tool window, wherever the strip ended up
        /// — it lives inside LunaForm's client host, so this recurses.</summary>
        private static ToolStripButton ToolWindowToggle(Control root, string text)
        {
            var strip = root as ToolStrip;
            if (strip != null)
                foreach (ToolStripButton button in strip.Items.OfType<ToolStripButton>())
                    if (button.ToolTipText == text) return button;

            foreach (Control child in root.Controls)
            {
                ToolStripButton found = ToolWindowToggle(child, text);
                if (found != null) return found;
            }
            return null;
        }

    }
}
