using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HelixTextEditorNET
{
    // The autocompletion dropdown: a borderless, non-activating tool window with
    // an owner-draw list. The editor keeps keyboard focus and routes keys here.
    internal sealed class AutoCListWindow : Form
    {
        private readonly ListBox _list;
        private readonly Dictionary<int, Bitmap> _images;
        private Font _ownedFont;
        private List<string> _allItems = new List<string>();     // display text
        private List<int> _allTypes = new List<int>();           // image type per item
        private readonly List<int> _filtered = new List<int>();  // indices into _allItems

        public AutoCListWindow(Dictionary<int, Bitmap> images)
        {
            _images = images;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // Deliberately NOT TopMost: that is a global, cross-application flag (WS_EX_TOPMOST)
            // that floats the popup above every other window on the desktop, including unrelated
            // apps. An owned window (see Scintilla.Input.cs, set before Show()) already renders
            // above its owner automatically without leaking outside this app.
            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
                ItemHeight = 18
            };
            _list.DrawItem += OnDrawItem;
            _list.MouseDoubleClick += delegate { RaiseCommit(); };
            // Fires on ANY change to the highlighted row — arrow/PageUp/PageDown navigation,
            // mouse hover-select, and Filter()'s own re-selection — not just on commit. Without
            // this, the side doc panel only ever refreshed on the item Scintilla auto-selected
            // when the popup first opened; arrowing through the list visibly moved the highlight
            // but the doc panel silently kept showing the first item forever.
            _list.SelectedIndexChanged += delegate { RaiseSelectionChanged(); };
            Controls.Add(_list);
        }

        /// <summary>Fired whenever the highlighted item changes (not just on commit).</summary>
        public event EventHandler SelectionChanged;

        private void RaiseSelectionChanged()
        {
            var handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
                cp.ExStyle = HelixPythonNamespace.Core.TestEnvironment.ExStyle(cp.Style, cp.ExStyle);   // test runs only
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HelixPythonNamespace.Core.TestEnvironment.HandleCreated(Handle, CreateParams.Style);   // alpha 0 in a test run
        }

        // Fired when the user commits a selection (Enter/Tab/double-click/fillup).
        public event EventHandler CommitRequested;

        private void RaiseCommit()
        {
            var handler = CommitRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public string SelectedItemText
        {
            get
            {
                int li = _list.SelectedIndex;
                if (li < 0 || li >= _filtered.Count) return null;
                return _allItems[_filtered[li]];
            }
        }

        public int ItemCount
        {
            get { return _filtered.Count; }
        }

        // Populate from a Scintilla item list ("word?type word?type ...").
        public void SetItems(string list, char separator, char typeSeparator, bool performSort)
        {
            _allItems = new List<string>();
            _allTypes = new List<int>();
            if (!string.IsNullOrEmpty(list))
            {
                foreach (string raw in list.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string item = raw;
                    int type = -1;
                    int q = raw.LastIndexOf(typeSeparator);
                    if (q >= 0)
                    {
                        int parsed;
                        if (int.TryParse(raw.Substring(q + 1), out parsed))
                        {
                            type = parsed;
                            item = raw.Substring(0, q);
                        }
                    }
                    _allItems.Add(item);
                    _allTypes.Add(type);
                }
            }
            if (performSort)
            {
                // Sort items and types together, case-insensitively like SciLexer's
                // PerformSort.
                var order = new List<int>();
                for (int i = 0; i < _allItems.Count; i++) order.Add(i);
                var items = _allItems;
                order.Sort(delegate (int a, int b)
                {
                    return string.Compare(items[a], items[b], StringComparison.OrdinalIgnoreCase);
                });
                var sortedItems = new List<string>(items.Count);
                var sortedTypes = new List<int>(items.Count);
                foreach (int i in order)
                {
                    sortedItems.Add(_allItems[i]);
                    sortedTypes.Add(_allTypes[i]);
                }
                _allItems = sortedItems;
                _allTypes = sortedTypes;
            }
        }

        // Re-filter with the same prefix-or-subsequence semantics as the completion ranker.
        // The ranker can deliberately offer fuzzy matches (e.g. "0" -> "...6003..."); a strict
        // StartsWith-only filter here would discard every ranked result and dismiss the popup.
        public bool Filter(string prefix, bool ignoreCase)
        {
            _filtered.Clear();
            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            for (int i = 0; i < _allItems.Count; i++)
            {
                if (MatchesPrefixOrSubsequence(_allItems[i], prefix, cmp))
                    _filtered.Add(i);
            }
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (int i in _filtered) _list.Items.Add(_allItems[i]);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.EndUpdate();
            return _filtered.Count > 0;
        }

        private static bool MatchesPrefixOrSubsequence(string item, string prefix, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(prefix) || item.StartsWith(prefix, comparison)) return true;

            // Match the completion engine's fuzzy tier, which is case-insensitive even when the
            // editor's exact-prefix filtering option is off.
            int prefixIndex = 0;
            for (int i = 0; i < item.Length && prefixIndex < prefix.Length; i++)
            {
                if (char.ToLowerInvariant(item[i]) == char.ToLowerInvariant(prefix[prefixIndex]))
                    prefixIndex++;
            }
            return prefixIndex == prefix.Length;
        }

        public void SizeToContent(int maxHeightRows, int maxWidthChars, Font font)
        {
            int rows = Math.Min(Math.Max(1, _filtered.Count), Math.Max(1, maxHeightRows));
            int width = 120;
            using (Graphics g = CreateGraphics())
            {
                foreach (int i in _filtered)
                {
                    Size sz = TextRenderer.MeasureText(g, _allItems[i], font);
                    width = Math.Max(width, sz.Width + 40);
                }
            }
            if (maxWidthChars > 0)
            {
                int maxPx = maxWidthChars * TextRenderer.MeasureText("M", font).Width;
                width = Math.Min(width, maxPx);
            }
            // GdiSurface owns and may dispose the supplied font whenever editor styles are reset.
            // The popup outlives that reset, so retaining the borrowed instance makes the next
            // owner-draw call fail in Font.Height with "Parameter is not valid". Keep an
            // independent font for exactly the popup's lifetime.
            // Refiltering calls this for every typed character. Do not replace an equivalent font:
            // WM_DRAWITEM may already be queued with the ListBox's previous Font in e.Font, and
            // disposing that instance here makes the later draw crash. A real style change gets a
            // new clone; the old one stays alive until the popup is disposed.
            if (_ownedFont == null || !SameFont(_ownedFont, font))
            {
                if (_ownedFont != null) _retiredFonts.Add(_ownedFont);
                _ownedFont = (Font)font.Clone();
                _list.Font = _ownedFont;
            }
            _list.ItemHeight = Math.Max(16, _ownedFont.Height + 2);
            Size = new Size(width, rows * _list.ItemHeight + 4);
        }

        private static bool SameFont(Font a, Font b)
        {
            return a != null && b != null &&
                   string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&
                   a.Size == b.Size && a.Style == b.Style && a.Unit == b.Unit &&
                   a.GdiCharSet == b.GdiCharSet && a.GdiVerticalFont == b.GdiVerticalFont;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _ownedFont != null)
            {
                _ownedFont.Dispose();
                _ownedFont = null;
            }
            if (disposing)
            {
                foreach (Font font in _retiredFonts) font.Dispose();
                _retiredFonts.Clear();
            }
        }

        // Keyboard routed from the editor. Returns true when consumed.
        public bool HandleKey(Keys key)
        {
            switch (key)
            {
                case Keys.Down:
                    if (_list.SelectedIndex < _list.Items.Count - 1) _list.SelectedIndex++;
                    return true;
                case Keys.Up:
                    if (_list.SelectedIndex > 0) _list.SelectedIndex--;
                    return true;
                case Keys.PageDown:
                    _list.SelectedIndex = Math.Min(_list.Items.Count - 1,
                                                   _list.SelectedIndex + Math.Max(1, VisibleRows() - 1));
                    return true;
                case Keys.PageUp:
                    _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - Math.Max(1, VisibleRows() - 1));
                    return true;
                case Keys.Enter:
                case Keys.Tab:
                    RaiseCommit();
                    return true;
                default:
                    return false;
            }
        }

        private int VisibleRows()
        {
            return Math.Max(1, _list.ClientSize.Height / Math.Max(1, _list.ItemHeight));
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _filtered.Count) return;
            int itemIdx = _filtered[e.Index];
            int x = e.Bounds.Left + 2;
            int type = _allTypes[itemIdx];
            Bitmap image;
            if (type >= 0 && _images.TryGetValue(type, out image) && image != null)
            {
                int iy = e.Bounds.Top + (e.Bounds.Height - image.Height) / 2;
                e.Graphics.DrawImage(image, x, Math.Max(e.Bounds.Top, iy));
            }
            x += 20;
            Color fore = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : SystemColors.WindowText;
            // e.Font is captured by WinForms when WM_DRAWITEM is queued and may refer to a font
            // replaced since then. The popup-owned current font is the stable rendering authority.
            Font drawFont = _ownedFont ?? e.Font ?? SystemFonts.DefaultFont;
            TextRenderer.DrawText(e.Graphics, _allItems[itemIdx], drawFont,
                                  new Point(x, e.Bounds.Top + (e.Bounds.Height - drawFont.Height) / 2), fore,
                                  TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        private readonly List<Font> _retiredFonts = new List<Font>();
    }
}
