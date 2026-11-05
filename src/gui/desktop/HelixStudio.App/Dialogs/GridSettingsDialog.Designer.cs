namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    public sealed partial class GridSettingsDialog
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private Button _cancelButton;
        private Button _okButton;
        private Label _infoLabel;
        private Label _valueLabel;
        private NumericUpDown _spacingInput;

        private void InitializeComponent()
        {
        Text = "Grid Settings";
        Size = new Size(420, 210);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        
        _infoLabel = new Label();
            this._infoLabel.Name = "infoLabel";
        _infoLabel.Text = "Grid pixel spacing (snap interval). Smaller = finer snap.";
        _infoLabel.Location = new Point(12, 12);
        _infoLabel.Size = new Size(380, 20);
        _infoLabel.Font = LunaFonts.Ui;
        
        _spacingInput = new NumericUpDown();
            this._spacingInput.Name = "spacingInput";
        _spacingInput.Minimum = 2;
        _spacingInput.Maximum = 100;
        _spacingInput.Increment = 2;
        _spacingInput.Location = new Point(12, 44);
        _spacingInput.Size = new Size(80, 24);
        _spacingInput.Font = LunaFonts.Ui;
        _spacingInput.ValueChanged += OnSpacingInputChanged;
        
        _spacingTrack = new TrackBar();
        _spacingTrack.Name = "spacingTrackBar";
        _spacingTrack.Minimum = 2;
        _spacingTrack.Maximum = 100;
        _spacingTrack.SmallChange = 2;
        _spacingTrack.LargeChange = 10;
        _spacingTrack.TickFrequency = 10;
        _spacingTrack.Location = new Point(100, 40);
        _spacingTrack.Size = new Size(290, 34);
        _spacingTrack.ValueChanged += OnSpacingTrackChanged;
        
        _valueLabel = new Label();
            this._valueLabel.Name = "valueLabel";
        _valueLabel.Location = new Point(12, 80);
        _valueLabel.Size = new Size(380, 20);
        _valueLabel.Font = LunaFonts.Ui;
        
        _okButton = new Button();
            this._okButton.Name = "okButton";
        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Size = new Size(80, 28);
        _okButton.Location = new Point(220, 130);
        _okButton.Click += OnOk;
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Size = new Size(80, 28);
        _cancelButton.Location = new Point(310, 130);
        
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        
        Controls.Add(_infoLabel);
        Controls.Add(_spacingInput);
        Controls.Add(_spacingTrack);
        Controls.Add(_valueLabel);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);
        }
    }
}
