namespace HelixPythonNamespace.App
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// The shell window's lifecycle: minimize, the title-bar close, and the explicit File &#8250; Exit.
    ///
    /// <para>
    /// Three gestures that a plain WinForms shell treats as one. Here they are three:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Minimize</b> is ordinary Windows minimize — the window goes to the TASKBAR, not the tray.
    /// Nothing in this file touches it; it is called out because the tray behaviour below is exactly
    /// the kind of change that accidentally swallows it (setting <c>ShowInTaskbar = false</c> for the
    /// tray, or hiding the form from <c>OnResize</c>, both do). <see cref="OnResize"/> asserts the
    /// separation rather than leaving it to be re-broken later.
    /// </description></item>
    /// <item><description>
    /// <b>The title-bar [X]</b> does not quit. The IDE holds a solution, open documents, a debug
    /// session and a running AI terminal, and a user reaching for [X] mid-session usually means
    /// "get off my screen", not "throw all of that away". So it hides to the notification area and
    /// keeps running.
    /// </description></item>
    /// <item><description>
    /// <b>File &#8250; Exit</b> — and the tray icon's own Exit — is the deliberate one, and it really
    /// quits: it sets <see cref="_exitRequested"/> so <c>OnFormClosing</c> stops diverting to the
    /// tray, and the normal shutdown path (save settings, stop jobs, kill the debuggee) runs once
    /// and returns from <c>Application.Run</c>.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// Alt+F4 is deliberately treated as the title-bar [X], because it is: Windows delivers both as
    /// <see cref="CloseReason.UserClosing"/>, and telling them apart would need a fragile read of
    /// which code path called <c>Close()</c>. The IDE stays reachable in the tray either way, so the
    /// failure mode of getting this "wrong" is one click, not lost work.
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private NotifyIcon _trayIcon;

        /// <summary>Set once by File &#8250; Exit (or the tray menu) so the next close is a real close.</summary>
        private bool _exitRequested;

        private bool _hiddenToTray;

        /// <summary>The "still running down here" balloon is a one-off — after that the user knows.</summary>
        private bool _trayHintShown;

        /// <summary>
        /// Whether the title-bar close hides to the tray instead of quitting. OFF by default, and
        /// turned on by <c>Program.Main</c> — the real shell — alone.
        ///
        /// <para>
        /// This is not a user preference; it is a statement about the HOST. A test or a screenshot
        /// harness composes a real <see cref="MainForm"/> and then calls <c>Close()</c> to dispose
        /// it, which arrives as <see cref="CloseReason.UserClosing"/> exactly like the [X] does.
        /// Defaulting this on would turn every one of those into a cancelled close plus a tray icon
        /// in a process with no message loop left to service it — a leaked window per test, and a
        /// row of ghost icons in the developer's notification area.
        /// </para>
        /// </summary>
        internal bool CloseToTrayEnabled { get; set; }

        /// <summary>
        /// Put the fourth caption button on the title bar and point it at the tray.
        ///
        /// <para>Until this existed, <see cref="HideToTray"/> was reachable only by pressing [X],
        /// because <c>Program.Main</c> set <see cref="CloseToTrayEnabled"/>. That made the close
        /// button mean "hide" — so a user who wanted to close the IDE could not, and a user who
        /// wanted it in the tray had to learn that the X did something other than what it says.
        /// The two actions are now two buttons: [tray] hides, [X] shuts down.</para>
        /// </summary>
        private void EnableTrayCaptionButton()
        {
            ShowTrayButton = true;
            TrayRequested += delegate { HideToTray(); };
        }

        /// <summary>True while the window is parked in the notification area.</summary>
        internal bool IsHiddenToTray { get { return _hiddenToTray; } }

        /// <summary>The notification-area icon, or null until the first hide. Exposed for tests.</summary>
        internal NotifyIcon TrayIcon { get { return _trayIcon; } }

        /// <summary>
        /// True when this close should be diverted to the notification area rather than performed.
        ///
        /// <para>
        /// Only a USER close is diverted. <c>Application.Exit</c>, a Windows shutdown, Task Manager
        /// and an owner-form close all mean the process is going away for reasons the user cannot
        /// take back by clicking a tray icon, so those close for real. So does any close that some
        /// other handler has already cancelled — <c>e.Cancel</c> is someone else's decision and this
        /// method does not overturn it.
        /// </para>
        /// </summary>
        private bool ShouldHideInsteadOfClosing(FormClosingEventArgs e)
        {
            return CloseToTrayEnabled
                && !_exitRequested
                && e != null
                && !e.Cancel
                && e.CloseReason == CloseReason.UserClosing;
        }

        /// <summary>
        /// File &#8250; Exit, and the tray menu's Exit: shut the IDE down now.
        ///
        /// <para>
        /// The flag is what makes this different from every other <c>Close()</c> in the app — it is
        /// read by <c>OnFormClosing</c>, which would otherwise cancel and hide. <c>Close()</c> then
        /// runs the one real shutdown path (breakpoints, modified documents, settings, layout, the
        /// debug session, background jobs) and returns from <c>Application.Run</c>, ending the
        /// process. The window is shown first when it is in the tray: an invisible form still
        /// closes correctly, but a shutdown that prompts — an engine refusing to stop, an
        /// unwritable settings file — would otherwise put its dialog on screen with nothing behind
        /// it.
        /// </para>
        /// </summary>
        internal void ExitApplication()
        {
            _exitRequested = true;
            if (_hiddenToTray) RestoreFromTray();
            Close();
        }

        /// <summary>
        /// Park the window in the notification area: the process, the solution, the debug session
        /// and every open document stay exactly as they are.
        ///
        /// <para>
        /// <c>Hide()</c> is what removes the taskbar button — deliberately, rather than
        /// <c>ShowInTaskbar = false</c>, which WinForms implements by recreating the window handle
        /// and would tear down and rebuild every child control (including the Direct3D surface the
        /// PDB viewer builds in <c>OnHandleCreated</c>) on a gesture that is meant to be free.
        /// </para>
        /// </summary>
        internal void HideToTray()
        {
            EnsureTrayIcon();
            _hiddenToTray = true;
            _trayIcon.Visible = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                try
                {
                    _trayIcon.ShowBalloonTip(
                        3000,
                        ProductName,
                        ProductName + " is still running. Click here to bring it back, or right-click "
                            + "for Exit.",
                        ToolTipIcon.Info);
                }
                catch
                {
                    // A balloon is a courtesy — a shell that refuses to show one (policy, a locked
                    // -down session) must not stop the window from hiding.
                }
            }
        }

        /// <summary>
        /// Bring the window back from the notification area, whatever state it left in.
        ///
        /// <para>
        /// The <see cref="FormWindowState.Minimized"/> check is not redundant: minimize-then-[X]
        /// hides a window that is still flagged minimized, and <c>Show()</c> alone would restore it
        /// straight back to the taskbar as a minimized window — the click would look like it did
        /// nothing. <see cref="Form.Activate"/> is what takes focus; <c>BringToFront</c> alone
        /// raises the window without giving it the keyboard.
        /// </para>
        /// </summary>
        internal void RestoreFromTray()
        {
            _hiddenToTray = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        /// <summary>
        /// Create the notification-area icon on first use.
        ///
        /// <para>
        /// Lazy because most sessions end with File &#8250; Exit and never need one, and an icon
        /// registered at startup would sit in the tray for the whole session saying nothing. It is
        /// owned by <c>components</c> so <c>MainForm.Dispose</c> retires it — an undisposed
        /// <see cref="NotifyIcon"/> leaves a dead icon in the tray that only vanishes when the user
        /// happens to mouse over it.
        /// </para>
        /// </summary>
        private void EnsureTrayIcon()
        {
            if (_trayIcon != null) return;

            if (components == null) components = new System.ComponentModel.Container();

            var menu = new ContextMenuStrip();
            menu.Items.Add("&Open " + ProductName, null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("E&xit", null, delegate { ExitApplication(); });

            _trayIcon = new NotifyIcon(components)
            {
                // GetTrayIcon reads the icon embedded in HelixStudio.UI, at the shell's small-icon
                // size, and returns null if that lookup fails; a NotifyIcon with no icon is
                // invisible, which would strand the window with no way back. The system fallback is
                // ugly and present, which beats pretty and absent.
                Icon = LunaForm.GetTrayIcon() ?? SystemIcons.Application,
                Text = ProductName,
                ContextMenuStrip = menu,
                Visible = false,
            };
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            // Single click too: the balloon says "click here", and a tray icon whose primary
            // gesture needs a double-click is the one users report as not working.
            _trayIcon.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
            };
            _trayIcon.BalloonTipClicked += delegate { RestoreFromTray(); };
        }

        /// <summary>
        /// Minimize is ORDINARY minimize: the window goes to the taskbar and stays there.
        ///
        /// <para>
        /// This override adds no behaviour — it exists to keep the invariant honest. Hiding a form
        /// from <c>OnResize</c> when it minimizes is the standard recipe for "minimize to tray", and
        /// bolting the tray onto the close gesture (above) is precisely when someone adds it. The
        /// clearing of <see cref="_hiddenToTray"/> is real, though: restoring from the taskbar goes
        /// through here without passing <see cref="RestoreFromTray"/>, and a stale flag would make
        /// the next [X] think it had already hidden.
        /// </para>
        /// </summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_hiddenToTray && Visible && WindowState != FormWindowState.Minimized)
            {
                _hiddenToTray = false;
                if (_trayIcon != null) _trayIcon.Visible = false;
            }
        }
    }
}
