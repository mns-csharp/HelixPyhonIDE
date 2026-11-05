namespace HelixPythonNamespace.UI.Controls
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    partial class ToolsPaletteForm
    {
        private IContainer components = null;
        private ToolTip toolTip;
        private Button _selectButton; private Button _panButton; private Label _separator;
        private Button _interfaceButton; private Button _abstractButton; private Button _concreteButton;

        private void InitializeComponent()
        {
            this.components = new Container(); this.toolTip = new ToolTip(this.components); this._selectButton = new Button();
            this._panButton = new Button(); this._separator = new Label(); this._interfaceButton = new Button();
            this._abstractButton = new Button(); this._concreteButton = new Button(); this.SuspendLayout();
            this._selectButton.BackColor = SystemColors.Control; this._selectButton.FlatStyle = FlatStyle.Flat;
            this._selectButton.ImageAlign = ContentAlignment.MiddleCenter; this._selectButton.Location = new Point(6, 6);
            this._selectButton.Name = "selectButton"; this._selectButton.Size = new Size(36, 36); this._selectButton.Text = "";
            this.toolTip.SetToolTip(this._selectButton, "Select tool (S) — drag on empty space to rubber-band select");
            this._selectButton.Click += new System.EventHandler(this.OnSelectToolClick);
            this._panButton.BackColor = SystemColors.Control; this._panButton.FlatStyle = FlatStyle.Flat;
            this._panButton.ImageAlign = ContentAlignment.MiddleCenter; this._panButton.Location = new Point(46, 6);
            this._panButton.Name = "panButton"; this._panButton.Size = new Size(36, 36); this._panButton.Text = "";
            this.toolTip.SetToolTip(this._panButton, "Pan tool (H) — drag on empty space to pan the canvas");
            this._panButton.Click += new System.EventHandler(this.OnPanToolClick);
            this._separator.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; this._separator.BorderStyle = BorderStyle.Fixed3D;
            this._separator.Location = new Point(6, 48); this._separator.Name = "separator"; this._separator.Size = new Size(76, 2);
            this._interfaceButton.BackColor = SystemColors.Control; this._interfaceButton.FlatStyle = FlatStyle.Flat;
            this._interfaceButton.Location = new Point(6, 54); this._interfaceButton.Name = "interfaceButton";
            this._interfaceButton.Size = new Size(36, 36); this._interfaceButton.Text = "";
            this.toolTip.SetToolTip(this._interfaceButton, "Add Interface (I)"); this._interfaceButton.Click += new System.EventHandler(this.OnAddInterfaceClick);
            this._abstractButton.BackColor = SystemColors.Control; this._abstractButton.FlatStyle = FlatStyle.Flat;
            this._abstractButton.Location = new Point(46, 54); this._abstractButton.Name = "abstractButton";
            this._abstractButton.Size = new Size(36, 36); this._abstractButton.Text = "";
            this.toolTip.SetToolTip(this._abstractButton, "Add Abstract Class (A)"); this._abstractButton.Click += new System.EventHandler(this.OnAddAbstractClick);
            this._concreteButton.BackColor = SystemColors.Control; this._concreteButton.FlatStyle = FlatStyle.Flat;
            this._concreteButton.Location = new Point(6, 94); this._concreteButton.Name = "concreteButton";
            this._concreteButton.Size = new Size(36, 36); this._concreteButton.Text = "";
            this.toolTip.SetToolTip(this._concreteButton, "Add Concrete Class (C)"); this._concreteButton.Click += new System.EventHandler(this.OnAddConcreteClick);
            this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.ClientSize = new Size(88, 136);
            this.Controls.Add(this._concreteButton); this.Controls.Add(this._abstractButton); this.Controls.Add(this._interfaceButton);
            this.Controls.Add(this._separator); this.Controls.Add(this._panButton); this.Controls.Add(this._selectButton);
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow; this.KeyPreview = true; this.MaximizeBox = false;
            this.MinimizeBox = false; this.MinimumSize = new Size(104, 176); this.Name = "ToolsPaletteForm";
            this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.Manual; this.Text = "Tools"; this.TopMost = true;
            this.KeyDown += new KeyEventHandler(this.OnKeyDownShortcut); this.ResumeLayout(false);
        }
    }
}
