using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// A tool-window caption must repaint in FULL when it is resized.
    ///
    /// Reported from a live session: a bright vertical hairline stranded in the middle of the
    /// Solution Explorer's title bar. Its colour was an exact match for the caption's 1px inner
    /// light, which is drawn in one place only — the right-hand end of the bar — so what was on
    /// screen was the edge of a NARROWER caption that had never been painted over. Widening a plain
    /// Panel invalidates only the strip the resize newly exposes; everything the bar drew from its
    /// old width stays exactly where it was.
    /// </summary>
    [TestClass]
    public class ToolWindowCaptionRepaintTests
    {
        private static bool HasResizeRedraw(Control c)
        {
            MethodInfo getStyle = typeof(Control)
                .GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic);
            return (bool)getStyle.Invoke(c, new object[] { ControlStyles.ResizeRedraw });
        }

        private static Panel HeaderOf(DockablePanel panel)
        {
            FieldInfo f = typeof(DockablePanel)
                .GetField("_headerPanel", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_headerPanel field expected.");
            return (Panel)f.GetValue(panel);
        }

        [TestMethod]
        public void TheDockedCaption_RepaintsWholeOnResize()
        {
            using (var panel = new DockablePanel())
            {
                Assert.IsTrue(HasResizeRedraw(HeaderOf(panel)),
                    "the caption paints from its own width, so a resize must invalidate all of it");
            }
        }

        [TestMethod]
        public void TheDesignerToolWindowCaption_RepaintsWholeOnResize()
        {
            using (var header = new ToolWindowHeader("Toolbox", null))
            {
                Assert.IsTrue(HasResizeRedraw(header),
                    "the designer's tool-window bar is painted too and needs the same guarantee");
            }
        }

        /// <summary>
        /// The end state the style exists to guarantee: after a widening, some paint pass covers the
        /// column where the old right edge sat.
        ///
        /// This is a weaker check than the two above and does NOT fail on its own if ResizeRedraw is
        /// removed — WinForms happens to invalidate from the old docked-button positions, which for
        /// a three-button caption already reaches that column. It is here to catch the case where
        /// the caption's own geometry changes such that it no longer does.
        /// </summary>
        [TestMethod]
        public void AfterWidening_ThePreviousRightEdgeColumnIsRepainted()
        {
            using (var host = new Form())
            using (var panel = new DockablePanel())
            {
                host.Size = new Size(700, 400);
                panel.Dock = DockStyle.Top;
                panel.Height = 200;
                host.Controls.Add(panel);
                TestWindow.ShowOffScreen(host);

                panel.Width = 240;
                Pump();

                Panel header = HeaderOf(panel);
                int oldEdge = header.Width - 1;

                var clips = new List<Rectangle>();
                PaintEventHandler record = delegate (object s, PaintEventArgs e) { clips.Add(e.ClipRectangle); };
                header.Paint += record;

                panel.Width = 520;
                Pump();
                header.Paint -= record;

                bool covered = false;
                foreach (Rectangle r in clips)
                    if (r.Left <= oldEdge && r.Right > oldEdge) covered = true;

                Assert.IsTrue(covered,
                    "nothing repainted x=" + oldEdge + ", so the old caption edge is still on screen");
                host.Close();
            }
        }

        private static void Pump()
        {
            for (int i = 0; i < 12; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(15);
            }
        }
    }
}
