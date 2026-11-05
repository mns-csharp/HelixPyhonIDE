using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.TestSupport
{
    /// <summary>
    /// The only way a test shows a window (book Chapter 4, §4.6.5). Linked into every test project
    /// that shows one; <c>TestWindowGuardTests</c> in App.Tests fails on any other Show.
    ///
    /// <para>
    /// A plain <c>Show()</c> is not safe, even at (-4000, -4000). <c>MainForm</c> and every dialog are
    /// <c>LunaForm</c>s, and <c>LunaForm.OnShown</c> pulls a window shown off-screen back onto the
    /// nearest screen. So the window sat at (0,0), opaque, under the user's real mouse and keyboard:
    /// D-CARDMOVE, a real drag moved a CG card during a test. A plain <c>Show()</c> also
    /// activates the window, and measured it took the foreground from the user.
    /// </para>
    /// <para>
    /// Here the window is fully transparent before it exists on screen (so also click-through), it
    /// is shown with <c>SW_SHOWNOACTIVATE</c> (not activated), and any move onto a screen while
    /// <c>Shown</c> runs is undone at once. It also gets <c>WS_EX_NOACTIVATE</c>: a later
    /// <c>control.Focus()</c> in the test still activates the window and focuses the control inside
    /// the test's own thread, but the window never becomes the foreground window, so it never takes
    /// the keyboard from the user (measured: without the style, <c>Focus()</c> did).
    /// The style also keeps the window off the taskbar. <c>LockSetForegroundWindow</c> covers the
    /// rest (see below). <c>DrawToBitmap</c> still renders
    /// real pixels at <c>Opacity = 0</c> (measured), so <c>Opacity</c> stays 0.
    /// </para>
    /// </summary>
    internal static class TestWindow
    {
        public static readonly Point OffScreen = new Point(-4000, -4000);

        private const int SW_SHOWNOACTIVATE = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool LockSetForegroundWindow(uint uLockCode);
        private const uint LSFW_LOCK = 1;

        /// <summary>
        /// Shows <paramref name="form"/> (owned by <paramref name="owner"/> when given) realized,
        /// off every screen, transparent and not activated. Returns after <c>Shown</c> has run.
        /// A location that is already off every screen is kept (an owned dialog keeps its offset
        /// from its owner); any other is replaced by <see cref="OffScreen"/>.
        /// </summary>
        public static void ShowOffScreen(Form form, Form owner = null)
        {
            // WS_EX_NOACTIVATE does not stop WindowState = Normal after a minimize, or an Activate()
            // (the product's RestoreFromTray calls both): measured, each took the
            // foreground. This lock stops this process from taking it at all. It fails harmlessly
            // when the process has no foreground right; Windows lifts it at the user's next click
            // or Alt and when the process ends, so the user's own window switching is untouched.
            LockSetForegroundWindow(LSFW_LOCK);
            if (owner != null) form.Owner = owner;
            Point park = form.StartPosition == FormStartPosition.Manual && !IsOnAnyScreen(form.Bounds)
                ? form.Location : OffScreen;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = park;
            form.Opacity = 0;

            bool shown = false;
            EventHandler onShown = (s, e) => shown = true;
            EventHandler keepOff = (s, e) => { if (IsOnAnyScreen(form.Bounds)) form.Location = park; };
            form.Shown += onShown;
            form.LocationChanged += keepOff;    // LunaForm.OnShown's clamp is undone inside the same call
            try
            {
                IntPtr hwnd = form.Handle;
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);          // WinForms then sees the form as visible,
                form.Show();                                  // so Show runs Load and posts Shown only
                for (int i = 0; i < 100 && !shown; i++)
                {
                    Application.DoEvents();
                    if (!shown) Thread.Sleep(2);
                }
            }
            finally
            {
                form.Shown -= onShown;
                form.LocationChanged -= keepOff;
            }
            if (IsOnAnyScreen(form.Bounds)) form.Location = park;
        }

        /// <summary>
        /// For a window the test creates but product code shows (a data tip placed at a screen
        /// point, a popup the product pulls back onto the screen): fully transparent and
        /// click-through, so where the product puts it does not matter to the user.
        /// </summary>
        public static T Transparent<T>(T form) where T : Form
        {
            LockSetForegroundWindow(LSFW_LOCK);
            form.Opacity = 0;
            return form;
        }

        /// <summary>
        /// Opens <paramref name="item"/>'s dropdown transparent. WinForms keeps a dropdown inside
        /// the screen work area even when its owner is off every screen, so it cannot be parked.
        /// </summary>
        public static void ShowDropDown(ToolStripDropDownItem item)
        {
            item.DropDown.Opacity = 0;
            item.DropDown.DropShadowEnabled = false;   // the system shadow is its own opaque window
            item.ShowDropDown();
        }

        public static bool IsOnAnyScreen(Rectangle bounds)
        {
            foreach (Screen screen in Screen.AllScreens)
                if (screen.Bounds.IntersectsWith(bounds)) return true;
            return false;
        }
    }
}
