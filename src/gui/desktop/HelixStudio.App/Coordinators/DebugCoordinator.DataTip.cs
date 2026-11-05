namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Forms;
    using HelixPythonNamespace.App.Controls;
    using HelixPythonNamespace.Debugger;
    using HelixPythonNamespace.Editor;

    /// <summary>
    /// Debug data tips — Visual Studio's "hover a variable while paused and see its value",
    /// expandable into members. The editor half only reports what was hovered
    /// (<see cref="PythonEditorControl.DebugDataTipRequested"/>); everything that needs the debugger
    /// lives here, because <c>HelixStudio.Editor</c> does not reference <c>HelixStudio.Debugger</c>.
    /// </summary>
    public sealed partial class DebugCoordinator
    {
        private DebugDataTipWindow _dataTip;

        // A dwell ends the moment the mouse moves — including the move TOWARDS the tip the user is
        // reaching for. Dismissing on that event directly makes an expandable tip impossible to
        // touch, so the dismissal is deferred by a beat and cancelled while the pointer is over the
        // tip. This is the same grace period Visual Studio's data tip uses.
        private Timer _dataTipDismissTimer;
        private const int DataTipDismissGraceMs = 260;

        /// <summary>
        /// Answers a hover from the editor with the expression's live value, when there is a paused
        /// session to ask. Leaves <c>Handled</c> false otherwise, so the ordinary signature/doc hover
        /// still happens when the IDE is not debugging — the editor must not lose its normal hover
        /// just because this feature exists.
        /// </summary>
        internal void OnEditorDataTipRequested(object sender, PythonEditorControl.DebugDataTipEventArgs e)
        {
            if (e == null || host == null || _debugController == null) return;
            if (!_debugController.IsPaused) return;
            if (string.IsNullOrEmpty(e.Expression)) return;

            var editor = sender as PythonEditorControl;

            // Re-hovering the same expression must not rebuild the tip: that would collapse every
            // node the user had just opened.
            if (_dataTip != null && !_dataTip.IsDisposed && _dataTip.Visible &&
                string.Equals(_dataTip.Expression, e.Expression, StringComparison.Ordinal))
            {
                CancelDataTipDismiss();
                e.Handled = true;
                return;
            }

            DebugVariable value;
            try { value = _debugController.Evaluate(e.Expression, _selectedFrameId, "hover"); }
            catch { return; }
            // No value means the name is not in scope in this frame (a hover over a keyword, a
            // parameter of some other function, text in a different file). Say nothing and let the
            // ordinary hover answer instead — an empty tooltip reads as a broken feature.
            if (value == null) return;

            EnsureDataTip();
            // Drop the tip one text line below the hovered word so it never covers what it explains.
            int lineHeight = 16;
            try { if (editor != null && editor.Font != null) lineHeight = editor.Font.Height; }
            catch { }

            try
            {
                CancelDataTipDismiss();
                _dataTip.ShowFor(e.Expression, value, e.ScreenPoint, lineHeight,
                                 _debugController.GetChildren, host);
                e.Handled = true;
            }
            catch { /* a tip that cannot be shown must not break hovering */ }
        }

        /// <summary>
        /// The dwell ended. Start the grace period rather than hiding now — see
        /// <see cref="DataTipDismissGraceMs"/>.
        /// </summary>
        internal void OnEditorDataTipDismissRequested(object sender, EventArgs e)
        {
            if (_dataTip == null || _dataTip.IsDisposed || !_dataTip.Visible) return;
            EnsureDismissTimer();
            _dataTipDismissTimer.Stop();
            _dataTipDismissTimer.Start();
        }

        /// <summary>
        /// Takes the data tip down immediately. Called when the value it shows stops being true:
        /// the program resumed, stepped, or the session ended.
        /// </summary>
        internal void HideDataTip()
        {
            CancelDataTipDismiss();
            if (_dataTip != null && !_dataTip.IsDisposed) _dataTip.HideTip();
        }

        /// <summary>Wires a newly opened editor tab to the data tip. Called once per tab.</summary>
        internal void AttachDataTips(PythonEditorControl editor)
        {
            if (editor == null) return;
            editor.DebugDataTipRequested += OnEditorDataTipRequested;
            editor.DebugDataTipDismissRequested += OnEditorDataTipDismissRequested;
        }

        private void EnsureDataTip()
        {
            if (_dataTip == null || _dataTip.IsDisposed) _dataTip = new DebugDataTipWindow();
        }

        private void EnsureDismissTimer()
        {
            if (_dataTipDismissTimer != null) return;
            _dataTipDismissTimer = new Timer { Interval = DataTipDismissGraceMs };
            _dataTipDismissTimer.Tick += (s, e) =>
            {
                // Still being used: the user is reading it, expanding a node, or dragging its resize
                // corner. Keep waiting — closing a tip mid-drag makes it impossible to enlarge.
                if (_dataTip != null && !_dataTip.IsDisposed && _dataTip.IsInteracting) return;
                _dataTipDismissTimer.Stop();
                if (_dataTip != null && !_dataTip.IsDisposed) _dataTip.HideTip();
            };
        }

        private void CancelDataTipDismiss()
        {
            if (_dataTipDismissTimer != null) _dataTipDismissTimer.Stop();
        }

        private void DisposeDataTip()
        {
            try { if (_dataTipDismissTimer != null) { _dataTipDismissTimer.Stop(); _dataTipDismissTimer.Dispose(); } }
            catch { }
            _dataTipDismissTimer = null;
            try { if (_dataTip != null && !_dataTip.IsDisposed) _dataTip.Dispose(); }
            catch { }
            _dataTip = null;
        }
    }
}
