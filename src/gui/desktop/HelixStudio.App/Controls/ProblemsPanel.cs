namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.VirtualCompiler;

    /// <summary>
    /// Displays diagnostics produced by <see cref="VirtualCompiler"/>. Mirrors the
    /// existing <c>AnalysisFeedbackPanel</c> column/color convention but with a
    /// source-location column set (File / Line / Code / Message) and a
    /// double-click event that surfaces the selected diagnostic so the hosting
    /// form can jump to the offending line.
    /// </summary>
    public class ProblemsPanel : UserControl
    {
        private ListView _listView;
        private Label _emptyLabel;
        private ImageList _severityIcons;
        private ToolStrip _filterBar;
        private ToolStripButton _btnErrors;
        private ToolStripButton _btnWarnings;
        private ToolStripButton _btnMessages;
        private ToolStripTextBox _filterText;
        private ColumnHeader _colSeverity;
        private ColumnHeader _colFile;
        private ColumnHeader _colLine;
        private ColumnHeader _colCode;
        private ColumnHeader _colMessage;

        // The full diagnostic set; the ListView shows only the severities whose filter toggle is on.
        private readonly List<CompileDiagnostic> _all = new List<CompileDiagnostic>();

        // Row index F8 / Shift+F8 navigation resumes from (-1 = none yet). Tracked explicitly so it
        // works even before the control has a native handle (unit tests); kept in sync with clicks.
        private int _navIndex = -1;

        public event EventHandler<CompileDiagnostic> DiagnosticActivated;

        /// <summary>
        /// Raised when the user asks for the offered resolution of the selected diagnostic. Only
        /// diagnostics <see cref="HasQuickFix"/> accepts can raise it; the host performs the fix.
        /// </summary>
        public event EventHandler<CompileDiagnostic> QuickFixRequested;

        /// <summary>
        /// Whether this diagnostic has a resolution the IDE can carry out.
        ///
        /// PY8001 ("not part of any project") is the one that most needs it: it is a hard blocker on
        /// Run and Debug, and the message can only tell the user what is wrong, not do anything
        /// about it. PY8002 is deliberately absent — turning procedural code into classes is a
        /// refactor with judgement in it, not a one-click move, and the Preferences toggle already
        /// covers the "I cannot restructure this code" case.
        /// </summary>
        public static bool HasQuickFix(CompileDiagnostic d)
        {
            return d != null
                && !string.IsNullOrEmpty(d.FilePath)
                && string.Equals(d.Code, VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace,
                                 StringComparison.Ordinal);
        }

        /// <summary>The label the fix is offered under — also used by the host's confirmation copy.</summary>
        public const string QuickFixCaption = "Add to Project…";

        // Raised whenever the diagnostic set changes, so the host can mirror the counts in the status
        // bar. Read ErrorCount / WarningCount / MessageCount for the current totals.
        public event EventHandler DiagnosticsChanged;

        public int ErrorCount { get { return CountOf(true, false, false); } }
        public int WarningCount { get { return CountOf(false, true, false); } }
        public int MessageCount { get { return CountOf(false, false, true); } }
        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _all.AsReadOnly(); } }

        private int CountOf(bool errors, bool warnings, bool infos)
        {
            int n = 0;
            foreach (CompileDiagnostic d in _all)
            {
                bool isErr = d.Severity == ParseSeverity.Error || d.Severity == ParseSeverity.Fatal;
                bool isWarn = d.Severity == ParseSeverity.Warning;
                if ((errors && isErr) || (warnings && isWarn) || (infos && !isErr && !isWarn)) n++;
            }
            return n;
        }

        private void RaiseDiagnosticsChanged()
        {
            EventHandler h = DiagnosticsChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public ProblemsPanel()
        {
            InitializeControls();
        }

        // Replace the list with a fresh diagnostic set from the virtual compiler.
        public void ShowDiagnostics(IReadOnlyList<CompileDiagnostic> diagnostics)
        {
            _all.Clear();
            if (diagnostics != null) _all.AddRange(diagnostics);
            UpdateFilterCounts();
            Repopulate();
            RaiseDiagnosticsChanged();
        }

        /// <summary>
        /// Drop one diagnostic that has just been resolved, so the list reflects the fix straight
        /// away instead of showing a stale blocker until the next Compile. Returns false when the
        /// diagnostic was not in the list.
        /// </summary>
        public bool RemoveDiagnostic(CompileDiagnostic diagnostic)
        {
            if (diagnostic == null || !_all.Remove(diagnostic)) return false;
            UpdateFilterCounts();
            Repopulate();
            RaiseDiagnosticsChanged();
            return true;
        }

        // Fill the ListView from _all, skipping severities whose filter toggle is off.
        private void Repopulate()
        {
            _navIndex = -1;   // a fresh list restarts F8 navigation from the top
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                foreach (CompileDiagnostic d in _all)
                {
                    if (!IsSeverityVisible(d.Severity)) continue;
                    if (!TextMatches(d)) continue;
                    ListViewItem item = new ListViewItem(GetSeverityText(d.Severity));
                    item.ImageIndex = SeverityIconIndex(d.Severity);   // error/warning/info glyph
                    // File column shows leaf name only; full path is the tooltip.
                    item.SubItems.Add(string.IsNullOrEmpty(d.FilePath) ? "" : Path.GetFileName(d.FilePath));
                    item.SubItems.Add(d.Span.Start.Line.ToString());
                    item.SubItems.Add(d.Code);
                    item.SubItems.Add(d.Message);
                    // Keep the diagnostic so activation can raise DiagnosticActivated with it.
                    item.Tag = d;
                    // A diagnostic the IDE can fix says so on the row itself — a resolution nobody
                    // knows exists is the same as no resolution.
                    item.ToolTipText = HasQuickFix(d)
                        ? d.FilePath + Environment.NewLine
                          + "Right-click ▸ " + QuickFixCaption + "  (Ctrl+.)"
                        : d.FilePath;

                    // Colour the row by severity (fatal/error red, warning orange, info blue).
                    switch (d.Severity)
                    {
                        case ParseSeverity.Fatal:
                        case ParseSeverity.Error: item.ForeColor = Color.DarkRed; break;
                        case ParseSeverity.Warning: item.ForeColor = Color.DarkOrange; break;
                        case ParseSeverity.Info: item.ForeColor = Color.DarkBlue; break;
                        default: item.ForeColor = Color.Gray; break;
                    }

                    _listView.Items.Add(item);
                }
            }
            finally
            {
                _listView.EndUpdate();
            }
            // Grow the Message column to whatever the longest message needs. At the fixed 500px it
            // was cut off mid-sentence with an ellipsis and no way to read the rest — and a
            // diagnostic the user cannot finish reading cannot be acted on. Horizontal scroll is
            // the honest trade.
            if (_listView.Items.Count > 0)
            {
                _colMessage.Width = -1;   // -1 = size to the widest cell
                if (_colMessage.Width < DefaultMessageWidth) _colMessage.Width = DefaultMessageWidth;
            }
            else
            {
                _colMessage.Width = DefaultMessageWidth;
            }

            // Preserve the user's sticky sort/glyph after the repopulate.
            SortableListView.ReapplySort(_listView);
            UpdateEmptyStateText();
            ListEmptyState.Refresh(_listView, _emptyLabel);
        }

        // The Problems list can end up empty for three different reasons, and a single fixed message
        // ("Run Compile") is wrong for two of them. Say which one it actually is: a live text filter
        // that hides everything, all severities toggled off, or genuinely nothing to report.
        private void UpdateEmptyStateText()
        {
            if (_emptyLabel == null) return;
            string filter = _filterText != null ? _filterText.Text.Trim() : string.Empty;
            if (_all.Count > 0 && _listView.Items.Count == 0)
            {
                if (filter.Length > 0)
                {
                    _emptyLabel.Text = "No problems match “" + filter + "”." + Environment.NewLine + Environment.NewLine
                        + "Clear the filter box to see all " + _all.Count + " diagnostic(s).";
                    return;
                }
                _emptyLabel.Text = "All severities are hidden." + Environment.NewLine + Environment.NewLine
                    + "Re-enable Errors / Warnings / Messages above to see the " + _all.Count + " diagnostic(s).";
                return;
            }
            _emptyLabel.Text = DefaultEmptyText;
        }

        private const string DefaultEmptyText =
            "No compiler diagnostics.\r\n\r\nRun Build → Compile (F6) to check the solution.";

        // Show a grey placeholder in the filter box while it is empty. A ToolStripTextBox may not have
        // its native handle yet at construction, so (re)apply the cue whenever the handle is created.
        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCue(TextBox box, string cue)
        {
            if (box == null) return;
            EventHandler apply = delegate
            {
                try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, cue); }
                catch { /* cosmetic only */ }
            };
            if (box.IsHandleCreated) apply(box, EventArgs.Empty);
            box.HandleCreated += apply;
        }

        // True when the diagnostic matches the live text filter (case-insensitive substring over the
        // message, code and file name — the columns a user scans). Empty filter matches everything.
        private bool TextMatches(CompileDiagnostic d)
        {
            string filter = _filterText != null ? _filterText.Text.Trim() : string.Empty;
            if (filter.Length == 0) return true;
            filter = filter.ToLowerInvariant();
            if (!string.IsNullOrEmpty(d.Message) && d.Message.ToLowerInvariant().Contains(filter)) return true;
            if (!string.IsNullOrEmpty(d.Code) && d.Code.ToLowerInvariant().Contains(filter)) return true;
            if (!string.IsNullOrEmpty(d.FilePath) && Path.GetFileName(d.FilePath).ToLowerInvariant().Contains(filter)) return true;
            return false;
        }

        private bool IsSeverityVisible(ParseSeverity s)
        {
            switch (s)
            {
                case ParseSeverity.Fatal:
                case ParseSeverity.Error: return _btnErrors.Checked;
                case ParseSeverity.Warning: return _btnWarnings.Checked;
                default: return _btnMessages.Checked;   // Info and anything lower
            }
        }

        // Refresh the "N Errors / M Warnings / K Messages" toggle labels from the full set.
        private void UpdateFilterCounts()
        {
            int e = 0, w = 0, i = 0;
            foreach (CompileDiagnostic d in _all)
            {
                switch (d.Severity)
                {
                    case ParseSeverity.Fatal:
                    case ParseSeverity.Error: e++; break;
                    case ParseSeverity.Warning: w++; break;
                    default: i++; break;
                }
            }
            _btnErrors.Text = e + (e == 1 ? " Error" : " Errors");
            _btnWarnings.Text = w + (w == 1 ? " Warning" : " Warnings");
            _btnMessages.Text = i + (i == 1 ? " Message" : " Messages");
        }

        private void OnFilterToggled(object sender, EventArgs e)
        {
            Repopulate();
        }

        private const int DefaultMessageWidth = 500;

        public void Clear()
        {
            _all.Clear();
            UpdateFilterCounts();
            _listView.Items.Clear();
            SortableListView.ResetSort(_listView);
            ListEmptyState.Refresh(_listView, _emptyLabel);
            RaiseDiagnosticsChanged();
        }

        // Build the single fill ListView and its five columns. SuspendLayout/ResumeLayout
        // brackets the setup so the layout engine runs once at the end.
        private void InitializeControls()
        {
            SuspendLayout();

            _listView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
                // Keep the F8-navigated row highlighted even after focus moves to the editor.
                HideSelection = false,
            };
            _listView.DoubleClick += OnListViewDoubleClick;
            _listView.KeyDown += OnListViewKeyDown;
            // Keep F8 navigation anchored to whatever row the user last clicked/selected.
            _listView.SelectedIndexChanged += delegate
            {
                if (_listView.SelectedIndices.Count > 0) _navIndex = _listView.SelectedIndices[0];
            };
            // Severity glyphs (error/warning/info), the way a professional Error List reads.
            _severityIcons = BuildSeverityIcons();
            _listView.SmallImageList = _severityIcons;

            // Severity filter toggles with live counts, like Visual Studio's Error List. Each button
            // shows its glyph + count and hides that severity's rows when toggled off. All on by default.
            _filterBar = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                ImageList = _severityIcons,
                RenderMode = ToolStripRenderMode.System,
            };
            _btnErrors = new ToolStripButton("0 Errors") { CheckOnClick = true, Checked = true, ImageIndex = 0 };
            _btnWarnings = new ToolStripButton("0 Warnings") { CheckOnClick = true, Checked = true, ImageIndex = 1 };
            _btnMessages = new ToolStripButton("0 Messages") { CheckOnClick = true, Checked = true, ImageIndex = 2 };
            _btnErrors.CheckedChanged += OnFilterToggled;
            _btnWarnings.CheckedChanged += OnFilterToggled;
            _btnMessages.CheckedChanged += OnFilterToggled;
            _filterBar.Items.Add(_btnErrors);
            _filterBar.Items.Add(_btnWarnings);
            _filterBar.Items.Add(_btnMessages);

            // A text filter, right-aligned, narrowing the list by message / code / file — the Error
            // List search box a large solution's diagnostics need to be navigable. Composes with the
            // severity toggles (a row shows only when its severity is on AND it matches the text).
            _filterText = new ToolStripTextBox
            {
                Alignment = ToolStripItemAlignment.Right,
                AutoSize = false,
                Width = 200,
                ToolTipText = "Filter problems by message, code or file",
            };
            SetCue(_filterText.TextBox, "Filter problems…");
            _filterText.TextChanged += delegate { Repopulate(); };
            _filterBar.Items.Add(_filterText);

            _colSeverity = new ColumnHeader { Text = "Severity", Width = 80 };
            _colFile = new ColumnHeader { Text = "File", Width = 160 };
            _colLine = new ColumnHeader { Text = "Line", Width = 55 };
            _colCode = new ColumnHeader { Text = "Code", Width = 70 };
            _colMessage = new ColumnHeader { Text = "Message", Width = DefaultMessageWidth };

            _listView.Columns.Add(_colSeverity);
            _listView.Columns.Add(_colFile);
            _listView.Columns.Add(_colLine);
            _listView.Columns.Add(_colCode);
            _listView.Columns.Add(_colMessage);

            SortableListView.Enable(_listView, 2); // Line is numeric
            // The Severity column reads "Error/Warning/…"; sort it by severity rank (the row's glyph),
            // not alphabetically, so the Error List groups Errors → Warnings → Messages.
            SortableListView.SetIconRankColumn(_listView, 0);

            Controls.Add(_listView);
            // Until a compile runs this grid is a wall of blank ruled rows, which reads as broken
            // rather than "nothing to report" — see ListEmptyState.
            _emptyLabel = ListEmptyState.Attach(_listView,
                "No compiler diagnostics." + Environment.NewLine + Environment.NewLine +
                "Run Build → Compile (F6) to check the solution.");

            // Added last so the Top-docked filter bar reserves the top edge above the Fill list.
            Controls.Add(_filterBar);
            ResultsToolbar.Attach(this, "compile diagnostics",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_listView, false), Clear);
            UpdateFilterCounts();

            // Right-click to copy diagnostics — the standard Error List affordance for sharing them.
            var menu = new ContextMenuStrip();

            // …and, for the diagnostics that have one, to APPLY the resolution rather than only read
            // about it. Listed first and in bold: when a row is a hard blocker on Run/Debug, acting
            // on it is what the user came to the panel for.
            _quickFixItem = new ToolStripMenuItem(QuickFixCaption, null, delegate { RequestQuickFix(); });
            _quickFixItem.Font = new Font(_quickFixItem.Font, FontStyle.Bold);
            _quickFixSeparator = new ToolStripSeparator();
            menu.Items.Add(_quickFixItem);
            menu.Items.Add(_quickFixSeparator);
            menu.Opening += delegate
            {
                bool applicable = HasQuickFix(FirstSelected());
                _quickFixItem.Enabled = applicable;
                _quickFixItem.Visible = applicable;
                _quickFixSeparator.Visible = applicable;
            };

            var selectAll = new ToolStripMenuItem("Select All", null, delegate { HelixPythonNamespace.UI.Controls.ListViewClipboard.SelectAll(_listView); });
            selectAll.ShortcutKeyDisplayString = "Ctrl+A";
            menu.Items.Add(selectAll);
            var copy = new ToolStripMenuItem("Copy", null, delegate { CopyRows(SelectedDiagnostics()); });
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            menu.Items.Add(copy);
            menu.Items.Add(new ToolStripMenuItem("Copy All", null, delegate { CopyRows(VisibleDiagnostics()); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Clear", null, delegate { Clear(); }));
            _listView.ContextMenuStrip = menu;

            ResumeLayout(false);
        }

        // The context-menu entry offering the selected diagnostic's resolution, and the rule that
        // separates it from the copy/clear commands. Both hide themselves for rows that have none.
        private ToolStripMenuItem _quickFixItem;
        private ToolStripSeparator _quickFixSeparator;

        /// <summary>The diagnostic on the selected row, or null when nothing is selected.</summary>
        public CompileDiagnostic FirstSelected()
        {
            if (_listView.SelectedItems.Count == 0) return null;
            return _listView.SelectedItems[0].Tag as CompileDiagnostic;
        }

        /// <summary>
        /// Ask the host to apply the selected diagnostic's resolution. Public so the keyboard path
        /// and tests reach the same code the menu entry does. No-op for rows without a fix.
        /// </summary>
        public bool RequestQuickFix()
        {
            CompileDiagnostic d = FirstSelected();
            if (!HasQuickFix(d)) return false;
            EventHandler<CompileDiagnostic> h = QuickFixRequested;
            if (h == null) return false;
            h(this, d);
            return true;
        }

        private System.Collections.Generic.IEnumerable<CompileDiagnostic> SelectedDiagnostics()
        {
            foreach (ListViewItem it in _listView.SelectedItems)
                if (it.Tag is CompileDiagnostic d) yield return d;
        }

        private System.Collections.Generic.IEnumerable<CompileDiagnostic> VisibleDiagnostics()
        {
            foreach (ListViewItem it in _listView.Items)
                if (it.Tag is CompileDiagnostic d) yield return d;
        }

        private void CopyRows(System.Collections.Generic.IEnumerable<CompileDiagnostic> rows)
        {
            string text = BuildClipboardText(rows);
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { /* clipboard access can fail transiently */ }
        }

        // One clipboard line per diagnostic: "file(line): severity CODE: message" — the compiler-style
        // form editors and issue trackers understand.
        internal static string FormatDiagnosticLine(CompileDiagnostic d)
        {
            string file = string.IsNullOrEmpty(d.FilePath) ? string.Empty : Path.GetFileName(d.FilePath);
            return file + "(" + d.Span.Start.Line + "): " + GetSeverityText(d.Severity).ToLowerInvariant()
                + " " + d.Code + ": " + d.Message;
        }

        internal static string BuildClipboardText(System.Collections.Generic.IEnumerable<CompileDiagnostic> rows)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (CompileDiagnostic d in rows) lines.Add(FormatDiagnosticLine(d));
            return string.Join(Environment.NewLine, lines);
        }

        private void OnListViewDoubleClick(object sender, EventArgs e)
        {
            ActivateSelected();
        }

        // Enter activates the selected row, same as double-click.
        private void OnListViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                ActivateSelected();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                HelixPythonNamespace.UI.Controls.ListViewClipboard.SelectAll(_listView);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                CopyRows(SelectedDiagnostics());
                e.Handled = true;
            }
            else if (e.Control && (e.KeyCode == Keys.OemPeriod || e.KeyCode == Keys.Decimal))
            {
                // Ctrl+. — the quick-actions chord every editor shares, so the fix is reachable
                // without hunting through a context menu.
                if (RequestQuickFix()) e.Handled = true;
            }
        }

        // Raise DiagnosticActivated with the selected row's diagnostic so the host can jump to it.
        private void ActivateSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            ListViewItem sel = _listView.SelectedItems[0];
            CompileDiagnostic d = sel.Tag as CompileDiagnostic;
            if (d == null) return;
            EventHandler<CompileDiagnostic> h = DiagnosticActivated;
            if (h != null) h(this, d);
        }

        // Move the selection by <paramref name="direction"/> (+1 next, -1 previous), wrapping around
        // the ends, then activate it so the host jumps to that diagnostic. Drives F8 / Shift+F8.
        // Returns false when there is nothing to navigate.
        public bool ActivateAdjacent(int direction)
        {
            int count = _listView.Items.Count;
            if (count == 0) { _navIndex = -1; return false; }
            int next;
            if (_navIndex < 0 || _navIndex >= count) next = direction >= 0 ? 0 : count - 1;
            else next = ((_navIndex + direction) % count + count) % count;   // wrap in both directions
            _navIndex = next;
            _listView.SelectedItems.Clear();
            _listView.Items[next].Selected = true;
            _listView.Items[next].EnsureVisible();
            _listView.Select();
            ActivateRow(next);
            return true;
        }

        // Raise DiagnosticActivated for the row at <paramref name="index"/> (by Tag, not by the
        // selection collection — that is empty before the control has a handle).
        private void ActivateRow(int index)
        {
            if (index < 0 || index >= _listView.Items.Count) return;
            CompileDiagnostic d = _listView.Items[index].Tag as CompileDiagnostic;
            if (d == null) return;
            EventHandler<CompileDiagnostic> h = DiagnosticActivated;
            if (h != null) h(this, d);
        }

        // Severity glyph index into _severityIcons (0 error, 1 warning, 2 info).
        private static int SeverityIconIndex(ParseSeverity s)
        {
            switch (s)
            {
                case ParseSeverity.Fatal:
                case ParseSeverity.Error: return 0;
                case ParseSeverity.Warning: return 1;
                default: return 2;   // Info and anything lower
            }
        }

        // A 16px ImageList of drawn severity glyphs — no external assets, so it renders identically
        // in every theme: a red ✕ disc (error), an amber ! triangle (warning), a blue i disc (info).
        private static ImageList BuildSeverityIcons()
        {
            ImageList il = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            il.Images.Add("error", MakeErrorIcon());
            il.Images.Add("warning", MakeWarningIcon());
            il.Images.Add("info", MakeInfoIcon());
            return il;
        }

        private static Bitmap MakeErrorIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 32, 32))) g.FillEllipse(b, 2, 2, 12, 12);
                using (Pen p = new Pen(Color.White, 2f)) { g.DrawLine(p, 5, 5, 11, 11); g.DrawLine(p, 11, 5, 5, 11); }
            }
            return bmp;
        }

        private static Bitmap MakeWarningIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Point[] tri = { new Point(8, 1), new Point(15, 14), new Point(1, 14) };
                using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 160, 0))) g.FillPolygon(b, tri);
                using (Pen p = new Pen(Color.White, 2f)) g.DrawLine(p, 8, 6, 8, 10);
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, 7, 11, 2, 2);
            }
            return bmp;
        }

        private static Bitmap MakeInfoIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(30, 90, 200))) g.FillEllipse(b, 2, 2, 12, 12);
                using (Pen p = new Pen(Color.White, 2f)) g.DrawLine(p, 8, 7, 8, 11);
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, 7, 4, 2, 2);
            }
            return bmp;
        }

        // Severity enum -> display label.
        private static string GetSeverityText(ParseSeverity s)
        {
            switch (s)
            {
                case ParseSeverity.Fatal: return "Fatal";
                case ParseSeverity.Error: return "Error";
                case ParseSeverity.Warning: return "Warning";
                case ParseSeverity.Info: return "Info";
                case ParseSeverity.Hidden: return "Hidden";
                default: return s.ToString();
            }
        }
    }
}
