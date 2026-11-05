namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Workspace.Toml;

    /// <summary>
    /// Edits the scalar keys of one TOML table — the edit surface behind double-clicking a box on
    /// the TOML config canvas. Only scalar values are offered: sub-tables and arrays-of-tables are
    /// their own boxes on the canvas, so they are edited by double-clicking those instead.
    ///
    /// Values are written back through the raw text the parser preserved, so quoting style and
    /// numeric formatting survive a round-trip unchanged unless the user actually edits them.
    /// </summary>
    public sealed class TomlTableEditDialog : LunaForm
    {
        private readonly TomlTable _table;
        private readonly List<KeyValuePair<string, TextBox>> _editors = new List<KeyValuePair<string, TextBox>>();

        /// <summary>True when at least one value was actually changed.</summary>
        public bool Changed { get; private set; }

        public TomlTableEditDialog(string tableName, TomlTable table)
        {
            _table = table;
            Text = "Edit  [" + (string.IsNullOrEmpty(tableName) ? "root" : tableName) + "]";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 90);

            var scalars = new List<string>();
            if (table != null)
                foreach (string key in table.KeyOrder)
                    if (table.Get(key) is TomlValue) scalars.Add(key);

            int y = 14;
            foreach (string key in scalars)
            {
                var value = (TomlValue)table.Get(key);

                Controls.Add(new Label
                {
                    Text = key,
                    Left = 14,
                    Top = y + 3,
                    Width = 150,
                    AutoEllipsis = true,
                    Font = LunaFonts.Ui,
                });

                var box = new TextBox
                {
                    Text = value.Raw ?? string.Empty,
                    Left = 170,
                    Top = y,
                    Width = 270,
                    Font = LunaFonts.Ui,
                };
                Controls.Add(box);
                _editors.Add(new KeyValuePair<string, TextBox>(key, box));

                y += 28;
            }

            if (scalars.Count == 0)
            {
                Controls.Add(new Label
                {
                    Text = "This table has no scalar keys of its own." + Environment.NewLine +
                           "Its sub-tables are separate boxes on the canvas — double-click those.",
                    Left = 14,
                    Top = y,
                    Width = 430,
                    Height = 40,
                    ForeColor = Color.FromArgb(110, 110, 110),
                    Font = LunaFonts.Ui,
                });
                y += 46;
            }

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 84, Font = LunaFonts.Ui };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 84, Font = LunaFonts.Ui };
            ok.Left = 268; cancel.Left = 358;
            ok.Top = cancel.Top = y + 10;
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            ClientSize = new Size(460, y + 48);
            ok.Click += delegate { Apply(); };
        }

        private void Apply()
        {
            foreach (KeyValuePair<string, TextBox> pair in _editors)
            {
                var value = _table.Get(pair.Key) as TomlValue;
                if (value == null) continue;

                string edited = pair.Value.Text ?? string.Empty;
                if (edited == (value.Raw ?? string.Empty)) continue;

                value.Raw = edited;
                RetypeFromRaw(value, edited);
                Changed = true;
            }
        }

        /// <summary>
        /// Keeps the typed fields consistent with the edited raw text, so a value edited here reads
        /// back the same way the parser would have read it from the file.
        /// </summary>
        private static void RetypeFromRaw(TomlValue value, string raw)
        {
            string trimmed = (raw ?? string.Empty).Trim();

            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"') ||
                 (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')))
            {
                value.Kind = TomlValueKind.String;
                value.StringValue = trimmed.Substring(1, trimmed.Length - 2);
                return;
            }

            bool boolValue;
            if (bool.TryParse(trimmed, out boolValue))
            {
                value.Kind = TomlValueKind.Boolean;
                value.BoolValue = boolValue;
                return;
            }

            long intValue;
            if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out intValue))
            {
                value.Kind = TomlValueKind.Integer;
                value.IntValue = intValue;
                return;
            }

            double floatValue;
            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out floatValue))
            {
                value.Kind = TomlValueKind.Float;
                value.FloatValue = floatValue;
                return;
            }

            // Anything else (arrays, inline tables, dates) keeps its raw text; TomlWriter emits Raw.
            value.StringValue = trimmed;
        }
    }
}
