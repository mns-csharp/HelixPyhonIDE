using System;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Prompts for a package's Run/Debug command line — the equivalent of Visual Studio's
    /// "Command line arguments" launch setting. The value is stored verbatim in the package's
    /// <c>pyproject.toml</c> under <c>[tool.helixstudio].run-args</c>, so it is committed and
    /// shared with the team rather than kept in per-developer IDE state.
    /// </summary>
    public sealed class RunArgumentsDialog : LunaForm
    {
        private readonly TextBox _input;

        /// <param name="workingDirectory">
        /// The folder the program will run in, shown so the user can tell what a RELATIVE argument
        /// will resolve to. This replaced a worked example of the form
        /// <c>input.tsv "C:\My Data\out.tsv"</c>: a concrete-looking sample in a hint sitting right
        /// under an empty text box reads as a template to copy, and was in fact copied verbatim
        /// into a real project, where it failed with FileNotFoundError on a file nobody had.
        /// </param>
        public RunArgumentsDialog(string projectName, string entryFileName, string currentValue,
            string workingDirectory = null)
        {
            Text = "Run Arguments — " + (projectName ?? "Project");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(560, 190);

            var prompt = new Label
            {
                AutoSize = false,
                Left = 14, Top = 14, Width = 532, Height = 34,
                Text = "Arguments passed to " + (entryFileName ?? "the entry point")
                     + " when you Run or Debug this package.",
            };
            Controls.Add(prompt);

            _input = new TextBox
            {
                Left = 14, Top = 54, Width = 532,
                Text = currentValue ?? string.Empty,
                Font = new Font("Consolas", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(_input);

            var hint = new Label
            {
                AutoSize = false,
                Left = 14, Top = 82, Width = 532, Height = 44,
                ForeColor = SystemColors.GrayText,
                Text = string.IsNullOrEmpty(workingDirectory)
                    ? "Put double quotes around any path containing spaces. Saved to pyproject.toml."
                    : "Relative paths resolve against the working directory:\n"
                      + workingDirectory
                      + "\nPut double quotes around any path containing spaces. Saved to pyproject.toml.",
            };
            Controls.Add(hint);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 374, Top = 144, Width = 84, Height = 28 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 462, Top = 144, Width = 84, Height = 28 };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Shown += delegate { _input.Focus(); _input.SelectAll(); };
        }

        /// <summary>The entered command line, trimmed. Empty string means "no arguments".</summary>
        public string RunArguments
        {
            get { return (_input.Text ?? string.Empty).Trim(); }
        }
    }
}
