using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The custom tab strip: arrows at BOTH ends, the native scroller gone entirely.
    ///
    /// The native common control puts its overflow scroller — one control carrying both arrows — at
    /// the right-hand end and nowhere else, which is what put a "scroll left" button on the right of
    /// the strip. These pin the replacement: that no native scroller is ever created, that the two
    /// arrows land on opposite ends, and that a head is never laid out underneath one.
    /// </summary>
    [TestClass]
    public class HelixTabControlTests
    {
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        private delegate bool EnumProc(IntPtr h, IntPtr l);

        private static Form _host;
        private static HelixTabControl _tabs;

        private static HelixTabControl Build(int tabCount, int width, TabAlignment alignment = TabAlignment.Top)
        {
            _host = new Form { Size = new Size(width, 400), StartPosition = FormStartPosition.Manual };
            _tabs = new HelixTabControl { Dock = DockStyle.Fill, Alignment = alignment };
            _host.Controls.Add(_tabs);
            for (int i = 0; i < tabCount; i++)
                _tabs.TabPages.Add(new TabPage("Analysis Results " + i));
            TestWindow.ShowOffScreen(_host);
            Pump();
            return _tabs;
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (_host != null) { _host.Close(); _host.Dispose(); _host = null; _tabs = null; }
        }

        private static void Pump()
        {
            for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
        }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const int HTCLIENT = 1;

        /// <summary>
        /// The strip has to be hit-testable, not merely painted.
        ///
        /// This is the check whose absence let a completely dead tab strip ship: every other test
        /// here posts messages straight to the handle, and a posted message never goes through
        /// hit-testing. The native control answers HTTRANSPARENT for any part of itself its own tab
        /// items do not cover — and those are shrunk to a pixel each — so Windows handed real clicks
        /// to the PARENT window and the heads did nothing at all. Selection worked perfectly when
        /// driven in code, which is exactly why the rest of the suite stayed green.
        /// </summary>
        [TestMethod]
        public void TheStripIsHitTestable_NotJustPainted()
        {
            HelixTabControl tabs = Build(5, 900);

            for (int i = 0; i < tabs.TabCount; i++)
            {
                Rectangle r = tabs.GetTabRect(i);
                Point screen = tabs.PointToScreen(new Point(r.Left + 10, r.Top + r.Height / 2));
                IntPtr lp = (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF));
                int hit = (int)SendMessage(tabs.Handle, WM_NCHITTEST, IntPtr.Zero, lp);

                Assert.AreNotEqual(HTTRANSPARENT, hit,
                    "head " + i + " reports itself transparent, so real clicks fall through to the parent");
                Assert.AreEqual(HTCLIENT, hit, "head " + i + " must hit-test as client area");
            }
        }

        /// <summary>The arrows have to be clickable for the same reason the heads do.</summary>
        [TestMethod]
        public void TheScrollArrowsAreHitTestable()
        {
            HelixTabControl tabs = Build(24, 400);
            Assert.IsTrue(tabs.TabsOverflow);

            foreach (Rectangle arrow in new[] { tabs.LeftArrowBounds, tabs.RightArrowBounds })
            {
                Point screen = tabs.PointToScreen(
                    new Point(arrow.Left + arrow.Width / 2, arrow.Top + arrow.Height / 2));
                IntPtr lp = (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF));
                int hit = (int)SendMessage(tabs.Handle, WM_NCHITTEST, IntPtr.Zero, lp);
                Assert.AreEqual(HTCLIENT, hit, "arrow at " + arrow + " is not clickable");
            }
        }

        /// <summary>The whole point: the common control never gets to create its own arrow pair.</summary>
        [TestMethod]
        public void NoNativeScrollerIsEverCreated_EvenWithFarMoreTabsThanFit()
        {
            HelixTabControl tabs = Build(24, 400);

            int scrollers = 0;
            EnumChildWindows(tabs.Handle, delegate (IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf("updown", StringComparison.OrdinalIgnoreCase) >= 0) scrollers++;
                return true;
            }, IntPtr.Zero);

            Assert.AreEqual(0, scrollers,
                "the native msctls_updown32 is back — it always docks both arrows to the right");
        }

        [TestMethod]
        public void Overflowing_TheLeftArrowIsOnTheLeftEdge_AndTheRightArrowOnTheRight()
        {
            HelixTabControl tabs = Build(24, 400);
            Assert.IsTrue(tabs.TabsOverflow, "24 tabs in 400px must overflow for this to mean anything");

            Rectangle strip = tabs.StripBounds;
            Assert.AreEqual(strip.Left, tabs.LeftArrowBounds.Left, "the left arrow must sit on the LHS");
            Assert.AreEqual(strip.Right, tabs.RightArrowBounds.Right, "the right arrow must sit on the RHS");
            Assert.IsTrue(tabs.LeftArrowBounds.Right <= tabs.RightArrowBounds.Left,
                "the arrows must be at opposite ends, not adjacent");
        }

        [TestMethod]
        public void NotOverflowing_NeitherArrowIsShown()
        {
            HelixTabControl tabs = Build(2, 900);
            Assert.IsFalse(tabs.TabsOverflow);
            Assert.AreEqual(Rectangle.Empty, tabs.LeftArrowBounds);
            Assert.AreEqual(Rectangle.Empty, tabs.RightArrowBounds);
            Assert.AreEqual(tabs.StripBounds, tabs.TabLane, "with no arrows the heads get the whole strip");
        }

        /// <summary>The reserved-lane choice: a head is never laid out under an arrow.</summary>
        [TestMethod]
        public void Overflowing_TheTabLaneIsInsetClearOfBothArrows()
        {
            HelixTabControl tabs = Build(24, 400);
            Rectangle lane = tabs.TabLane;

            Assert.IsFalse(lane.IntersectsWith(tabs.LeftArrowBounds), "the lane runs under the left arrow");
            Assert.IsFalse(lane.IntersectsWith(tabs.RightArrowBounds), "the lane runs under the right arrow");
            Assert.AreEqual(HelixTabControl.ArrowWidth, lane.Left - tabs.StripBounds.Left);
            Assert.AreEqual(HelixTabControl.ArrowWidth, tabs.StripBounds.Right - lane.Right);
        }

        [TestMethod]
        public void ScrollingRightThenLeft_ReturnsToTheStart_AndClampsAtBothEnds()
        {
            HelixTabControl tabs = Build(24, 400);
            Assert.AreEqual(0, tabs.ScrollOffset);

            tabs.ScrollTabsLeft();
            Assert.AreEqual(0, tabs.ScrollOffset, "cannot scroll before the first tab");

            tabs.ScrollTabsRight();
            int after = tabs.ScrollOffset;
            Assert.IsTrue(after > 0, "scrolling right must move the strip");

            for (int i = 0; i < 60; i++) tabs.ScrollTabsRight();
            int end = tabs.ScrollOffset;
            tabs.ScrollTabsRight();
            Assert.AreEqual(end, tabs.ScrollOffset, "must clamp at the far end rather than run on");

            for (int i = 0; i < 80; i++) tabs.ScrollTabsLeft();
            Assert.AreEqual(0, tabs.ScrollOffset, "scrolling back must reach the start exactly");
        }

        [TestMethod]
        public void SelectingATabOffScreen_ScrollsItIntoTheLane()
        {
            HelixTabControl tabs = Build(24, 400);
            tabs.SelectedIndex = 20;
            Pump();

            Rectangle r = tabs.GetTabRect(20);
            Rectangle lane = tabs.TabLane;
            Assert.IsTrue(r.Left >= lane.Left && r.Right <= lane.Right,
                "selecting a tab that is scrolled away must bring it fully into view");
        }

        /// <summary>Hit-testing and painting read the same layout, so they cannot disagree.</summary>
        [TestMethod]
        public void TabIndexAt_MatchesTheDrawnRect_AndTheCloseBoxIsInsideIt()
        {
            HelixTabControl tabs = Build(24, 400);

            for (int i = 0; i < tabs.TabCount; i++)
            {
                Rectangle r = tabs.GetTabRect(i);
                if (!tabs.TabLane.Contains(r)) continue;   // only the fully visible heads are clickable

                Assert.AreEqual(i, tabs.TabIndexAt(new Point(r.Left + 3, r.Top + r.Height / 2)),
                    "the point inside head " + i + " did not hit-test to it");
                Assert.IsTrue(r.Contains(tabs.GetCloseButtonRect(i)),
                    "head " + i + "'s close box escapes the head");
            }
        }

        [TestMethod]
        public void GetTabRect_ReportsTheDrawnHead_NotTheNeutralisedNativeRow()
        {
            HelixTabControl tabs = Build(6, 900);
            Rectangle drawn = tabs.GetTabRect(0);

            Assert.IsTrue(drawn.Width > 20, "a drawn head is wider than the 1px native row item");
            Assert.IsTrue(drawn.Height > 8, "a drawn head is taller than the 1px native row item");
            Assert.IsTrue(tabs.StripBounds.IntersectsWith(drawn), "the head must lie in the strip band");
        }

        /// <summary>TCM_ADJUSTRECT is what buys the strip its band; pages take everything else.</summary>
        [TestMethod]
        public void PagesFillEverythingExceptTheStripBand()
        {
            HelixTabControl tabs = Build(3, 600);
            Rectangle page = tabs.TabPages[0].Bounds;

            Assert.AreEqual(tabs.StripHeight, page.Top, "the page must start just below the strip");
            Assert.AreEqual(tabs.ClientRectangle.Bottom, page.Bottom, "the page must reach the bottom");
            Assert.AreEqual(tabs.ClientRectangle.Width, page.Width, "the page must span the full width");
        }

        [TestMethod]
        public void BottomAlignment_PutsTheStripUnderThePages()
        {
            HelixTabControl tabs = Build(3, 600, TabAlignment.Bottom);

            Assert.AreEqual(tabs.ClientRectangle.Bottom, tabs.StripBounds.Bottom,
                "a bottom-aligned strip sits on the bottom edge — the tool-window idiom");
            Assert.AreEqual(0, tabs.TabPages[0].Bounds.Top, "the pages start at the top instead");
        }

        [TestMethod]
        public void ClickingACloseBox_RaisesTabCloseRequested_ForThatTab()
        {
            HelixTabControl tabs = Build(6, 900);
            TabEventArgs seen = null;
            tabs.TabCloseRequested += delegate (object s, TabEventArgs e) { seen = e; };

            Rectangle close = tabs.GetCloseButtonRect(2);
            var click = new MouseEventArgs(MouseButtons.Left, 1,
                close.Left + close.Width / 2, close.Top + close.Height / 2, 0);
            typeof(HelixTabControl).GetMethod("OnMouseDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(tabs, new object[] { click });

            Assert.IsNotNull(seen, "the ✕ raised nothing");
            Assert.AreEqual(2, seen.Index);
            Assert.AreSame(tabs.TabPages[2], seen.Page);
        }
    }
}
