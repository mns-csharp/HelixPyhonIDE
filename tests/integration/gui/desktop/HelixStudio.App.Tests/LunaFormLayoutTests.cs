using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 249: LunaForm draws its caption inside the client area (WM_NCCALCSIZE removes the native
    /// non-client area), so absolutely-positioned designer controls at small Y used to render UNDER
    /// the caption, and nothing kept a dialog on the visible desktop. Two fixes: reparent children
    /// into a Dock=Fill host below the caption, and clamp the shown window into the work area.
    /// </summary>
    [TestClass]
    public class LunaFormLayoutTests
    {
        // ---- ClampToArea (pure geometry: shrink to fit, then slide fully inside) ----

        [TestMethod]
        public void ClampToArea_TallerThanArea_ShrinksHeight()
        {
            var r = LunaForm.ClampToArea(new Rectangle(0, 0, 500, 2000), new Rectangle(0, 0, 1000, 1000));
            Assert.AreEqual(new Rectangle(0, 0, 500, 1000), r);
        }

        [TestMethod]
        public void ClampToArea_OffRightAndBottom_SlidesInside()
        {
            var r = LunaForm.ClampToArea(new Rectangle(900, 900, 400, 400), new Rectangle(0, 0, 1000, 1000));
            Assert.AreEqual(new Rectangle(600, 600, 400, 400), r);
        }

        [TestMethod]
        public void ClampToArea_FullyInside_Unchanged()
        {
            var inside = new Rectangle(100, 100, 300, 300);
            Assert.AreEqual(inside, LunaForm.ClampToArea(inside, new Rectangle(0, 0, 1000, 1000)));
        }

        [TestMethod]
        public void ClampToArea_OffLeftEdge_SlidesRight()
        {
            var r = LunaForm.ClampToArea(new Rectangle(-2000, 100, 400, 300), new Rectangle(0, 0, 1920, 1080));
            Assert.AreEqual(new Rectangle(0, 100, 400, 300), r);
        }

        [TestMethod]
        public void ClampToArea_OnSecondMonitorToTheLeft_StaysThere()
        {
            // A left-hand monitor at negative coordinates: a window already fully inside is untouched.
            var window = new Rectangle(-1800, 100, 400, 300);
            var leftMonitor = new Rectangle(-1920, 0, 1920, 1080);
            Assert.AreEqual(window, LunaForm.ClampToArea(window, leftMonitor));
        }

        // ---- Reparenting: an absolutely-positioned child lands in the client host below the caption ----

        private sealed class ProbeForm : LunaForm
        {
            public ProbeForm()
            {
                ClientSize = new Size(300, 200);
                Controls.Add(new Label { Name = "probe", Text = "top", Location = new Point(10, 5), AutoSize = true });
            }
        }

        [TestMethod]
        public void AbsoluteChild_IsReparented_IntoClientHostBelowCaption()
        {
            using (var f = new ProbeForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000); // off-screen so nothing flashes
                f.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(f);
                try
                {
                    Application.DoEvents();

                    Label probe = FindByName(f, "probe");
                    Assert.IsNotNull(probe, "probe label should still exist after reparenting");

                    // Its parent must be the Dock=Fill host panel, not the form itself.
                    Assert.AreNotSame(f, probe.Parent, "child must no longer be a direct child of the form");
                    Assert.IsInstanceOfType(probe.Parent, typeof(Panel));

                    // The host sits below the caption, so a Location.Y of 5 no longer collides with it.
                    var host = (Control)probe.Parent;
                    Assert.IsTrue(host.Top > 0, "client host must be offset below the hand-drawn caption");
                }
                finally { f.Close(); }
            }
        }

        [TestMethod]
        public void MaximizedForm_FitsTheWorkArea_NotBeyondIt()
        {
            using (var f = new ProbeForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(f);
                try
                {
                    Application.DoEvents();
                    // The one test window that must sit on a screen: the claim is about the work area.
                    // It stays at Opacity 0 (invisible, click-through) and WS_EX_NOACTIVATE keeps it
                    // out of the foreground (book Chapter 4, 4.6.5).
                    f.WindowState = FormWindowState.Maximized;
                    Application.DoEvents();

                    Rectangle wa = Screen.FromControl(f).WorkingArea;
                    // The content (client area) is what must stay on-screen. A maximized frameless
                    // window's rect overhangs by the resize border, but WM_NCCALCSIZE pins the client
                    // to the work area, so no content spills off the desktop / under the taskbar.
                    Rectangle client = f.RectangleToScreen(f.ClientRectangle);
                    Assert.IsTrue(client.Left >= wa.Left - 1 && client.Top >= wa.Top - 1,
                        "client top-left " + client.Location + " must be within work area " + wa);
                    Assert.IsTrue(client.Right <= wa.Right + 1 && client.Bottom <= wa.Bottom + 1,
                        "client bottom-right must be within the work area (was " + client + ", wa " + wa + ")");
                }
                finally { f.Close(); }
            }
        }

        [TestMethod]
        public void ShownForm_HasRoundedCornerRegion()
        {
            using (var f = new ProbeForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(f);
                try
                {
                    Application.DoEvents();
                    Assert.IsNotNull(f.Region, "a non-maximized Luna form must have a rounded-corner region");
                }
                finally { f.Close(); }
            }
        }

        private static Label FindByName(Control root, string name)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Label l && l.Name == name) return l;
                Label found = FindByName(c, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
