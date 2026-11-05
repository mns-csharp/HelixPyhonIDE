namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.CodeReview;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Displays a <see cref="ReviewReport"/>: the verdict, what was and was not examined, one row
    /// per comment, and the selected comment's full rationale and suggested change.
    ///
    /// Two pieces of this are not decoration. The verdict strip carries the "N file(s) could not be
    /// parsed" notice, because a reviewer who reads only the verdict must still be told the review
    /// was partial. And the detail pane exists because a review comment is three things — what is
    /// wrong, why it matters, what to do — and a grid row can only hold the first.
    /// </summary>
    public sealed class CodeReviewPanel : UserControl
    {
        private readonly ListView _list;
        private readonly TextBox _details;
        private readonly Label _verdictLabel;
        private readonly Label _incompleteLabel;
        private readonly ComboBox _severityFilter;
        private readonly ComboBox _categoryFilter;
        private readonly TextBox _searchBox;
        private readonly Label _empty;
        private readonly ToolTip _tips = new ToolTip();

        // The unfiltered report from the last run. The filters produce a VIEW over this, so
        // clearing a filter restores every row without re-running the engine over the solution.
        private ReviewReport _report;

        /// <summary>Raised when a row is activated: file path + line.</summary>
        public event Action<string, int> ItemActivated;

        /// <summary>The last report shown, or null. Used by Analysis ▸ Code Review ▸ Export.</summary>
        public ReviewReport LastReport { get { return _report; } }

        private const string HowToRun =
            "Run Analysis ▸ Code Review ▸ Review Whole Solution (Ctrl+Shift+R) to review the code.";

        public CodeReviewPanel()
        {
            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
                MultiSelect = true,
            };
            _list.Columns.Add(new ColumnHeader { Text = "Severity", Width = 70 });
            _list.Columns.Add(new ColumnHeader { Text = "Category", Width = 100 });
            _list.Columns.Add(new ColumnHeader { Text = "Rule", Width = 90 });
            _list.Columns.Add(new ColumnHeader { Text = "File", Width = 140 });
            _list.Columns.Add(new ColumnHeader { Text = "Line", Width = 50 });
            _list.Columns.Add(new ColumnHeader { Text = "Member", Width = 150 });
            _list.Columns.Add(new ColumnHeader { Text = "Comment", Width = 520 });
            SortableListView.Enable(_list, 4);   // Line is numeric
            // Worst-first, matching the engine's own ordering — an A-Z sort on the severity text
            // would put Blocker after Critical and Info before Major, i.e. exactly backwards.
            SortableListView.SetTextRankColumn(_list, 0, new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Blocker", 0 }, { "Critical", 1 }, { "Major", 2 }, { "Minor", 3 }, { "Info", 4 },
            });
            ListViewClipboard.AttachCopyMenu(_list);
            _list.DoubleClick += delegate { ActivateSelected(); };
            _list.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { ActivateSelected(); e.Handled = true; }
            };
            _list.SelectedIndexChanged += delegate { ShowDetailsForSelection(); };

            _details = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                Font = LunaFonts.Ui,
                Text = "Select a finding to see why it matters and what to change.",
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
            };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(_details);
            // SplitterDistance is validated against the CURRENT height, and a freshly-constructed
            // SplitContainer is 150px tall — setting it here would throw before the panel is laid
            // out. Do it once, on the first resize that gives it room.
            EventHandler placeSplitter = null;
            placeSplitter = delegate
            {
                if (split.Height < 160) return;
                split.SizeChanged -= placeSplitter;
                try { split.SplitterDistance = (int)(split.Height * 0.62); }
                catch (InvalidOperationException) { /* leave the default */ }
            };
            split.SizeChanged += placeSplitter;

            // --- filter row ---
            _severityFilter = MakeFilter(new object[] { "All severities", "Blocker", "Critical", "Major", "Minor", "Info" });
            _categoryFilter = MakeFilter(new object[]
            {
                "All categories", "Correctness", "Security", "ErrorHandling", "Performance",
                "Design", "Maintainability", "Documentation", "Testing",
            });
            _searchBox = new TextBox { Dock = DockStyle.Fill };
            _searchBox.TextChanged += delegate { ApplyFilter(); };
            SetCue(_searchBox, "Search rule, file, member or comment…");

            var filters = new Panel { Dock = DockStyle.Top, Height = 26 };
            filters.Controls.Add(_searchBox);
            filters.Controls.Add(_categoryFilter);
            filters.Controls.Add(_severityFilter);

            // --- verdict strip ---
            _verdictLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(LunaFonts.Ui, FontStyle.Bold),
                Text = "No review yet.",
                Padding = new Padding(6, 0, 0, 0),
            };
            _incompleteLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DarkRed,
                Padding = new Padding(6, 0, 0, 0),
                Visible = false,
            };
            var verdict = new Panel { Dock = DockStyle.Top, Height = 42 };
            verdict.Controls.Add(_incompleteLabel);
            verdict.Controls.Add(_verdictLabel);

            Controls.Add(split);
            Controls.Add(filters);
            ResultsToolbar.Attach(this, "code review", BuildExportText, Clear);
            Controls.Add(verdict);

            _empty = ListEmptyState.Attach(_list,
                "No code-review findings yet." + Environment.NewLine + Environment.NewLine + HowToRun);
        }

        /// <summary>Replaces the panel's contents with a fresh report.</summary>
        public void Show(ReviewReport report)
        {
            _report = report;
            _severityFilter.SelectedIndex = 0;
            _categoryFilter.SelectedIndex = 0;
            _searchBox.Text = string.Empty;
            UpdateVerdictStrip();
            ApplyFilter();
        }

        public void Clear()
        {
            _report = null;
            _severityFilter.SelectedIndex = 0;
            _categoryFilter.SelectedIndex = 0;
            _searchBox.Text = string.Empty;
            _details.Text = "Select a finding to see why it matters and what to change.";
            UpdateVerdictStrip();
            PopulateRows(new List<ReviewFinding>());
            SortableListView.ResetSort(_list);
        }

        // The verdict is a claim about the whole submission, so it must carry its own caveats:
        // an incomplete review says so on the same strip, in red, next to the verdict it qualifies.
        private void UpdateVerdictStrip()
        {
            if (_report == null)
            {
                _verdictLabel.Text = "No review yet.";
                _verdictLabel.ForeColor = SystemColors.ControlText;
                _incompleteLabel.Visible = false;
                return;
            }

            _verdictLabel.Text = ReviewMarkdownWriter.VerdictText(_report.Verdict) + " — " + _report.VerdictReason +
                "   (" + _report.Summary.FilesReviewed + " file(s), " +
                _report.Summary.FunctionsReviewed + " function(s) reviewed)";
            _verdictLabel.ForeColor = VerdictColor(_report.Verdict);

            var caveats = new List<string>();
            if (_report.Cancelled) caveats.Add("cancelled before finishing");
            if (_report.Unparsed.Count > 0)
                caveats.Add(_report.Unparsed.Count + " file(s) did not parse and were NOT reviewed");
            if (_report.RuleFailures.Count > 0)
                caveats.Add(_report.RuleFailures.Count + " check(s) failed to run");

            _incompleteLabel.Visible = caveats.Count > 0;
            _incompleteLabel.Text = caveats.Count == 0
                ? string.Empty
                : "Incomplete review: " + string.Join("; ", caveats.ToArray()) + ".";
            // The strip summarises; hovering gives the actual list of files and checks, because
            // "3 file(s) did not parse" is only actionable once you know which three.
            if (caveats.Count > 0)
                _tips.SetToolTip(_incompleteLabel, string.Join(Environment.NewLine,
                    _report.Unparsed.Concat(_report.RuleFailures).ToArray()));
        }

        private void ApplyFilter()
        {
            IEnumerable<ReviewFinding> findings = _report != null
                ? (IEnumerable<ReviewFinding>)_report.Findings
                : new ReviewFinding[0];

            if (_severityFilter.SelectedIndex > 0)
            {
                string wanted = (string)_severityFilter.SelectedItem;
                findings = findings.Where(f => f.Severity.ToString() == wanted);
            }
            if (_categoryFilter.SelectedIndex > 0)
            {
                string wanted = (string)_categoryFilter.SelectedItem;
                findings = findings.Where(f => f.Category.ToString() == wanted);
            }

            string term = _searchBox.Text.Trim();
            if (term.Length > 0)
                findings = findings.Where(f => Matches(f, term));

            var rows = findings.ToList();
            PopulateRows(rows);

            bool narrowed = _report != null && rows.Count == 0 && _report.Findings.Count > 0;
            _empty.Text = narrowed
                ? "No findings match the current filter."
                : _report != null && _report.Findings.Count == 0
                    ? "No findings — " + _report.VerdictReason
                    : "No code-review findings yet." + Environment.NewLine + Environment.NewLine + HowToRun;
        }

        private static bool Matches(ReviewFinding finding, string term)
        {
            return Contains(finding.RuleId, term) || Contains(finding.Title, term) ||
                   Contains(finding.Message, term) || Contains(finding.MemberName, term) ||
                   Contains(finding.FilePath, term);
        }

        private static bool Contains(string text, string term)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PopulateRows(IEnumerable<ReviewFinding> findings)
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (ReviewFinding finding in findings)
                {
                    var item = new ListViewItem(finding.Severity.ToString());
                    item.SubItems.Add(finding.Category.ToString());
                    item.SubItems.Add(finding.RuleId);
                    item.SubItems.Add(SafeFileName(finding.FilePath));
                    item.SubItems.Add(finding.Line.ToString());
                    item.SubItems.Add(finding.MemberName);
                    item.SubItems.Add(finding.Message);
                    item.Tag = finding;
                    item.ToolTipText = finding.FilePath;
                    item.ForeColor = SeverityColor(finding.Severity);
                    _list.Items.Add(item);
                }
            }
            finally { _list.EndUpdate(); }
            SortableListView.ReapplySort(_list);
            ListEmptyState.Refresh(_list, _empty);
        }

        private void ShowDetailsForSelection()
        {
            if (_list.SelectedItems.Count == 0) return;
            ReviewFinding finding = _list.SelectedItems[0].Tag as ReviewFinding;
            if (finding == null) return;

            var text = new StringBuilder();
            text.AppendLine(finding.RuleId + " — " + finding.Title);
            text.AppendLine(finding.Severity + " · " + finding.Category + " · confidence " +
                finding.Confidence + " · effort " + finding.Effort);
            text.AppendLine(SafeFileName(finding.FilePath) + ":" + finding.Line +
                (finding.MemberName.Length > 0 ? "  in " + finding.MemberName : string.Empty));
            text.AppendLine();
            if (!string.IsNullOrEmpty(finding.Snippet)) { text.AppendLine("    " + finding.Snippet); text.AppendLine(); }
            text.AppendLine(finding.Message);
            text.AppendLine();
            text.AppendLine("Why it matters");
            text.AppendLine(finding.Rationale);
            text.AppendLine();
            text.AppendLine("Suggested change");
            text.AppendLine(finding.Suggestion);
            _details.Text = text.ToString();
            // Scrolling back to the top is cosmetic and needs a realized control; never let it
            // take the panel down with it.
            try { _details.SelectionStart = 0; _details.ScrollToCaret(); }
            catch { }
        }

        private void ActivateSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            ReviewFinding finding = _list.SelectedItems[0].Tag as ReviewFinding;
            if (finding == null) return;
            Action<string, int> handler = ItemActivated;
            if (handler != null) handler(finding.FilePath, finding.Line);
        }

        // The toolbar's Copy/Save hand over the full write-up rather than the grid's TSV: the grid
        // shows one line per finding, and the rationale and suggestion are the parts worth sharing.
        private string BuildExportText()
        {
            return _report == null ? string.Empty : ReviewMarkdownWriter.Write(_report);
        }

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFileName(path); }
            catch { return path; }
        }

        private static Color SeverityColor(ReviewSeverity severity)
        {
            switch (severity)
            {
                case ReviewSeverity.Blocker: return Color.FromArgb(150, 0, 0);
                case ReviewSeverity.Critical: return Color.DarkRed;
                case ReviewSeverity.Major: return Color.DarkOrange;
                case ReviewSeverity.Minor: return Color.DarkBlue;
                default: return Color.DimGray;
            }
        }

        private static Color VerdictColor(ReviewVerdict verdict)
        {
            switch (verdict)
            {
                case ReviewVerdict.RequestChanges: return Color.DarkRed;
                case ReviewVerdict.ApproveWithComments: return Color.FromArgb(150, 90, 0);
                default: return Color.FromArgb(0, 110, 0);
            }
        }

        private ComboBox MakeFilter(object[] items)
        {
            var box = new ComboBox
            {
                Dock = DockStyle.Left,
                Width = 130,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            box.Items.AddRange(items);
            box.SelectedIndex = 0;
            box.SelectedIndexChanged += delegate { ApplyFilter(); };
            return box;
        }

        // Grey placeholder text in the search box. A TextBox may not have its native handle yet at
        // construction time, so (re)apply the cue once it is created.
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
    }
}
