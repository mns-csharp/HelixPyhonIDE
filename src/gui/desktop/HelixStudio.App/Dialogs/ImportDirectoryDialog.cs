using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Wizard that collects inputs for converting a raw Python directory into
    /// one or both supported project layouts (TOML workspace / Visual Studio PTVS).
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class ImportDirectoryDialog : LunaForm
    {
        public ImportDirectoryDialog()
        {
            InitializeComponent();
        }













        // Preferred entry point: seeds the source path (if the caller already knows it),
        // derives a default solution name from that path, then refreshes the live preview.
        public ImportDirectoryDialog(string initialSourcePath = null) : this()
        {
            _sourceTextBox.Text = initialSourcePath ?? string.Empty;
            _nameTextBox.Text = DeriveNameFromPath(initialSourcePath);
            UpdatePreview();
        }

        // Public result surface: the caller reads these after ShowDialog() returns OK
        // to learn what the user chose. Text inputs are trimmed here so callers get clean values.
        public string SourceDirectory => _sourceTextBox.Text.Trim();
        public string SolutionName => _nameTextBox.Text.Trim();
        public bool OverwriteExisting => _overwriteCheckBox.Checked;

        // Maps the three mutually-exclusive radio buttons onto the ImportTargetFormat enum.
        // Defaults to TomlWorkspace when neither VS nor Both is checked.
        public ImportTargetFormat TargetFormat
        {
            get
            {
                if (_vsRadio != null && _vsRadio.Checked) return ImportTargetFormat.VisualStudio;
                if (_bothRadio != null && _bothRadio.Checked) return ImportTargetFormat.Both;
                return ImportTargetFormat.TomlWorkspace;
            }
        }

        // Legacy hand-rolled layout builder (distinct from the Designer's InitializeComponent).
        // Positions every control with absolute coordinates driven by the running currentY cursor,
        // wires input events to OnInputChanged so the preview stays live, and finally registers the
        // Import/Cancel buttons as the form's AcceptButton/CancelButton for Enter/Esc handling.
        private void InitializeComponents(string initialSourcePath)
        {
            Text = "Import Python Directory";
            Size = new Size(560, 410);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            int labelWidth = 110;
            int leftMargin = 16;
            int controlLeft = leftMargin + labelWidth + 8;
            int controlWidth = 390;
            int currentY = 20;

            _sourceLabel = new Label
            {
                Text = "Source directory:",
                Location = new Point(leftMargin, currentY + 3),
                Size = new Size(labelWidth, 20)
            };

            _sourceTextBox = new TextBox
            {
                Location = new Point(controlLeft, currentY),
                Size = new Size(controlWidth - 90, 22),
                Text = initialSourcePath ?? string.Empty
            };
            _sourceTextBox.TextChanged += OnInputChanged;

            _sourceBrowseButton = new Button
            {
                Text = "Browse...",
                Location = new Point(controlLeft + controlWidth - 80, currentY),
                Size = new Size(80, 24)
            };
            _sourceBrowseButton.Click += OnSourceBrowseClick;

            currentY += 32;

            _nameLabel = new Label
            {
                Text = "Solution name:",
                Location = new Point(leftMargin, currentY + 3),
                Size = new Size(labelWidth, 20)
            };

            _nameTextBox = new TextBox
            {
                Location = new Point(controlLeft, currentY),
                Size = new Size(controlWidth, 22),
                Text = DeriveNameFromPath(initialSourcePath)
            };
            _nameTextBox.TextChanged += OnInputChanged;

            currentY += 32;

            _formatGroup = new GroupBox
            {
                Text = "Target format",
                Location = new Point(leftMargin, currentY),
                Size = new Size(labelWidth + controlWidth + 8, 100)
            };
            _tomlRadio = new RadioButton
            {
                Text = "&TOML Workspace (uv-compatible) — opens in uv / PDM / HelixStudio",
                Location = new Point(12, 20),
                Size = new Size(500, 20),
                Checked = true
            };
            _vsRadio = new RadioButton
            {
                Text = "&Visual Studio (.sln + .pyproj) — opens in Visual Studio + PTVS",
                Location = new Point(12, 44),
                Size = new Size(500, 20)
            };
            _bothRadio = new RadioButton
            {
                Text = "&Both formats",
                Location = new Point(12, 68),
                Size = new Size(500, 20)
            };
            _tomlRadio.CheckedChanged += OnInputChanged;
            _vsRadio.CheckedChanged += OnInputChanged;
            _bothRadio.CheckedChanged += OnInputChanged;
            _formatGroup.Controls.AddRange(new Control[] { _tomlRadio, _vsRadio, _bothRadio });

            currentY += 106;

            _overwriteCheckBox = new CheckBox
            {
                Text = "Overwrite existing .sln / .pyproj files",
                Location = new Point(controlLeft, currentY),
                Size = new Size(controlWidth, 20),
                Checked = false
            };

            currentY += 32;

            _previewLabel = new Label
            {
                Text = "Will create:",
                Location = new Point(leftMargin, currentY + 3),
                Size = new Size(labelWidth, 20)
            };

            _previewTextBox = new TextBox
            {
                Location = new Point(controlLeft, currentY),
                Size = new Size(controlWidth, 44),
                ReadOnly = true,
                Multiline = true,
                BackColor = Color.FromArgb(245, 245, 245)
            };

            currentY += 60;

            _importButton = new Button
            {
                Text = "Import",
                Size = new Size(90, 28),
                Location = new Point(Width - 210, currentY),
                DialogResult = DialogResult.OK
            };
            _importButton.Click += OnImportClick;

            _cancelButton = new Button
            {
                Text = "Cancel",
                Size = new Size(90, 28),
                Location = new Point(Width - 110, currentY),
                DialogResult = DialogResult.Cancel
            };

            AcceptButton = _importButton;
            CancelButton = _cancelButton;

            Controls.AddRange(new Control[]
            {
                _sourceLabel, _sourceTextBox, _sourceBrowseButton,
                _nameLabel, _nameTextBox,
                _formatGroup,
                _overwriteCheckBox,
                _previewLabel, _previewTextBox,
                _importButton, _cancelButton
            });
        }

        // Suggests a solution name from the last path segment (e.g. C:\src\myapp -> "myapp").
        // Falls back to "imported" for empty input or a malformed path.
        private static string DeriveNameFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "imported";
            try { return new DirectoryInfo(path.TrimEnd('\\', '/')).Name; }
            catch { return "imported"; }
        }

        // Any input change (path, name, or format radio) re-runs the preview.
        private void OnInputChanged(object sender, EventArgs e) { UpdatePreview(); }

        // Recomputes the "Will create:" preview and gates the Import button.
        // Invalid input (missing/nonexistent dir or blank name) disables Import; otherwise it
        // shows the file paths that will be produced for the currently selected target format.
        private void UpdatePreview()
        {
            string dir = SourceDirectory;
            string name = SolutionName;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || string.IsNullOrWhiteSpace(name))
            {
                _previewTextBox.Text = "(select a valid source directory)";
                _importButton.Enabled = false;
                return;
            }
            string tomlPath = Path.Combine(dir, "pyproject.toml");
            string slnPath = Path.Combine(dir, name + ".sln");
            switch (TargetFormat)
            {
                case ImportTargetFormat.VisualStudio: _previewTextBox.Text = slnPath; break;
                case ImportTargetFormat.Both: _previewTextBox.Text = tomlPath + "\r\n" + slnPath; break;
                default: _previewTextBox.Text = tomlPath; break;
            }
            _importButton.Enabled = true;
        }

        // "Browse..." handler: opens a folder picker seeded with the current path (if valid),
        // stores the chosen directory, and only auto-fills the name if the user hasn't typed one.
        private void OnSourceBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select the Python directory to import";
                if (Directory.Exists(_sourceTextBox.Text)) dlg.SelectedPath = _sourceTextBox.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _sourceTextBox.Text = dlg.SelectedPath;
                    if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
                        _nameTextBox.Text = DeriveNameFromPath(dlg.SelectedPath);
                }
            }
        }

        // Final validation gate before the OK result is honored. The Import button carries
        // DialogResult.OK, so setting DialogResult = None here vetoes the close and keeps the
        // dialog open when validation fails (nonexistent dir, blank name, or illegal filename chars).
        private void OnImportClick(object sender, EventArgs e)
        {
            if (!Directory.Exists(SourceDirectory))
            {
                MessageBox.Show("Source directory does not exist.", "Validation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _sourceTextBox.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(SolutionName))
            {
                MessageBox.Show("Please enter a solution name.", "Validation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _nameTextBox.Focus();
                return;
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (SolutionName.IndexOf(c) >= 0)
                {
                    MessageBox.Show("Solution name contains invalid characters.", "Validation Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    _nameTextBox.Focus();
                    return;
                }
            }
        }
    }
}
