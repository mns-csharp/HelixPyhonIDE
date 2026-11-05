namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Windows.Forms;
    using HelixPythonNamespace.App.Services;
    using HelixPythonNamespace.UI.Controls;

    // Modal capture box for recording a single keyboard chord (modifiers + key) for a keybinding.
    [System.ComponentModel.DesignerCategory("Form")]
    internal sealed partial class ChordCaptureDialog : LunaForm
    {
        internal ChordCaptureDialog() { InitializeComponent(); }
        // The captured chord (KeyData includes modifier flags); read by the caller after OK.
        internal Keys Captured { get; private set; }

        private void OnCaptureKeyDown(object sender, KeyEventArgs e)
        {
            // Escape cancels without recording anything.
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return; }
            // Ignore bare modifier presses; wait for an actual key so the chord isn't just Ctrl/Shift/etc.
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu ||
                e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin) return;
            // Record the full chord, show its human-readable form, and swallow the key so it isn't typed.
            Captured = e.KeyData;
            displayLabel.Text = KeybindingManager.ChordToString(Captured);
            e.SuppressKeyPress = true;
            // Defer closing until the key event fully unwinds, avoiding re-entrancy in the key pipeline.
            BeginInvoke((Action)CompleteCapture);
        }

        private void CompleteCapture() { DialogResult = DialogResult.OK; Close(); }
    }
}
