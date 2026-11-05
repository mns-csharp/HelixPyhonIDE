using System;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.App.Services;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>Editable grid of command to shortcut mappings.</summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class KeyBindingsDialog : LunaForm
    {
        private readonly KeybindingManager _manager;

        public KeyBindingsDialog()
        {
            InitializeComponent();
        }

        public KeyBindingsDialog(KeybindingManager manager) : this()
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            SelectCurrentPreset();
            LoadRows();
        }

        // Reflect the active preset in the combo so the user can see which one they're on. Safe to
        // set the index directly — the preset is applied by the "Apply Preset" button, not on change.
        private void SelectCurrentPreset()
        {
            if (_presetCombo == null || _manager == null) return;
            switch (_manager.CurrentPreset)
            {
                case KeybindingPreset.MacStyle: _presetCombo.SelectedIndex = 1; break;
                case KeybindingPreset.LinuxStyle: _presetCombo.SelectedIndex = 2; break;
                default: _presetCombo.SelectedIndex = 0; break; // Windows / default
            }
        }

        // Rebuild the grid from the manager's current bindings. Each row's Tag holds the binding's
        // internal Name (stable key) so handlers can act on it regardless of display text.
        private void LoadRows()
        {
            _grid.Rows.Clear();
            if (_manager == null) return;
            string filter = _filterBox != null ? _filterBox.Text.Trim() : string.Empty;
            foreach (var binding in _manager.AllBindings)
            {
                // Filter by command name OR its current shortcut, so "ctrl+s" and "save" both work.
                if (filter.Length > 0)
                {
                    string shortcut = KeybindingManager.ChordToString(binding.Chord);
                    bool match =
                        (binding.DisplayName != null && binding.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (shortcut != null && shortcut.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) continue;
                }
                int index = _grid.Rows.Add(binding.DisplayName, KeybindingManager.ChordToString(binding.Chord));
                _grid.Rows[index].Tag = binding.Name;
            }
        }

        // Re-filter the grid as the user types in the search box.
        private void OnFilterChanged(object sender, EventArgs e)
        {
            LoadRows();
        }

        // Double-click a row to rebind: capture a new chord and, if confirmed, store it and refresh.
        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (_manager == null || e.RowIndex < 0) return;
            string name = _grid.Rows[e.RowIndex].Tag as string;
            if (string.IsNullOrEmpty(name)) return;
            using (var dialog = new ChordCaptureDialog())
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Captured != Keys.None)
                {
                    // Warn before a rebind silently steals another command's shortcut; if the user
                    // proceeds, clear the loser so exactly one command owns the chord.
                    KeybindingManager.Binding conflict = _manager.FindConflict(name, dialog.Captured);
                    if (conflict != null)
                    {
                        DialogResult r = MessageBox.Show(this,
                            string.Format(
                                "{0} is already assigned to \"{1}\".\n\nReassign it? \"{1}\" will lose its shortcut.",
                                KeybindingManager.ChordToString(dialog.Captured), conflict.DisplayName),
                            "Shortcut conflict", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (r != DialogResult.Yes) return;
                        _manager.SetChord(conflict.Name, Keys.None);
                    }
                    _manager.SetChord(name, dialog.Captured);
                    LoadRows();
                }
            }
        }

        // Right-click context menu: restore the binding's default chord or clear it entirely.
        private void OnGridMouseUp(object sender, MouseEventArgs e)
        {
            if (_manager == null || e.Button != MouseButtons.Right) return;
            // Resolve which row was clicked and select it so the menu acts on the visible target.
            DataGridView.HitTestInfo hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0) return;
            _grid.ClearSelection();
            _grid.Rows[hit.RowIndex].Selected = true;
            string name = _grid.Rows[hit.RowIndex].Tag as string;
            if (string.IsNullOrEmpty(name)) return;
            using (var menu = new ContextMenuStrip())
            {
                menu.Items.Add("Restore default", null, delegate
                {
                    foreach (var binding in _manager.AllBindings)
                        if (binding.Name == name) { _manager.SetChord(name, binding.DefaultChord); break; }
                    LoadRows();
                });
                menu.Items.Add("Clear (no shortcut)", null, delegate { _manager.SetChord(name, Keys.None); LoadRows(); });
                menu.Show(_grid, e.Location);
            }
        }

        // Apply a whole preset (Windows/Mac/Linux) chosen in the combo, then reload the grid.
        private void OnApplyPresetClick(object sender, EventArgs e)
        {
            if (_manager == null) return;
            KeybindingPreset preset = KeybindingPreset.Windows;
            if (_presetCombo.SelectedIndex == 1) preset = KeybindingPreset.MacStyle;
            else if (_presetCombo.SelectedIndex == 2) preset = KeybindingPreset.LinuxStyle;
            _manager.ApplyPreset(preset);
            LoadRows();
        }
    }
}
