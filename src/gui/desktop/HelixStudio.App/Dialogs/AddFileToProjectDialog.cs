using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.Python;
using HelixPythonNamespace.AddIns.Kit;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// The resolution offered for rule PY8001 ("this file is not part of any project in the
    /// solution"): pick the project folder the file belongs in, and the IDE moves it there.
    ///
    /// Membership is by containment — a file is part of the project whose directory holds it — so
    /// choosing a destination IS the fix; there is no per-file membership list to add it to. The
    /// dialog says the file will be MOVED, and warns about the import consequence, because both are
    /// things the user has to know before pressing OK rather than discover afterwards.
    /// </summary>
    public sealed class AddFileToProjectDialog : LunaForm
    {
        private readonly ListView _list;
        private readonly Button _ok;
        private readonly List<MembershipTarget> _targets;

        /// <summary>The folder the user chose, or null when they cancelled.</summary>
        public MembershipTarget SelectedTarget { get; private set; }

        public AddFileToProjectDialog(string fileName, IList<MembershipTarget> targets)
        {
            _targets = new List<MembershipTarget>(targets ?? new List<MembershipTarget>());

            Text = "Add to Project — " + (fileName ?? "file");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(460, 360);
            Padding = new Padding(14, 12, 14, 12);

            // Docked, not positioned: the form scales with the shell font, and a fixed-pixel layout
            // pushed the hint and the buttons off the bottom of the client area when it did.
            var prompt = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 40,
                Font = LunaFonts.Ui,
                Text = "'" + (fileName ?? "This file") + "' is not part of any project, so the IDE "
                     + "cannot compile-check, browse or debug it. Choose the folder it belongs in:",
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = true,
                Font = LunaFonts.Ui,
            };
            _list.Columns.Add("Project", 150);
            _list.Columns.Add("Folder", 250);
            _list.Columns.Add("Kind", 110);
            LunaColors.ApplyTo(_list);
            foreach (MembershipTarget t in _targets)
            {
                var row = new ListViewItem(t.ProjectName ?? string.Empty);
                row.SubItems.Add(t.RelativePath ?? t.DirectoryPath);
                row.SubItems.Add(t.IsPackage
                    ? "package"
                    : (t.IsProjectRoot ? "project folder" : "folder"));
                row.ToolTipText = t.DirectoryPath;
                row.Tag = t;
                _list.Items.Add(row);
            }
            _list.ShowItemToolTips = true;
            // Folder paths are the long column and the one the user actually reads; give it whatever
            // width is left rather than truncating it while empty space sits to its right.
            _list.Resize += delegate
            {
                int rest = _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[2].Width - 4;
                if (rest > 120) _list.Columns[1].Width = rest;
            };
            _list.SelectedIndexChanged += delegate { UpdateOkState(); };
            _list.DoubleClick += delegate { if (_list.SelectedItems.Count > 0) Accept(); };

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = 72,
                Padding = new Padding(0, 8, 0, 4),
                ForeColor = SystemColors.GrayText,
                Font = LunaFonts.Sized(-0.5f),
                Text = "The file is MOVED into the folder you pick — that is what makes it part of the "
                     + "project. Anything that imported it by its old location will need updating."
                     + Environment.NewLine
                     + "To keep it where it is instead, make its own folder a project: "
                     + "Solution Explorer ▸ right-click the solution ▸ Add Existing Project.",
            };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            _ok = new Button
            {
                Text = "Move Here",
                Width = 96, Height = 28,
                Enabled = false,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Font = LunaFonts.Ui,
            };
            _ok.Click += delegate { Accept(); };

            var cancel = new Button
            {
                Text = "Cancel",
                Width = 88, Height = 28,
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Font = LunaFonts.Ui,
            };
            buttons.Controls.Add(_ok);
            buttons.Controls.Add(cancel);
            buttons.Layout += delegate
            {
                cancel.Left = buttons.ClientSize.Width - cancel.Width;
                _ok.Left = cancel.Left - _ok.Width - 8;
                cancel.Top = _ok.Top = 6;
            };

            // Fill last: docking order is reverse z-order, so the list must be added after the
            // bottom-docked rows have claimed their space.
            Controls.Add(_list);
            Controls.Add(hint);
            Controls.Add(buttons);   // added last among the bottom row, so it sits on the edge
            Controls.Add(prompt);

            AcceptButton = _ok;
            CancelButton = cancel;

            // Preselect the first row (packages sort first), so Enter alone is a complete answer.
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            UpdateOkState();
        }

        private void UpdateOkState()
        {
            _ok.Enabled = _list.SelectedItems.Count > 0;
        }

        private void Accept()
        {
            if (_list.SelectedItems.Count == 0) return;
            SelectedTarget = _list.SelectedItems[0].Tag as MembershipTarget;
            if (SelectedTarget == null) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
