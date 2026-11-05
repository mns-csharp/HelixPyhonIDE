namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    /// <summary>
    /// Global find-type dialog. Live-filter by substring on Name or FullyQualifiedName,
    /// Enter selects and closes with OK. Prefix-matches rank above substring-matches.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class FindTypesDialog : LunaForm
    {
        // OK button handler — commits whatever row is currently selected.
        private void OnAcceptSelectionClick(object sender, EventArgs e) { AcceptSelection(); }
        public FindTypesDialog()
        {
            InitializeComponent();
        }

        // Cap on rows rendered per keystroke; excess matches are hidden (with a "showing N of M" hint)
        // to keep the ListView responsive on large solutions.
        private const int MaxResults = 500;

        private readonly SolutionModel _solution;
        private readonly List<TypeModel> _allTypes;
        // Search index with name/FQN pre-lowercased once, so live filtering does not
        // re-allocate two lowercase strings per type on every keystroke.
        private readonly List<IndexEntry> _index;





        // The chosen type, read by the caller after DialogResult.OK; null if the dialog was cancelled.
        public TypeModel SelectedType { get; private set; }

        // Real constructor: snapshots all solution types, builds the pre-lowercased search index once,
        // then shows the full unfiltered list and focuses the filter box for immediate typing.
        public FindTypesDialog(SolutionModel solution) : this()
        {
            if (solution == null) throw new ArgumentNullException("solution");
            _solution = solution;
            _allTypes = solution.GetAllTypes().ToList();
            _index = new List<IndexEntry>(_allTypes.Count);
            foreach (TypeModel t in _allTypes)
            {
                _index.Add(new IndexEntry(
                    t,
                    (t.Name ?? string.Empty).ToLowerInvariant(),
                    (t.FullyQualifiedName ?? string.Empty).ToLowerInvariant(),
                    ComputeAcronym(t.Name)));
            }

            Refilter(string.Empty);
            _filterBox.Focus();
        }


        // Live filtering: re-rank on every keystroke in the filter box.
        private void OnFilterChanged(object sender, EventArgs e)
        {
            Refilter(_filterBox.Text);
        }

        // Keyboard flow while focus is in the filter box:
        //  - Down moves focus into the results list (keeping the current/first row selected and visible),
        //    so the user can type-then-arrow without touching the mouse.
        //  - Enter commits the current selection immediately.
        private void OnFilterKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                if (_results.Items.Count > 0)
                {
                    _results.Focus();
                    int idx = _results.SelectedIndices.Count > 0 ? _results.SelectedIndices[0] : 0;
                    _results.Items[Math.Min(idx, _results.Items.Count - 1)].Selected = true;
                    _results.Items[Math.Min(idx, _results.Items.Count - 1)].EnsureVisible();
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                AcceptSelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // Enter within the results list also commits (mirrors double-click / OK).
        private void OnResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                AcceptSelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // Recomputes the result list for the given query and repaints the grid.
        private void Refilter(string query)
        {
            _results.BeginUpdate();
            try
            {
                _results.Items.Clear();
                string q = (query ?? string.Empty).Trim();

                // Empty query short-circuits to the full type list (in its natural order).
                List<TypeModel> matched;
                if (string.IsNullOrEmpty(q))
                {
                    matched = _allTypes;
                }
                else
                {
                    // Rank every indexed type against the query, drop non-matches (rank < 0),
                    // then sort primarily by rank (best matches first) and secondarily by name.
                    string lower = q.ToLowerInvariant();
                    var ranked = new List<RankedType>();
                    foreach (IndexEntry e in _index)
                    {
                        int rank = RankMatch(e, lower);
                        if (rank >= 0) ranked.Add(new RankedType(e.Type, rank));
                    }
                    ranked.Sort((a, b) =>
                    {
                        int c = a.Rank.CompareTo(b.Rank);
                        return c != 0 ? c : string.Compare(a.Type.Name, b.Type.Name, StringComparison.OrdinalIgnoreCase);
                    });
                    matched = new List<TypeModel>(ranked.Count);
                    foreach (RankedType r in ranked) matched.Add(r.Type);
                }

                // Render at most MaxResults rows (Name / Kind / Namespace / Project); Tag holds the model.
                int total = matched.Count;
                int shown = Math.Min(total, MaxResults);
                for (int i = 0; i < shown; i++)
                {
                    TypeModel t = matched[i];
                    ListViewItem item = new ListViewItem(t.Name);
                    item.SubItems.Add(t.TypeKind.ToString());
                    item.SubItems.Add(t.Namespace ?? string.Empty);
                    item.SubItems.Add(t.Project != null ? t.Project.Name : string.Empty);
                    item.Tag = t;
                    _results.Items.Add(item);
                }

                // Pre-select the top-ranked row so Enter works without arrowing down first.
                if (_results.Items.Count > 0)
                {
                    _results.Items[0].Selected = true;
                }

                // Surface truncation so the user knows there are more matches than shown.
                _countLabel.Text = total > shown
                    ? string.Format("showing {0} of {1} — refine to narrow", shown, total)
                    : string.Format("{0} result{1}", total, total == 1 ? string.Empty : "s");
            }
            finally
            {
                _results.EndUpdate();
            }
        }

        /// <summary>
        /// Rank — 0 = exact-case-insensitive name match, 1 = name prefix match,
        /// 2 = name substring, 3 = FQN substring, 4 = acronym prefix, -1 = no match. Operates on the
        /// pre-lowercased <see cref="IndexEntry"/> to avoid per-keystroke allocations.
        /// </summary>
        private static int RankMatch(IndexEntry e, string lowerQuery)
        {
            string nLow = e.NameLower;
            if (nLow == lowerQuery) return 0;
            if (nLow.StartsWith(lowerQuery, StringComparison.Ordinal)) return 1;
            if (nLow.IndexOf(lowerQuery, StringComparison.Ordinal) >= 0) return 2;
            if (e.FqnLower.IndexOf(lowerQuery, StringComparison.Ordinal) >= 0) return 3;
            // Acronym quick-open: "spb" -> SolutionProjectionBuilder. Guarded to 2+ chars so a single
            // letter doesn't flood results with every type sharing that initial.
            if (lowerQuery.Length >= 2 && e.AcronymLower.StartsWith(lowerQuery, StringComparison.Ordinal)) return 4;
            return -1;
        }

        // A type paired with its match rank, used only for sorting inside Refilter.
        private struct RankedType
        {
            public readonly TypeModel Type;
            public readonly int Rank;
            public RankedType(TypeModel type, int rank) { Type = type; Rank = rank; }
        }

        // Precomputed search row: the type plus its name/FQN lowercased once at construction,
        // so live filtering avoids re-lowercasing on every keystroke.
        private sealed class IndexEntry
        {
            public readonly TypeModel Type;
            public readonly string NameLower;
            public readonly string FqnLower;
            public readonly string AcronymLower;   // initials of camelCase/PascalCase/snake_case words
            public IndexEntry(TypeModel type, string nameLower, string fqnLower, string acronymLower)
            {
                Type = type; NameLower = nameLower; FqnLower = fqnLower; AcronymLower = acronymLower;
            }
        }

        // Lowercased initials at word boundaries so "SPB" / "spb" finds "SolutionProjectionBuilder"
        // and "mc" finds "my_class" — the acronym quick-open that VS/VS Code offer. Computed once per
        // type at index time (never per keystroke).
        internal static string ComputeAcronym(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new System.Text.StringBuilder(8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c)) continue;
                bool boundary =
                    i == 0
                    || (char.IsUpper(c) && !char.IsUpper(name[i - 1]))          // camelCase hump
                    || !char.IsLetterOrDigit(name[i - 1]);                       // after _ . - etc.
                if (boundary) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // Commits the highlighted row: pulls its TypeModel out of Tag into SelectedType and closes
        // with OK. No-op if nothing is selected or the row somehow carries no model, leaving the
        // dialog open rather than returning a null result.
        private void AcceptSelection()
        {
            if (_results.SelectedItems.Count == 0) return;
            SelectedType = _results.SelectedItems[0].Tag as TypeModel;
            if (SelectedType != null)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }
}
