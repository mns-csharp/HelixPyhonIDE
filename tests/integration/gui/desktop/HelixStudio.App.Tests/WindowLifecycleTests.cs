using System;
using System.Drawing;
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
    /// The three ways out of the shell window, which are three different things:
    /// minimize goes to the TASKBAR, the title-bar [X] goes to the SYSTEM TRAY, and File ▸ Exit
    /// ends the process.
    ///
    /// <para>
    /// Every test here realizes the form through <see cref="FixtureSolution.RealizeHeadless"/> and
    /// never calls <c>Show()</c>. Two reasons, both learned this session:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Showing puts a real IDE window on the developer's desktop.</b> The
    /// <c>Location = (-4000, -4000)</c> trick does NOT work for a <c>LunaForm</c> descendant —
    /// <c>LunaForm.OnShown</c> clamps <c>Bounds</c> into <c>Screen.FromRectangle(Bounds).WorkingArea</c>,
    /// so the window is yanked straight back onto the visible screen. Measured: a form
    /// parked at -4000 came up at (0,0)-(1200,800).
    /// </description></item>
    /// <item><description>
    /// <b>RealizeHeadless installs the WindowsFormsSynchronizationContext.</b> Without it, MSTest
    /// leaves <c>SynchronizationContext.Current</c> null, every <c>await</c> inside
    /// <c>OpenSolutionAsync</c> resumes on a thread-pool thread, and controls created there crash
    /// the test host outright.
    /// </description></item>
    /// </list>
    /// <para>
    /// The form still gets a real window handle, which is what matters here: <c>Close()</c> raises
    /// <c>FormClosing</c> for a realized form, so the tray diversion is genuinely exercised. What is
    /// deliberately NOT asserted is the taskbar BUTTON itself — that would need a visible window,
    /// which this suite forbids. What is asserted instead is everything the product code controls:
    /// that minimize neither clears <c>ShowInTaskbar</c>, nor hides the form, nor creates a tray
    /// icon. Those are the three ways a "minimize to tray" change breaks requirement 1.
    /// </para>
    /// </summary>
    [TestClass]
    public class WindowLifecycleTests
    {
        private string _stateDir;
        private string _prevState;

        [TestInitialize]
        public void Setup()
        {
            // Never let a composed MainForm write the developer's real %APPDATA%\HelixPython profile.
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_winlife_" + Guid.NewGuid().ToString("N"));
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

        private static MainForm Realized()
        {
            MainForm form = TestMainForm.CreateComposed();
            FixtureSolution.RealizeHeadless(form);
            // RealizeHeadless clears ShowInTaskbar so the headless form cannot appear on the
            // taskbar. Requirement 1 is a claim ABOUT that flag, so put it back to the value the
            // shipped shell runs with — otherwise the assertions below would be measuring the
            // harness rather than the product.
            form.ShowInTaskbar = true;
            Application.DoEvents();
            return form;
        }

        /// <summary>
        /// A genuinely visible shell window. Used by the two minimize tests ONLY.
        ///
        /// <para>
        /// Requirement 1 is a claim about a window the user can see and click on the taskbar, and
        /// <c>Control.Visible</c> is false for a form that was never shown — so those two tests
        /// cannot be written headless without asserting nothing. Every other test in this class uses
        /// <see cref="Realized"/> and shows nothing.
        /// </para>
        /// <para>
        /// Shown through <c>TestWindow.ShowOffScreen</c> (book Chapter 4, §4.6.5): transparent, off every
        /// screen and never in front, although <c>LunaForm.OnShown</c> clamps <c>Bounds</c> back into
        /// <c>Screen.FromRectangle(Bounds).WorkingArea</c> for a plain Show.
        /// </para>
        /// </summary>
        private static MainForm Shown()
        {
            MainForm form = Realized();
            form.Size = new Size(800, 600);
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
            return form;
        }

        // ---- 1. Minimize -----------------------------------------------------------------------

        [TestMethod]
        public void Minimizing_LeavesTheWindowOnTheTaskbar_AndOutOfTheTray()
        {
            using (MainForm form = Shown())
            {
                form.WindowState = FormWindowState.Minimized;
                Application.DoEvents();

                Assert.AreEqual(FormWindowState.Minimized, form.WindowState, "it minimized");
                // The two properties that decide whether a minimized window has a taskbar button.
                // A form that is Visible with ShowInTaskbar is on the taskbar; hiding it or clearing
                // that flag — the two usual ways of implementing "minimize to tray" — is what this
                // pins shut.
                Assert.IsTrue(form.ShowInTaskbar, "a minimized IDE keeps its taskbar button");
                Assert.IsTrue(form.Visible, "minimize must not HIDE the window");
                Assert.IsFalse(form.IsHiddenToTray, "minimize is not the tray gesture");
                Assert.IsNull(form.TrayIcon, "minimize must not put an icon in the notification area");

                // Not asserted: that a taskbar button is really painted. That needs a visible
                // window, and this suite forbids one. WS_EX_TOOLWINDOW is checkable headless
                // though, and it is the one window style that would remove the button outright
                // however the rest of the code behaves.
                Assert.AreEqual(0, GetWindowLong(form.Handle, GWL_EXSTYLE) & WS_EX_TOOLWINDOW,
                    "the shell is not a tool window — a tool window gets no taskbar button at all");
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [TestMethod]
        public void RestoringFromTheTaskbar_GivesTheWindowItsGeometryBack()
        {

            // The shell hand-draws its own frame and takes the whole window as client area
            // (LunaForm's WM_NCCALCSIZE), which is the kind of chrome where a minimize/restore round
            // trip can come back the wrong size. Restoring is the half of "minimize works" that a
            // WindowState assertion alone does not cover.
            using (MainForm form = Shown())
            {
                Rectangle before = form.Bounds;

                form.WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                form.WindowState = FormWindowState.Normal;
                Application.DoEvents();

                Assert.AreEqual(before, form.Bounds, "the window comes back the size it went away");
                Assert.IsTrue(form.ShowInTaskbar);
                Assert.IsTrue(form.Visible);
            }
        }

        [TestMethod]
        public void TheCaptionsMinimizeButtonIsTheOneThatMinimizes()
        {
            // The shell wears hand-drawn chrome (LunaForm), so the caption's minimize button is a
            // painted rectangle rather than a real system button — its handler is what the user
            // actually clicks. Drive that handler, not the WindowState property, or this test would
            // pass on a window whose button does nothing.
            using (MainForm form = Realized())
            {
                MethodInfo perform = FindCaptionAction(form.GetType());
                Assert.IsNotNull(perform, "LunaForm.PerformCaptionAction still exists");

                perform.Invoke(form, new object[] { 0 });   // 0 = minimize, 1 = restore, 2 = close
                Application.DoEvents();

                Assert.AreEqual(FormWindowState.Minimized, form.WindowState,
                    "the caption's minimize button minimizes the window");
                Assert.IsTrue(form.ShowInTaskbar);
                Assert.IsFalse(form.IsHiddenToTray);
            }
        }

        private static MethodInfo FindCaptionAction(Type t)
        {
            for (Type cursor = t; cursor != null; cursor = cursor.BaseType)
            {
                MethodInfo m = cursor.GetMethod("PerformCaptionAction",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m != null) return m;
            }
            return null;
        }

        // ---- 3. The title-bar [X] --------------------------------------------------------------

        [TestMethod]
        public void TheTitleBarClose_HidesToTheTray_AndTheProcessKeepsRunning()
        {
            MainForm form = Realized();
            try
            {
                form.CloseToTrayEnabled = true;

                form.Close();                  // exactly what LunaForm's [X] does
                Application.DoEvents();

                Assert.IsFalse(form.IsDisposed, "the IDE is still alive — nothing was thrown away");
                Assert.IsFalse(form.Visible, "the window is off the screen and off the taskbar");
                Assert.IsTrue(form.IsHiddenToTray);
                Assert.IsNotNull(form.TrayIcon, "there is something to click to get it back");
                Assert.IsTrue(form.TrayIcon.Visible, "and it is actually in the notification area");
            }
            finally
            {
                // Dispose, not Close: Dispose does not raise FormClosing, so cleanup cannot be
                // diverted back into the tray it is trying to clean up.
                form.Dispose();
            }
        }

        [TestMethod]
        public void RestoringFromTheTray_BringsTheWindowBack()
        {
            MainForm form = Realized();
            try
            {
                // Shown first, as the user's window is: else RestoreFromTray's Show is the first
                // one, and LunaForm.OnShown puts the IDE on the desktop (book Chapter 4, 4.6.5).
                TestWindow.ShowOffScreen(form);
                form.CloseToTrayEnabled = true;
                form.Close();
                Application.DoEvents();
                Assert.IsTrue(form.IsHiddenToTray, "precondition: it is in the tray");

                form.RestoreFromTray();
                Application.DoEvents();

                Assert.IsTrue(form.Visible, "the window is back");
                Assert.IsTrue(form.ShowInTaskbar, "and back on the taskbar with it");
                Assert.AreNotEqual(FormWindowState.Minimized, form.WindowState,
                    "restoring from the tray must not leave it minimized");
                Assert.IsFalse(form.IsHiddenToTray);
                Assert.IsFalse(form.TrayIcon.Visible, "the tray icon retires once the window is back");
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void MinimizeThenClose_ThenRestore_ComesBackAsANormalWindow()
        {
            // The combination that a naive Show() gets wrong: the window is still flagged Minimized
            // while it sits in the tray, so Show() alone would restore it straight back to the
            // taskbar as a minimized window and the click would look like it did nothing.
            MainForm form = Realized();
            try
            {
                TestWindow.ShowOffScreen(form);   // see RestoringFromTheTray_BringsTheWindowBack
                form.CloseToTrayEnabled = true;
                form.WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                form.Close();
                Application.DoEvents();
                Assert.IsTrue(form.IsHiddenToTray);

                form.RestoreFromTray();
                Application.DoEvents();

                Assert.IsTrue(form.Visible);
                Assert.AreEqual(FormWindowState.Normal, form.WindowState);
            }
            finally { form.Dispose(); }
        }

        [TestMethod]
        public void AHostThatDidNotOptIn_ClosesNormally()
        {
            // The gate that keeps this suite usable. Every test that closes a composed MainForm to
            // dispose it sends CloseReason.UserClosing — the same reason the [X] sends. If the tray
            // divert were on by default, each of those would leak a live window and a tray icon into
            // a process with no message loop left to service them.
            MainForm form = Realized();
            Assert.IsFalse(form.CloseToTrayEnabled, "off unless Program.Main turns it on");

            form.Close();
            Application.DoEvents();

            Assert.IsTrue(form.IsDisposed, "an un-opted-in host gets a plain close");
            Assert.IsNull(form.TrayIcon);
        }

        // ---- 2. File ▸ Exit --------------------------------------------------------------------

        [TestMethod]
        public void FileExit_ShutsDownEvenThoughTheCloseGestureWouldNot()
        {
            MainForm form = Realized();
            form.CloseToTrayEnabled = true;

            ToolStripMenuItem exit = FindExitMenuItem(form);
            Assert.IsNotNull(exit, "File ▸ Exit exists");
            exit.PerformClick();
            Application.DoEvents();

            // The whole point of requirement 2: the same window that survives [X] does NOT survive
            // this. If Exit still routed to Close(), the form would be alive in the tray here.
            Assert.IsTrue(form.IsDisposed, "File ▸ Exit really closes the IDE");
            Assert.IsFalse(form.IsHiddenToTray, "and does not divert to the notification area");
        }

        [TestMethod]
        public void ExitFromTheTray_ShutsDownToo()
        {
            MainForm form = Realized();
            TestWindow.ShowOffScreen(form);     // ExitApplication restores from the tray first: see RestoringFromTheTray_BringsTheWindowBack
            form.CloseToTrayEnabled = true;
            form.Close();                       // park it in the tray first
            Application.DoEvents();
            Assert.IsTrue(form.IsHiddenToTray, "precondition");

            form.ExitApplication();
            Application.DoEvents();

            Assert.IsTrue(form.IsDisposed, "the tray menu's Exit ends the session from the tray");
        }

        private static ToolStripMenuItem FindExitMenuItem(MainForm form)
        {
            if (form.MainMenuStrip == null) return null;
            foreach (ToolStripMenuItem top in form.MainMenuStrip.Items.OfType<ToolStripMenuItem>())
            {
                foreach (ToolStripMenuItem item in top.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    // "E&xit" — compare on the mnemonic-stripped text so a change of accelerator
                    // does not silently turn this test into a no-op.
                    if (string.Equals(item.Text.Replace("&", string.Empty), "Exit",
                                      StringComparison.OrdinalIgnoreCase))
                        return item;
                }
            }
            return null;
        }
    }
}
