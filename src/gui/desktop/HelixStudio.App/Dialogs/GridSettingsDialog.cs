namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    [System.ComponentModel.DesignerCategory("Form")]

    // Small settings dialog for the canvas snap-grid spacing (px), edited via a linked spinner + slider.
    public sealed partial class GridSettingsDialog : LunaForm
    {
        public GridSettingsDialog()
        {
            InitializeComponent();
        }



        private TrackBar _spacingTrack;


        // The chosen spacing; the caller reads this after OK.
        public float SelectedGridSpacing { get; private set; }

        public GridSettingsDialog(float currentSpacing) : this()
        {
            // Seed both controls from the current spacing, clamped to the supported 2..100 px range.
            SelectedGridSpacing = currentSpacing;
            int v = Clamp((int)Math.Round(currentSpacing), 2, 100);
            _spacingInput.Value = v;
            _spacingTrack.Value = v;
            UpdateValueLabel(v);
        }


        // Spinner and slider are kept in sync: each change mirrors the other (guarded to avoid loops).
        private void OnSpacingInputChanged(object sender, EventArgs e)
        {
            int v = (int)_spacingInput.Value;
            if (_spacingTrack.Value != v) _spacingTrack.Value = v;
            UpdateValueLabel(v);
        }

        private void OnSpacingTrackChanged(object sender, EventArgs e)
        {
            int v = _spacingTrack.Value;
            if ((int)_spacingInput.Value != v) _spacingInput.Value = v;
            UpdateValueLabel(v);
        }

        private void UpdateValueLabel(int v)
        {
            _valueLabel.Text = string.Format("Snap every {0} px  (current default: 10 px)", v);
        }

        // Commit the spinner's value as the result on OK.
        private void OnOk(object sender, EventArgs e)
        {
            SelectedGridSpacing = (float)_spacingInput.Value;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
