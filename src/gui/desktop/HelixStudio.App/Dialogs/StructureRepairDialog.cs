using System;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.Structure;
using HelixPythonNamespace.AddIns.Kit;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// The consent prompt for solution policy rule 4 (PY8004): the solution's directory structure
    /// does not match the tree the IDE would draw for it, and the IDE is offering to restructure the
    /// user's directories.
    ///
    /// Two things this dialog must not be:
    ///
    /// <list type="bullet">
    /// <item><b>A dare.</b> A modal that moves an unknown number of a user's files on a two-word
    /// summary is not a confirmation. The plan is one click away behind <c>Details</c>, listed as
    /// <c>old path → new path</c>, and the header states up front how many folders will actually
    /// move — which for an orphan-only plan is zero, and the dialog says so.</item>
    /// <item><b>A proceed-anyway.</b> Cancel does not mean "draw it anyway". It leaves the solution
    /// structurally unverified, and the caller keeps Compile/Run/Debug blocked exactly as it does
    /// for PY8001-3. The button says so.</item>
    /// </list>
    ///
    /// The prompt text itself is <see cref="DirectoryStructureCorrectionEngine.WarningMessage"/> —
    /// held in the engine rather than here so it is identical on every route that can raise it and
    /// can be asserted without constructing a form.
    /// </summary>
    public sealed class StructureRepairDialog : LunaForm
    {
        private readonly ListView _plan;
        private readonly Button _details;
        private readonly int _collapsedHeight;

        /// <summary>The plan the user was shown. Never mutated by the dialog.</summary>
        public StructureRepairPlan Plan { get; }

        public StructureRepairDialog(StructureParityReport report, StructureRepairPlan plan)
        {
            Plan = plan;

            Text = "Directory Structure";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 190);
            MinimumSize = new Size(520, 220);
            Padding = new Padding(14, 12, 14, 12);

            var prompt = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 44,
                Font = LunaFonts.Ui,
                Text = DirectoryStructureCorrectionEngine.WarningMessage,
            };

            var summary = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 62,
                ForeColor = SystemColors.GrayText,
                Font = LunaFonts.Sized(-0.5f),
                Text = BuildSummary(report, plan),
            };

            _plan = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = true,
                Visible = false,
                Font = LunaFonts.Ui,
            };
            _plan.Columns.Add("Action", 150);
            _plan.Columns.Add("What happens", 430);
            LunaColors.ApplyTo(_plan);
            FillPlan(plan);
            _plan.Resize += delegate
            {
                int rest = _plan.ClientSize.Width - _plan.Columns[0].Width - 4;
                if (rest > 160) _plan.Columns[1].Width = rest;
            };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40 };

            _details = new Button
            {
                Text = "Details ▼",
                Width = 96, Height = 28,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Font = LunaFonts.Ui,
            };
            _details.Click += delegate { ToggleDetails(); };

            var ok = new Button
            {
                Text = "OK",
                Width = 88, Height = 28,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Font = LunaFonts.Ui,
            };
            var cancel = new Button
            {
                Text = "Cancel",
                Width = 88, Height = 28,
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Font = LunaFonts.Ui,
            };

            buttons.Controls.Add(_details);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            buttons.Layout += delegate
            {
                _details.Left = 0;
                cancel.Left = buttons.ClientSize.Width - cancel.Width;
                ok.Left = cancel.Left - ok.Width - 8;
                _details.Top = cancel.Top = ok.Top = 6;
            };

            Controls.Add(_plan);
            Controls.Add(buttons);
            Controls.Add(summary);
            Controls.Add(prompt);

            AcceptButton = ok;
            CancelButton = cancel;

            // Cancel must be the safe default. Enter on a dialog the user has not read should not
            // start moving their directories, so focus lands on Cancel even though OK is the
            // AcceptButton for an explicit Enter after a deliberate selection.
            ActiveControl = cancel;

            _collapsedHeight = ClientSize.Height;
        }

        /// <summary>
        /// The header the user reads before deciding. It leads with the count of directories that
        /// will MOVE, because that is the irreversible-feeling part; a plan that only starts showing
        /// paths already on disk says so explicitly, so the prompt is not alarming when nothing is
        /// at stake.
        /// </summary>
        internal static string BuildSummary(StructureParityReport report, StructureRepairPlan plan)
        {
            int defects = report == null ? 0 : report.Defects.Count;
            int moves = 0;
            if (plan != null)
                foreach (StructureRepairOperation op in plan.Operations)
                    if (op.TouchesDisk && op.Kind != StructureRepairKind.RewriteManifest) moves++;

            string head = defects + (defects == 1 ? " difference" : " differences")
                + " between the Solution Explorer and the folders on disk.";
            string body = moves == 0
                ? " Nothing will be moved or renamed — the IDE will simply start showing the files "
                  + "that are already there."
                : " " + moves + (moves == 1 ? " folder" : " folders") + " will be moved or renamed, and "
                  + "the manifests that reference them updated. Press Details to see exactly what.";

            string tail = string.Empty;
            if (plan != null && !plan.CanFullyRepair)
                tail = Environment.NewLine + "Some differences need a human decision and will be left alone.";
            return head + body + tail;
        }

        private void FillPlan(StructureRepairPlan plan)
        {
            if (plan == null) return;
            foreach (StructureRepairOperation op in plan.Operations)
            {
                var row = new ListViewItem(op.Kind.ToString());
                row.SubItems.Add(op.TargetPath == null
                    ? op.Description
                    : op.SourcePath + "  →  " + op.TargetPath);
                if (!op.TouchesDisk) row.ForeColor = SystemColors.GrayText;
                _plan.Items.Add(row);
            }
            foreach (string refusal in plan.Refusals)
            {
                var row = new ListViewItem("Not repaired");
                row.SubItems.Add(refusal);
                _plan.Items.Add(row);
            }
            foreach (string followUp in plan.ManualFollowUps)
            {
                var row = new ListViewItem("Check by hand");
                row.SubItems.Add(followUp);
                row.ForeColor = SystemColors.GrayText;
                _plan.Items.Add(row);
            }
        }

        private void ToggleDetails()
        {
            _plan.Visible = !_plan.Visible;
            _details.Text = _plan.Visible ? "Details ▲" : "Details ▼";
            ClientSize = new Size(ClientSize.Width, _plan.Visible ? _collapsedHeight + 240 : _collapsedHeight);
        }
    }
}
