using System;
using System.Drawing;
using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Small generic "type a value" modal. Used for rename operations and similar
    /// one-field prompts where a full editor dialog would be overkill.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class SimpleInputDialog : LunaForm
    {
        public SimpleInputDialog()
        {
            InitializeComponent();
        }


        // The text the user typed, trimmed — leading/trailing spaces never belong in a name or message.
        public string Value { get { return (_textBox.Text ?? string.Empty).Trim(); } }

        private bool _allowEmpty;

        /// <summary>
        /// When false (the default), OK is disabled while the input is empty/whitespace, so the user
        /// can't accept a blank name. Set true for prompts where blank is meaningful (e.g. a tracepoint
        /// message where blank clears it).
        /// </summary>
        public bool AllowEmpty
        {
            get { return _allowEmpty; }
            set { _allowEmpty = value; UpdateOkEnabled(); }
        }

        private void UpdateOkEnabled()
        {
            if (_okButton != null)
                _okButton.Enabled = _allowEmpty || !string.IsNullOrWhiteSpace(_textBox.Text);
        }

        // Build the whole dialog in code: prompt label, seeded (pre-selected) text box, and OK/Cancel
        // buttons wired to their DialogResults, with OK as Accept and Cancel as the CancelButton.
        public SimpleInputDialog(string title, string prompt, string initialValue)
        {
            Text = title ?? string.Empty;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 120);

            Label promptLabel = new Label
            {
                Text = prompt ?? string.Empty,
                Location = new Point(12, 12),
                Size = new Size(396, 20),
                AutoEllipsis = true,
            };
            Controls.Add(promptLabel);

            _textBox = new TextBox
            {
                Text = initialValue ?? string.Empty,
                Location = new Point(12, 38),
                Size = new Size(396, 22),
            };
            _textBox.SelectAll();
            _textBox.TextChanged += delegate { UpdateOkEnabled(); };
            Controls.Add(_textBox);

            _okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(244, 78),
                Size = new Size(80, 28),
            };
            Controls.Add(_okButton);

            Button cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(330, 78),
                Size = new Size(80, 28),
            };
            Controls.Add(cancelButton);

            AcceptButton = _okButton;
            CancelButton = cancelButton;

            UpdateOkEnabled();   // seed OK's enabled state from the initial value
        }
    }
}
