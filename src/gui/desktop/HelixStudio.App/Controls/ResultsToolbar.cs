namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// The Clear / Copy / Save-as-text button strip every results panel carries, styled exactly
    /// like the Terminal panel's toolbar (28x24 flat icon buttons, tooltips instead of captions, on
    /// the normal Luna toolbar background — chrome, not content surface).
    ///
    /// Panels differ in what they hold — one grid, three grids, a text buffer, or a grid plus a
    /// text buffer behind sub-tabs — so each supplies its own <c>getText</c> and <c>clear</c>
    /// instead of this control reaching into their internals.
    /// </summary>
    public sealed class ResultsToolbar : Panel
    {
        private readonly ToolTip _tips = new ToolTip();
        private readonly Func<string> _getText;
        private readonly string _subject;

        public Button ClearButton { get; private set; }
        public Button CopyButton { get; private set; }
        public Button SaveButton { get; private set; }

        /// <summary>
        /// Add the strip to <paramref name="host"/>. Call this AFTER the panel's own content
        /// controls are added: WinForms lays docked children out from the end of the collection
        /// backwards, so the last-added Top control claims the topmost band.
        /// </summary>
        /// <param name="subject">Panel name — used in tooltips and the default save filename.</param>
        /// <param name="getText">The panel's current content as text; null/empty disables Copy and Save.</param>
        /// <param name="clear">Clears the panel. Null leaves the Clear button disabled.</param>
        public static ResultsToolbar Attach(Control host, string subject, Func<string> getText, Action clear)
        {
            if (host == null) return null;
            var bar = new ResultsToolbar(subject, getText, clear);
            host.Controls.Add(bar);
            return bar;
        }

        private ResultsToolbar(string subject, Func<string> getText, Action clear)
        {
            _subject = string.IsNullOrEmpty(subject) ? "results" : subject;
            _getText = getText;

            Dock = DockStyle.Top;
            Height = 26;
            BackColor = LunaColors.ToolbarBackground;

            SaveButton = MakeIconButton(ToolbarIcons.Save(), "Save the " + _subject + " to a .txt file");
            SaveButton.Click += delegate { SaveToFile(); };

            CopyButton = MakeIconButton(ToolbarIcons.Copy(), "Copy the " + _subject + " to the clipboard");
            CopyButton.Click += delegate { CopyToClipboard(); };

            ClearButton = MakeIconButton(ToolbarIcons.Clear(), "Clear the " + _subject);
            ClearButton.Enabled = clear != null;
            if (clear != null) ClearButton.Click += delegate { try { clear(); } catch { } };

            // Added right-to-left because each docks Left: Clear ends up first.
            Controls.Add(SaveButton);
            Controls.Add(CopyButton);
            Controls.Add(ClearButton);
        }

        // 28x24 flat icon-only button, tooltip-only label — matches TerminalPanel.MakeIconButton so
        // the two toolbars read as the same control.
        private Button MakeIconButton(Image icon, string tooltip)
        {
            var btn = new Button
            {
                Dock = DockStyle.Left,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Image = icon,
                ImageAlign = ContentAlignment.MiddleCenter,
                Text = string.Empty,
                TabStop = false,
            };
            btn.FlatAppearance.BorderSize = 0;
            _tips.SetToolTip(btn, tooltip);
            return btn;
        }

        private string CurrentText()
        {
            if (_getText == null) return string.Empty;
            try { return _getText() ?? string.Empty; } catch { return string.Empty; }
        }

        private void CopyToClipboard()
        {
            string text = CurrentText();
            if (string.IsNullOrEmpty(text)) return;
            // Clipboard access fails transiently when another process holds it — never take the
            // IDE down over a copy.
            try { Clipboard.SetText(text); } catch { }
        }

        private void SaveToFile()
        {
            string text = CurrentText();
            if (string.IsNullOrEmpty(text)) return;
            using (var dlg = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = SafeFileName(_subject) + ".txt",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, text); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save the " + _subject + ": " + ex.Message,
                        "Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string SafeFileName(string subject)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in subject.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _tips.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
