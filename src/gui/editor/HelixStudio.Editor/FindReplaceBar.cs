using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using HelixTextEditorNET;

namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// A compact in-editor Find / Replace bar that docks to the top of an
    /// <see cref="EditorTabPage"/> and drives a <see cref="Scintilla"/> control via its
    /// search-target API. Supports incremental highlight-all, next/previous with wrap,
    /// match-case and whole-word toggles, and replace / replace-all.
    ///
    /// The bar is hidden by default; <see cref="ShowFind"/> / <see cref="ShowReplace"/>
    /// reveal it (the replace row is collapsed in find-only mode). Escape hides it and
    /// returns focus to the editor.
    /// </summary>
    public sealed class FindReplaceBar : UserControl
    {
        // Indicator 0 is the error squiggle (see PythonEditorControl); use a distinct
        // index for search highlighting so the two never clobber each other.
        private const int SearchIndicator = 8;
        private const int FindRowHeight = 30;
        private const int ReplaceRowHeight = 30;

        private readonly Scintilla _editor;

        private readonly TextBox _findBox;
        private readonly TextBox _replaceBox;
        private readonly Label _countLabel;
        private readonly CheckBox _matchCase;
        private readonly CheckBox _wholeWord;
        private readonly CheckBox _regex;
        private readonly Button _next;
        private readonly Button _prev;
        private readonly Button _replaceOne;
        private readonly Button _replaceAll;
        private readonly Button _close;
        private readonly Label _replaceLabel;

        private bool _replaceMode;
        private bool _indicatorReady;
        private bool _regexError;   // last search had a malformed regex pattern
        private readonly List<int> _matchStarts = new List<int>();

        public FindReplaceBar(Scintilla editor)
        {
            if (editor == null) throw new ArgumentNullException("editor");
            _editor = editor;

            Dock = DockStyle.Top;
            BackColor = Color.FromArgb(238, 240, 243);
            Height = FindRowHeight;
            Visible = false;
            Font = SystemFonts.MessageBoxFont;

            _findBox = new TextBox { Location = new Point(8, 4), Width = 200 };
            _findBox.TextChanged += (s, e) => { HighlightAll(); };
            _findBox.KeyDown += OnFieldKeyDown;

            _prev = MakeButton("▲", 214, "Previous match (Shift+F3)");
            _prev.Click += (s, e) => FindNext(false);
            _next = MakeButton("▼", 246, "Next match (F3)");
            _next.Click += (s, e) => FindNext(true);

            _matchCase = new CheckBox
            {
                Text = "Aa",
                Location = new Point(282, 6),
                Width = 40,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _matchCase.ToolTipText("Match case");
            _matchCase.CheckedChanged += (s, e) => HighlightAll();

            _wholeWord = new CheckBox
            {
                Text = "␣W",
                Location = new Point(324, 6),
                Width = 44,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _wholeWord.ToolTipText("Match whole word");
            _wholeWord.CheckedChanged += (s, e) => HighlightAll();

            _regex = new CheckBox
            {
                Text = ".*",
                Location = new Point(370, 6),
                Width = 40,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _regex.ToolTipText("Use regular expression");
            // Whole-word doesn't apply to a regex, so grey it out while regex is on (mirrors Flags()).
            _regex.CheckedChanged += (s, e) => { _wholeWord.Enabled = !_regex.Checked; HighlightAll(); };

            _countLabel = new Label
            {
                Location = new Point(418, 8),
                Width = 110,
                ForeColor = Color.FromArgb(90, 90, 90),
                Text = string.Empty,
            };

            _close = MakeButton("✕", 0, "Close (Esc)");
            _close.Click += (s, e) => Hide();

            // Replace row (second line) — hidden in find-only mode.
            _replaceLabel = new Label
            {
                Text = "Replace",
                Location = new Point(8, FindRowHeight + 8),
                Width = 50,
                Visible = false,
            };
            _replaceBox = new TextBox
            {
                Location = new Point(8, FindRowHeight + 4),
                Width = 200,
                Visible = false,
            };
            _replaceBox.KeyDown += OnFieldKeyDown;
            _replaceOne = MakeButton("Replace", 214, "Replace current match");
            _replaceOne.Top = FindRowHeight + 4;
            _replaceOne.Width = 70;
            _replaceOne.Visible = false;
            _replaceOne.Click += (s, e) => ReplaceCurrent();
            _replaceAll = MakeButton("All", 288, "Replace all matches");
            _replaceAll.Top = FindRowHeight + 4;
            _replaceAll.Width = 50;
            _replaceAll.Visible = false;
            _replaceAll.Click += (s, e) => ReplaceAll();

            Controls.Add(_findBox);
            Controls.Add(_prev);
            Controls.Add(_next);
            Controls.Add(_matchCase);
            Controls.Add(_wholeWord);
            Controls.Add(_regex);
            Controls.Add(_countLabel);
            Controls.Add(_close);
            Controls.Add(_replaceLabel);
            Controls.Add(_replaceBox);
            Controls.Add(_replaceOne);
            Controls.Add(_replaceAll);

            Resize += (s, e) => { _close.Left = Width - _close.Width - 6; };
        }

        // ----------------------------------------------------------------- public API

        public void ShowFind()
        {
            _replaceMode = false;
            ApplyMode();
            Reveal();
        }

        public void ShowReplace()
        {
            _replaceMode = true;
            ApplyMode();
            Reveal();
        }

        public new void Hide()
        {
            Visible = false;
            ClearHighlights();
            try { _editor.Focus(); } catch { }
        }

        public void FindNextExternal(bool forward)
        {
            if (!Visible) ShowFind();
            FindNext(forward);
        }

        // ----------------------------------------------------------------- internals

        private void ApplyMode()
        {
            _replaceLabel.Visible = _replaceMode;
            _replaceBox.Visible = _replaceMode;
            _replaceOne.Visible = _replaceMode;
            _replaceAll.Visible = _replaceMode;
            Height = _replaceMode ? FindRowHeight + ReplaceRowHeight : FindRowHeight;
        }

        private void Reveal()
        {
            Visible = true;
            // Seed the find box with the current selection when it is a single line.
            string sel = _editor.SelectedText;
            if (!string.IsNullOrEmpty(sel) && sel.IndexOf('\n') < 0)
                _findBox.Text = sel;
            HighlightAll();
            _findBox.Focus();
            _findBox.SelectAll();
        }

        private void OnFieldKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                FindNext(!e.Shift);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private SearchFlags Flags()
        {
            SearchFlags f = SearchFlags.None;
            if (_matchCase.Checked) f |= SearchFlags.MatchCase;
            // Whole-word has no meaning for a regex (and Scintilla rejects the combo), so it is
            // mutually exclusive with the regex toggle.
            if (_regex.Checked) f |= SearchFlags.Regex;
            else if (_wholeWord.Checked) f |= SearchFlags.WholeWord;
            return f;
        }

        private void EnsureIndicator()
        {
            if (_indicatorReady) return;
            _editor.Indicators[SearchIndicator].Style = IndicatorStyle.StraightBox;
            _editor.Indicators[SearchIndicator].ForeColor = Color.FromArgb(255, 200, 60);
            _editor.Indicators[SearchIndicator].Under = true;
            _editor.Indicators[SearchIndicator].Alpha = 90;
            _editor.Indicators[SearchIndicator].OutlineAlpha = 160;
            _indicatorReady = true;
        }

        private void ClearHighlights()
        {
            if (!_indicatorReady) return;
            _editor.IndicatorCurrent = SearchIndicator;
            _editor.IndicatorClearRange(0, _editor.TextLength);
            _matchStarts.Clear();
        }

        private void HighlightAll()
        {
            EnsureIndicator();
            _editor.IndicatorCurrent = SearchIndicator;
            _editor.IndicatorClearRange(0, _editor.TextLength);
            _matchStarts.Clear();

            string term = _findBox.Text;
            if (string.IsNullOrEmpty(term)) { UpdateCount(); return; }

            int len = _editor.TextLength;
            _editor.SearchFlags = Flags();
            _editor.TargetStart = 0;
            _editor.TargetEnd = len;

            // An incomplete or malformed regex (a lone `(` typed while building `(foo)`) makes
            // SearchInTarget throw; without this the editor crashed on a keystroke. Treat any search
            // fault as "invalid pattern" — no matches, distinct feedback — instead of propagating.
            _regexError = false;
            try
            {
                while (true)
                {
                    int pos = _editor.SearchInTarget(term);
                    if (pos < 0) break;
                    int matchLen = _editor.TargetEnd - _editor.TargetStart;
                    if (matchLen <= 0) break; // guard against zero-width matches
                    _editor.IndicatorFillRange(_editor.TargetStart, matchLen);
                    _matchStarts.Add(_editor.TargetStart);

                    int nextStart = _editor.TargetEnd;
                    if (nextStart >= len) break;
                    _editor.TargetStart = nextStart;
                    _editor.TargetEnd = len;
                }
            }
            catch
            {
                _regexError = true;
                _matchStarts.Clear();
            }
            UpdateCount();
        }

        private void FindNext(bool forward)
        {
            string term = _findBox.Text;
            if (string.IsNullOrEmpty(term)) { UpdateCount(); return; }

            int len = _editor.TextLength;
            _editor.SearchFlags = Flags();

            int pos;
            try
            {
            if (forward)
            {
                _editor.TargetStart = _editor.SelectionEnd;
                _editor.TargetEnd = len;
                pos = _editor.SearchInTarget(term);
                if (pos < 0) // wrap to top
                {
                    _editor.TargetStart = 0;
                    _editor.TargetEnd = len;
                    pos = _editor.SearchInTarget(term);
                }
            }
            else
            {
                // Backward search: TargetStart > TargetEnd searches toward the start.
                _editor.TargetStart = _editor.SelectionStart;
                _editor.TargetEnd = 0;
                pos = _editor.SearchInTarget(term);
                if (pos < 0) // wrap to bottom
                {
                    _editor.TargetStart = len;
                    _editor.TargetEnd = 0;
                    pos = _editor.SearchInTarget(term);
                }
            }
            }
            catch { _regexError = true; UpdateCount(); return; }   // malformed regex — feedback, no crash

            if (pos < 0) { UpdateCount(); return; }

            int a = _editor.TargetStart;
            int b = _editor.TargetEnd;
            int matchStart = Math.Min(a, b);
            int matchEnd = Math.Max(a, b);
            _editor.SelectionStart = matchStart;
            _editor.SelectionEnd = matchEnd;
            _editor.ScrollCaret();
            UpdateCount();
        }

        private bool SelectionIsMatch(string term)
        {
            string sel = _editor.SelectedText;
            if (string.IsNullOrEmpty(sel) || sel.Length != term.Length) return false;
            StringComparison cmp = _matchCase.Checked
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            return string.Equals(sel, term, cmp);
        }

        private void ReplaceCurrent()
        {
            string term = _findBox.Text;
            if (string.IsNullOrEmpty(term)) return;

            if (SelectionIsMatch(term))
            {
                _editor.ReplaceSelection(_replaceBox.Text);
            }
            FindNext(true);
            HighlightAll();
        }

        private void ReplaceAll()
        {
            string term = _findBox.Text;
            if (string.IsNullOrEmpty(term)) return;

            string replacement = _replaceBox.Text ?? string.Empty;

            ClearHighlights();

            // Collect all match ranges first (searching does not modify the document) and
            // rebuild the whole document in a SINGLE assignment. Replacing in place with
            // one ReplaceTarget per match re-lexes and re-bookkeeps the document on every
            // edit — O(n) per edit, i.e. O(n^2) overall (measured ~82s for 50k matches).
            // Slicing via GetTextRange keeps all offsets in Scintilla's own position units,
            // so this stays correct for non-ASCII text too.
            int len = _editor.TextLength;
            _editor.SearchFlags = Flags();
            _editor.TargetStart = 0;
            _editor.TargetEnd = len;

            var sb = new StringBuilder(len + 16);
            int cursor = 0;
            int count = 0;
            while (true)
            {
                int pos = _editor.SearchInTarget(term);
                if (pos < 0) break;
                int mStart = _editor.TargetStart;
                int mEnd = _editor.TargetEnd;
                if (mEnd <= mStart) break; // zero-width guard

                if (mStart > cursor) sb.Append(_editor.GetTextRange(cursor, mStart - cursor));
                sb.Append(replacement);
                cursor = mEnd;
                count++;

                if (mEnd >= len) break;
                _editor.TargetStart = mEnd;
                _editor.TargetEnd = len;
            }

            if (count == 0) { _countLabel.Text = "No results"; return; }
            if (cursor < len) sb.Append(_editor.GetTextRange(cursor, len - cursor));

            _editor.BeginUndoAction();
            try { _editor.Text = sb.ToString(); }
            finally { _editor.EndUndoAction(); }

            HighlightAll();
            _countLabel.Text = count == 1 ? "1 replaced" : count + " replaced";
        }

        // The find box's normal / no-match backgrounds. VS Code tints the input when a non-empty
        // search finds nothing, so "No results" is felt without reading the counter.
        private static readonly Color FindBoxNormal = Color.White;
        private static readonly Color FindBoxNoMatch = Color.FromArgb(255, 220, 220);

        private void UpdateCount()
        {
            if (string.IsNullOrEmpty(_findBox.Text))
            {
                _countLabel.Text = string.Empty;
                _findBox.BackColor = FindBoxNormal;   // empty search is not a "no match"
                return;
            }
            if (_regexError)
            {
                _countLabel.Text = "Invalid pattern";
                _findBox.BackColor = FindBoxNoMatch;   // same tint as no-match — the search yields nothing
                return;
            }
            int total = _matchStarts.Count;
            if (total == 0)
            {
                _countLabel.Text = "No results";
                _findBox.BackColor = FindBoxNoMatch;  // tint the input so a miss is obvious
                return;
            }
            _findBox.BackColor = FindBoxNormal;

            int caret = _editor.SelectionStart;
            int idx = -1;
            for (int i = 0; i < _matchStarts.Count; i++)
            {
                if (_matchStarts[i] == caret) { idx = i; break; }
            }
            _countLabel.Text = idx >= 0
                ? string.Format("{0} of {1}", idx + 1, total)
                : string.Format("{0} matches", total);
        }

        private Button MakeButton(string text, int left, string tip)
        {
            Button b = new Button
            {
                Text = text,
                Location = new Point(left, 4),
                Width = 28,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 202, 206);
            if (!string.IsNullOrEmpty(tip)) b.ToolTipText(tip);
            return b;
        }
    }

    internal static class ToolTipExtensions
    {
        // Lightweight shared tooltip so individual controls can set hover text without
        // each owning a ToolTip component.
        private static readonly ToolTip Shared = new ToolTip();
        public static void ToolTipText(this Control c, string text)
        {
            Shared.SetToolTip(c, text);
        }
    }
}
