namespace HelixPythonNamespace.App
{
    using System;
    using System.Windows.Forms;

    /// <summary>
    /// Debug focus mode: while a debug session is live the side panels step aside so the source,
    /// the current-line marker and the Debug panel get the whole window — the shape Visual Studio
    /// takes when it starts debugging.
    ///
    /// This is deliberately TRANSIENT and not persisted. The user did not close the Solution
    /// Explorer; the IDE borrowed its space for the length of a run and gives it back. Persisting
    /// it would mean a single debug session silently rewrote the user's layout preference — the
    /// exact mistake the "persist the INTENT, not a control's transient visibility" rule warns
    /// about, seen from the other side.
    /// </summary>
    public partial class MainForm
    {
        private bool _debugFocusMode;
        private bool _debugFocusExplorerWasVisible = true;
        private bool _debugFocusTerminalWasVisible = true;
        private bool _debugFocusFloatingExplorerWasVisible;
        private int _debugFocusSavedSplitterDistance;

        /// <summary>True while the side panels are stood down for a debug session.</summary>
        internal bool IsDebugFocusMode { get { return _debugFocusMode; } }

        /// <summary>
        /// Enters or leaves debug focus mode. Idempotent, so the debug coordinator can call it from
        /// every session-state transition without tracking edges itself.
        ///
        /// What each side is doing matters:
        ///  * hiding <c>_leftDockPanel</c> alone is NOT enough — it lives in a SplitContainer panel,
        ///    and a hidden child still leaves the splitter's column reserved, so the window would
        ///    show a blank 200px gutter instead of giving the width to the editor. Collapsing
        ///    Panel1 is what actually frees the space (the same lesson notebook mode records).
        ///  * a FLOATING Solution Explorer is a separate window that a collapsed splitter cannot
        ///    touch, so it is hidden and restored explicitly.
        ///  * the exit path restores what was captured on entry, never a hardcoded "visible" — a
        ///    user who had already closed the explorer must not find it reopened by debugging.
        /// </summary>
        internal void SetDebugFocusMode(bool on)
        {
            if (on == _debugFocusMode) return;
            _debugFocusMode = on;

            try
            {
                if (on) EnterDebugFocusMode();
                else LeaveDebugFocusMode();
            }
            catch
            {
                // A layout failure must never take down a debug session — the debugger is the
                // point of the exercise and the chrome is not.
            }
        }

        private void EnterDebugFocusMode()
        {
            // Read the INTENT, never `Control.Visible`. `Visible` reports EFFECTIVE visibility —
            // false for every control while the form itself is not yet shown — so capturing it would
            // record "the user had the explorer closed" for a window that simply had not been
            // painted yet, and the restore would then leave it closed for good. The splitter's own
            // collapsed flag and the AI terminal's `_aiTerminalHidden` intent are the real state,
            // and neither depends on whether the shell has been realized. (Measured: reading
            // `Visible` here failed two restore tests on an unshown form.)
            _debugFocusExplorerWasVisible = _leftSplitter != null && !_leftSplitter.Panel1Collapsed;
            _debugFocusTerminalWasVisible = IsAiTerminalVisible;
            _debugFocusFloatingExplorerWasVisible =
                _floatingExplorerForm != null && !_floatingExplorerForm.IsDisposed && _floatingExplorerForm.Visible;

            if (_leftSplitter != null && !_leftSplitter.IsDisposed)
            {
                if (!_leftSplitter.Panel1Collapsed)
                    _debugFocusSavedSplitterDistance = _leftSplitter.SplitterDistance;
                _leftSplitter.Panel1Collapsed = true;
            }
            if (_leftDockPanel != null && !_leftDockPanel.IsDisposed) _leftDockPanel.Visible = false;
            if (_aiTerminalDock != null && !_aiTerminalDock.IsDisposed) _aiTerminalDock.Visible = false;
            if (_debugFocusFloatingExplorerWasVisible) _floatingExplorerForm.Hide();
        }

        private void LeaveDebugFocusMode()
        {
            if (_leftDockPanel != null && !_leftDockPanel.IsDisposed)
                _leftDockPanel.Visible = _debugFocusExplorerWasVisible;

            if (_leftSplitter != null && !_leftSplitter.IsDisposed)
            {
                _leftSplitter.Panel1Collapsed = !_debugFocusExplorerWasVisible;
                if (_debugFocusExplorerWasVisible && _debugFocusSavedSplitterDistance > 0)
                {
                    // SplitterDistance throws when the saved width no longer fits the (possibly
                    // resized) window; the panel is already back, so a failed width is cosmetic.
                    try { _leftSplitter.SplitterDistance = _debugFocusSavedSplitterDistance; }
                    catch (InvalidOperationException) { }
                    catch (ArgumentException) { }
                }
            }

            // The terminal's dock is restored to the user's intent, not to whatever it looked like
            // mid-session; `_aiTerminalHidden` is deliberately left untouched throughout, so the
            // View ▸ tick and the persisted setting never learn that a debug session happened.
            if (_aiTerminalDock != null && !_aiTerminalDock.IsDisposed)
                _aiTerminalDock.Visible = _debugFocusTerminalWasVisible;

            if (_debugFocusFloatingExplorerWasVisible &&
                _floatingExplorerForm != null && !_floatingExplorerForm.IsDisposed)
                _floatingExplorerForm.Show();
        }
    }
}
