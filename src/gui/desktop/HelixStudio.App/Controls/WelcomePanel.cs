namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// The start page, drawn in the Visual Studio 2022 idiom with GDI+ rather than assembled from
    /// stock WinForms controls: an accent-washed title band over two columns — recent solutions on
    /// the left, "Get started" action cards on the right — with hover feedback, keyboard navigation
    /// and product artwork for the action cards.
    ///
    /// The page is custom-painted so it reads as one designed surface instead of a form with
    /// buttons scattered across it. Everything clickable is painted and hit-tested from the same
    /// rectangles, so painting and hit-testing cannot disagree.
    /// </summary>
    public sealed class WelcomePanel : UserControl
    {
        private readonly RecentFilesManager _recentFiles;
        private readonly UserSettingsManager _settings;

        private CheckBox _noStartupCheck;
        private ToolTip _recentTip;
        private int _recentTipIndex = -1;

        private readonly List<string> _recent = new List<string>();
        private const string NoRecentText = "(no recent solutions)";
        private const int MaxShownRecent = 100;

        private VScrollBar _recentScroll;
        private int _firstVisibleRecent;   // index of the top visible row (scroll position)

        // Hit regions, rebuilt on every paint so painting and hit-testing cannot disagree.
        private readonly List<Rectangle> _cardBounds = new List<Rectangle>();
        private readonly List<Rectangle> _recentBounds = new List<Rectangle>();
        private int _hotCard = -1;
        private int _hotRecent = -1;
        private int _focusCard = -1;

        // The "Clear list" affordance in the recent column's heading row. Like every other hit
        // region it is rebuilt on each paint, and left empty when there is nothing to clear.
        private Rectangle _clearRecentBounds = Rectangle.Empty;
        private bool _hotClearRecent;
        private const string ClearRecentText = "Clear list";

        public event EventHandler NewSolutionClicked;
        public event EventHandler ImportDirectoryClicked;

        /// <summary>Create a notebook — no solution involved.</summary>
        public event EventHandler NewNotebookClicked;

        /// <summary>
        /// Open whatever the user picks: a notebook or a solution, sorted out by its name. One
        /// command, because the program can tell which it was handed and the user should not have
        /// to declare it before the dialog will show it to them.
        /// </summary>
        public event EventHandler OpenAnythingClicked;

        /// <summary>A standalone solution, never joining the open one (MainForm.OnFileNewSolution).</summary>
        public event EventHandler NewStandaloneSolutionClicked;

        private ContextMenuStrip _newMenu;
        public event EventHandler<string> OpenRecentClicked;

        /// <summary>Raised AFTER a solution was deleted from disk via the recent-row context
        /// menu (argument = the solution file path), so the host can close it if it is open.</summary>
        public event EventHandler<string> SolutionDeletedFromDisk;

        /// <summary>Raised AFTER the recent-solutions history was cleared from this page, so the
        /// host can rebuild its own File ▸ Recent Files menu from the now-empty list.</summary>
        public event EventHandler RecentsCleared;

        private const int PagePad = 28;
        private const int BaseHeaderHeight = 88;
        private const int BaseBannerHeight = 40;
        private const int CardHeight = 72;
        private const int CardGap = 10;
        private const int RowHeight = 38;
        private const int ColumnGap = 24;

        private sealed class ActionCard
        {
            public string Title;
            public string Description;
            public string Glyph;              // vector, drawn with GDI+ — no image assets
            public Action Invoke;
        }

        private readonly List<ActionCard> _cards = new List<ActionCard>();

        private static Image _bannerImage;
        private static bool _bannerLoadAttempted;
        private static readonly Dictionary<string, Image> ActionGlyphs = new Dictionary<string, Image>();

        /// <summary>Lazily loads and caches the "HelixPython IDE" wordmark banner for the header.
        /// Falls back to a plain text title (see DrawHeader) if the asset is missing, so a
        /// packaging mistake degrades the page instead of crashing it.
        ///
        /// <para>
        /// The file this replaced was named after, and read, "Helix Studio" -- the pre-split shell
        /// six products were carved out of. It was the largest piece of the old brand still on
        /// screen, and the one place a user met it before anything else: the Welcome page is the
        /// first thing an empty IDE shows.
        /// </para></summary>
        private static Image BannerImage
        {
            get
            {
                if (!_bannerLoadAttempted)
                {
                    _bannerLoadAttempted = true;
                    try { _bannerImage = ToolbarIconDiskLoader.Load("product\\banner", "helixpython-ide-banner.png"); }
                    catch { _bannerImage = null; }
                }
                return _bannerImage;
            }
        }

        public WelcomePanel(RecentFilesManager recentFiles, UserSettingsManager settings)
        {
            if (recentFiles == null) throw new ArgumentNullException("recentFiles");
            if (settings == null) throw new ArgumentNullException("settings");
            _recentFiles = recentFiles;
            _settings = settings;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(250, 250, 251);
            TabStop = true;

            // ONE New and ONE Open — one glyph each.
            //
            // Four cards showing two glyphs was the panel repeating itself: two plus-signs and two
            // folders, differing only in the noun underneath. A first screen should name the two
            // things a person does when they arrive, not enumerate the program's file types. New
            // asks WHAT afterwards, which is a second step and reads as one; Open does not even
            // need to ask, because a pyproject.toml and an .ipynb are told apart by their names.
            _cards.Add(new ActionCard
            {
                Title = "New...",
                Description = "A Jupyter notebook, a project, or a solution.",
                Glyph = "new",
                Invoke = ShowNewMenu,
            });
            _cards.Add(new ActionCard
            {
                Title = "Open...",
                Description = "Open a notebook or a solution — whichever you pick.",
                Glyph = "open",
                Invoke = () => Raise(OpenAnythingClicked),
            });
            _cards.Add(new ActionCard
            {
                Title = "Import a directory",
                Description = "Scan a folder of sources and generate the project files.",
                Glyph = "import",
                Invoke = () => Raise(ImportDirectoryClicked),
            });

            _recentTip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100 };

            _recentScroll = new VScrollBar { Visible = false, Width = SystemInformation.VerticalScrollBarWidth };
            _recentScroll.ValueChanged += delegate
            {
                _firstVisibleRecent = _recentScroll.Value;
                Invalidate();
            };
            Controls.Add(_recentScroll);

            _noStartupCheck = new CheckBox
            {
                Text = "Don't show this panel on startup",
                AutoSize = true,
                Font = LunaFonts.Ui,
                BackColor = Color.Transparent,
            };
            _noStartupCheck.Checked = !_settings.Settings.ShowWelcomeOnStartup;
            _noStartupCheck.CheckedChanged += delegate
            {
                _settings.Settings.ShowWelcomeOnStartup = !_noStartupCheck.Checked;
                try { _settings.Save(); } catch { /* a preference write must not break the page */ }
            };
            Controls.Add(_noStartupCheck);

            RefreshRecentFiles();
        }

        /// <summary>Re-populate the recent solutions list (call after any solution open/close).</summary>
        public void RefreshRecentFiles()
        {
            _recent.Clear();
            IReadOnlyList<string> files = _recentFiles.GetRecentFiles();
            if (files != null)
                for (int i = 0; i < files.Count && i < MaxShownRecent; i++) _recent.Add(files[i]);

            _hotRecent = -1;
            _recentTipIndex = -1;
            _firstVisibleRecent = 0;
            // Drop the button's hit region now rather than waiting for the repaint: on an emptied
            // list a stale rectangle would still swallow a click.
            _clearRecentBounds = Rectangle.Empty;
            _hotClearRecent = false;
            if (_recentTip != null) _recentTip.SetToolTip(this, string.Empty);
            UpdateRecentScrollbar();
            Invalidate();
        }

        private int VisibleRecentRows
        {
            get { return Math.Max(1, (LeftColumn.Height - 30) / RowHeight); }
        }

        // Show/size the scrollbar for the current list length and column height. Row units:
        // Value = index of the top visible row; the standard WinForms rule (max reachable
        // Value = Maximum - LargeChange + 1) makes Maximum = Count - 1 with LargeChange =
        // visible rows land exactly on "last page fully visible".
        private void UpdateRecentScrollbar()
        {
            if (_recentScroll == null) return;
            int visible = VisibleRecentRows;
            bool needed = _recent.Count > visible;
            if (!needed)
            {
                _recentScroll.Visible = false;
                _firstVisibleRecent = 0;
                return;
            }
            _recentScroll.Minimum = 0;
            _recentScroll.Maximum = _recent.Count - 1;
            _recentScroll.LargeChange = visible;
            _recentScroll.SmallChange = 1;
            int maxTop = Math.Max(0, _recent.Count - visible);
            if (_recentScroll.Value > maxTop) _recentScroll.Value = maxTop;
            _firstVisibleRecent = _recentScroll.Value;
            _recentScroll.Visible = true;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_recentScroll == null || !_recentScroll.Visible || !LeftColumn.Contains(e.Location)) return;
            int lines = Math.Max(1, SystemInformation.MouseWheelScrollLines);
            int delta = (e.Delta > 0 ? -lines : lines);
            int maxTop = Math.Max(0, _recent.Count - VisibleRecentRows);
            int newValue = Math.Max(0, Math.Min(maxTop, _recentScroll.Value + delta));
            if (newValue != _recentScroll.Value) _recentScroll.Value = newValue;
        }

        // ---- Layout ------------------------------------------------------------------------

        /// <summary>
        /// How tall the wordmark is drawn. It takes every pixel of page width it can get: the
        /// banner IS the page's title, and at the fixed 40px it used to be it read as a chip of
        /// decoration beside the subtitle rather than as the product's name.
        ///
        /// <para>
        /// Two ceilings, because only one of them binds at a time. Width is the usual one — the
        /// wordmark is about 3:1, so the page's own margins decide the height and the full name
        /// always fits. The quarter-of-the-page cap only bites on a window that is wide and short,
        /// where obeying width alone would hand most of a 400px-tall Welcome page to a logo and
        /// push the Open-recent and Get-started columns off the bottom.
        /// </para>
        /// </summary>
        private int BannerHeight
        {
            get
            {
                Image banner = BannerImage;
                if (banner == null || banner.Width <= 0 || banner.Height <= 0) return 0;

                int available = Math.Max(0, ClientSize.Width - ((PagePad + 16) * 2));
                int byWidth = (int)Math.Round(available * ((float)banner.Height / banner.Width));
                int cap = Math.Max(BaseBannerHeight, ClientSize.Height / 4);
                return Math.Max(BaseBannerHeight, Math.Min(byWidth, cap));
            }
        }

        /// <summary>
        /// The band is exactly the banner plus its own margins — <see cref="BaseHeaderHeight"/>
        /// applies only to the text fallback, which still carries a title line and a strapline
        /// under it. Derived rather than a constant plus a delta, so removing the strapline took
        /// its 24px of band with it instead of leaving a gap under the wordmark.
        /// </summary>
        private int HeaderHeight
        {
            get
            {
                int banner = BannerHeight;
                return banner > 0 ? banner + 24 : BaseHeaderHeight;
            }
        }

        private int ColumnsBottom
        {
            get { return Math.Max(PagePad + HeaderHeight + RowHeight, ClientSize.Height - 46); }
        }

        private Rectangle LeftColumn
        {
            get
            {
                int top = PagePad + HeaderHeight;
                int width = Math.Max(200, (int)((ClientSize.Width - (PagePad * 2) - ColumnGap) * 0.55f));
                return new Rectangle(PagePad, top, width, Math.Max(0, ColumnsBottom - top));
            }
        }

        private Rectangle RightColumn
        {
            get
            {
                Rectangle left = LeftColumn;
                int x = left.Right + ColumnGap;
                return new Rectangle(x, left.Top, Math.Max(200, ClientSize.Width - PagePad - x), left.Height);
            }
        }

        private bool _layingOut;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);

            // Moving/resizing a child re-enters OnLayout; without this guard the two SetBounds calls
            // below recurse until the stack overflows, which kills the process outright (an
            // uncatchable StackOverflowException, seen as a test-host crash).
            if (_layingOut) return;
            _layingOut = true;
            try
            {
                if (_noStartupCheck != null)
                    _noStartupCheck.Location = new Point(PagePad, Math.Max(0, ClientSize.Height - 28));
                if (_recentScroll != null)
                {
                    Rectangle col = LeftColumn;
                    _recentScroll.SetBounds(col.Right - _recentScroll.Width, col.Top + 30,
                        _recentScroll.Width, Math.Max(0, col.Height - 30));
                    UpdateRecentScrollbar();
                }
            }
            finally { _layingOut = false; }
        }

        // ---- Painting ----------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawHeader(g);
            DrawRecentColumn(g);
            DrawCardColumn(g);
        }

        private void DrawHeader(Graphics g)
        {
            Color accent = LunaThemes.Current.CaptionMid;

            var band = new Rectangle(0, 0, ClientSize.Width, PagePad + HeaderHeight - 16);
            if (band.Height > 0)
            {
                using (var wash = new LinearGradientBrush(band,
                           Blend(accent, Color.White, 0.90f), Color.FromArgb(250, 250, 251),
                           LinearGradientMode.Vertical))
                {
                    g.FillRectangle(wash, band);
                }
            }
            using (var rule = new Pen(Blend(accent, Color.White, 0.55f)))
            {
                g.DrawLine(rule, 0, band.Bottom, ClientSize.Width, band.Bottom);
            }

            Image banner = BannerImage;
            if (banner != null && banner.Width > 0 && banner.Height > 0)
            {
                int bannerHeight = BannerHeight;
                int bannerWidth = (int)Math.Round(bannerHeight * ((float)banner.Width / banner.Height));
                var bannerRect = new Rectangle(PagePad + 16, PagePad - 4, bannerWidth, bannerHeight);

                // The wordmark is a photographic-looking 3D render, so it is reduced the way a
                // photograph is. The default (low-quality bilinear) turned its gradients into
                // banding and left the helix's white edges crawling at the sizes this now draws at.
                InterpolationMode interpolation = g.InterpolationMode;
                PixelOffsetMode offset = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(banner, bannerRect);
                g.InterpolationMode = interpolation;
                g.PixelOffsetMode = offset;

                // No accent stripe and no strapline under the wordmark. Both existed to prop up a
                // 40px logo: the stripe gave it something to sit against, and the line of grey
                // text said what the product was because a chip that size could not. At this size
                // the wordmark does both jobs itself, and a rule beside it only fences it in.
            }
            else
            {
                // The text fallback still gets both. A bare string in the corner of an empty band
                // does need the anchor, and it is the one case where nothing on the page says
                // what HelixPython is.
                using (var stripe = new SolidBrush(accent))
                {
                    g.FillRectangle(stripe, PagePad, PagePad + 4, 4, 32);
                }
                using (var title = new Font(LunaFonts.Ui.FontFamily, LunaFonts.BaseSize + 8f, FontStyle.Bold))
                using (var ink = new SolidBrush(Color.FromArgb(28, 30, 34)))
                {
                    g.DrawString("HelixPython", title, ink, PagePad + 16, PagePad - 2);
                }
                using (var sub = new SolidBrush(Color.FromArgb(96, 100, 108)))
                {
                    g.DrawString(
                        "Visual OOP architecture designer & Python IDE  —  Python, with CUDA/C++ acceleration",
                        LunaFonts.Ui, sub, PagePad + 16, PagePad + 38);
                }
            }
        }

        private void DrawRecentColumn(Graphics g)
        {
            Rectangle col = LeftColumn;
            _recentBounds.Clear();
            DrawColumnHeading(g, col, "Open recent");
            DrawClearRecentButton(g, col);

            int y = col.Top + 30;
            if (_recent.Count == 0)
            {
                using (var grey = new SolidBrush(Color.FromArgb(130, 134, 142)))
                {
                    g.DrawString(NoRecentText, LunaFonts.Ui, grey, col.Left + 2, y + 6);
                    g.DrawString("Create or open a project and it will appear here.",
                        LunaFonts.Ui, grey, col.Left + 2, y + 26);
                }
                return;
            }

            // Rows are drawn from the scroll position; _recentBounds holds only the VISIBLE
            // rows, so hit-testing translates a bounds index to _recent via _firstVisibleRecent.
            int rowWidth = col.Width - ((_recentScroll != null && _recentScroll.Visible) ? _recentScroll.Width + 4 : 0);
            using (var nameFont = new Font(LunaFonts.Ui, FontStyle.Bold))
            using (var nameBrush = new SolidBrush(Color.FromArgb(20, 60, 140)))
            using (var pathBrush = new SolidBrush(Color.FromArgb(122, 126, 134)))
            {
                for (int i = _firstVisibleRecent; i < _recent.Count; i++)
                {
                    var row = new Rectangle(col.Left, y, rowWidth, RowHeight);
                    if (row.Bottom > col.Bottom) break;
                    _recentBounds.Add(row);

                    if (i - _firstVisibleRecent == _hotRecent)
                    {
                        using (var hot = new SolidBrush(Blend(LunaThemes.Current.CaptionMid, Color.White, 0.88f)))
                            g.FillRectangle(hot, row);
                    }

                    string path = _recent[i];
                    g.DrawString(SolutionDisplayName(path), nameFont, nameBrush, row.Left + 6, row.Top + 2);
                    g.DrawString(Ellipsize(g, path, LunaFonts.Ui, row.Width - 18),
                        LunaFonts.Ui, pathBrush, row.Left + 6, row.Top + 19);

                    y += RowHeight;
                }
            }
        }

        /// <summary>
        /// The "Clear list" button, drawn at the right end of the "Open recent" heading row —
        /// beside the thing it acts on, and above the rows so a mis-click lands on the button
        /// rather than on a solution. Offered only when there is history to clear, and dropped
        /// entirely if the column is too narrow to hold it without colliding with the heading.
        /// </summary>
        private void DrawClearRecentButton(Graphics g, Rectangle col)
        {
            _clearRecentBounds = Rectangle.Empty;
            if (_recent.Count == 0) return;

            SizeF text = g.MeasureString(ClearRecentText, LunaFonts.Ui);
            int width = (int)Math.Ceiling(text.Width) + 20;
            const int Height = 20;
            var box = new Rectangle(col.Right - width, col.Top - 2, width, Height);
            if (box.Left <= col.Left + 90) return;

            _clearRecentBounds = box;

            Color accent = LunaThemes.Current.CaptionMid;
            using (GraphicsPath path = RoundedRect(box, 4))
            using (var back = new SolidBrush(_hotClearRecent ? Blend(accent, Color.White, 0.88f) : Color.White))
            using (var pen = new Pen(_hotClearRecent ? accent : Color.FromArgb(222, 224, 228)))
            using (var ink = new SolidBrush(_hotClearRecent ? accent : Color.FromArgb(104, 108, 116)))
            {
                g.FillPath(back, path);
                g.DrawPath(pen, path);
                g.DrawString(ClearRecentText, LunaFonts.Ui, ink,
                    box.Left + 10, box.Top + Math.Max(0, (Height - text.Height) / 2f));
            }
        }

        private void DrawCardColumn(Graphics g)
        {
            Rectangle col = RightColumn;
            _cardBounds.Clear();
            DrawColumnHeading(g, col, "Get started");

            int y = col.Top + 30;
            for (int i = 0; i < _cards.Count; i++)
            {
                var card = new Rectangle(col.Left, y, col.Width, CardHeight);
                _cardBounds.Add(card);
                DrawCard(g, card, _cards[i], i == _hotCard, i == _focusCard);
                y += CardHeight + CardGap;
            }
        }

        private void DrawColumnHeading(Graphics g, Rectangle col, string text)
        {
            using (var head = new Font(LunaFonts.Ui, FontStyle.Bold))
            using (var ink = new SolidBrush(Color.FromArgb(48, 52, 60)))
            using (var rule = new Pen(Color.FromArgb(226, 228, 232)))
            {
                g.DrawString(text, head, ink, col.Left, col.Top);
                g.DrawLine(rule, col.Left, col.Top + 22, col.Right, col.Top + 22);
            }
        }

        private void DrawCard(Graphics g, Rectangle card, ActionCard model, bool hot, bool focused)
        {
            Color accent = LunaThemes.Current.CaptionMid;
            Color border = hot ? accent : Color.FromArgb(222, 224, 228);
            Color fill = hot ? Blend(accent, Color.White, 0.94f) : Color.White;

            using (GraphicsPath path = RoundedRect(card, 6))
            using (var back = new SolidBrush(fill))
            using (var pen = new Pen(border, hot ? 1.6f : 1f))
            {
                g.FillPath(back, path);
                g.DrawPath(pen, path);
            }

            var glyphBox = new Rectangle(card.Left + 8, card.Top + ((card.Height - 42) / 2), 42, 42);
            DrawGlyph(g, glyphBox, model.Glyph, accent);

            int textLeft = glyphBox.Right + 14;
            int textWidth = card.Right - textLeft - 14;
            using (var titleFont = new Font(LunaFonts.Ui, FontStyle.Bold))
            using (var titleInk = new SolidBrush(Color.FromArgb(24, 26, 30)))
            using (var descInk = new SolidBrush(Color.FromArgb(112, 116, 124)))
            {
                g.DrawString(model.Title, titleFont, titleInk, textLeft, card.Top + 13);
                g.DrawString(Ellipsize(g, model.Description, LunaFonts.Ui, textWidth),
                    LunaFonts.Ui, descInk, textLeft, card.Top + 34);
            }

            if (focused)
            {
                using (var dots = new Pen(Color.FromArgb(140, 144, 152)) { DashStyle = DashStyle.Dot })
                {
                    Rectangle r = card; r.Inflate(-3, -3);
                    g.DrawRectangle(dots, r);
                }
            }
        }

        private static void DrawGlyph(Graphics g, Rectangle box, string kind, Color accent)
        {
            Image icon = GetActionGlyph(kind);
            if (icon != null)
            {
                g.DrawImage(icon, box);
                return;
            }

            using (var pen = new Pen(accent, 1.8f))
            using (var soft = new SolidBrush(Blend(accent, Color.White, 0.86f)))
            {
                g.FillEllipse(soft, box);
                int cx = box.Left + (box.Width / 2);
                int cy = box.Top + (box.Height / 2);

                if (kind == "new")
                {
                    g.DrawLine(pen, cx - 7, cy, cx + 7, cy);
                    g.DrawLine(pen, cx, cy - 7, cx, cy + 7);
                }
                else if (kind == "open")
                {
                    var folder = new Rectangle(cx - 9, cy - 5, 18, 11);
                    g.DrawRectangle(pen, folder);
                    g.DrawLine(pen, cx - 9, cy - 5, cx - 4, cy - 9);
                    g.DrawLine(pen, cx - 4, cy - 9, cx + 1, cy - 5);
                }
                else
                {
                    g.DrawLine(pen, cx, cy - 8, cx, cy + 3);
                    g.DrawLine(pen, cx - 5, cy - 2, cx, cy + 3);
                    g.DrawLine(pen, cx + 5, cy - 2, cx, cy + 3);
                    g.DrawLine(pen, cx - 8, cy + 7, cx + 8, cy + 7);
                }
            }
        }

        private static Image GetActionGlyph(string kind)
        {
            Image image;
            if (ActionGlyphs.TryGetValue(kind, out image)) return image;

            string fileName = kind == "new" ? "new.png" : kind == "open" ? "open.png" : "import.png";
            try
            {
                image = ToolbarIconDiskLoader.Load("product\\welcome", fileName);
                ActionGlyphs.Add(kind, image);
                return image;
            }
            catch
            {
                ActionGlyphs.Add(kind, null);
                return null;
            }
        }

        // ---- Interaction --------------------------------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int card = IndexAt(_cardBounds, e.Location);
            int recent = IndexAt(_recentBounds, e.Location);
            bool clear = HitClearRecent(e.Location);

            if (card != _hotCard || recent != _hotRecent || clear != _hotClearRecent)
            {
                _hotCard = card;
                _hotRecent = recent;
                _hotClearRecent = clear;
                Cursor = (card >= 0 || recent >= 0 || clear) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            UpdateRecentTooltip(recent);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hotCard = -1;
            _hotRecent = -1;
            _hotClearRecent = false;
            Cursor = Cursors.Default;
            UpdateRecentTooltip(-1);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Focus();

            // Activation is LEFT-click only: MouseClick fires for every button, and the
            // right button belongs to the recent-row context menu (OnMouseUp) — without
            // this guard a right-click opened the solution AND showed the menu.
            if (e.Button != MouseButtons.Left) return;

            if (HitClearRecent(e.Location)) { ConfirmAndClearRecentList(); return; }

            int card = IndexAt(_cardBounds, e.Location);
            if (card >= 0) { _focusCard = card; Invalidate(); _cards[card].Invoke(); return; }

            int recent = RecentIndexAt(e.Location);
            if (recent >= 0) OpenRecent(_recent[recent]);
        }

        /// <summary>Translate a mouse position to an index into <see cref="_recent"/> (visible
        /// bounds index + scroll offset), or -1.</summary>
        private int RecentIndexAt(Point p)
        {
            int visibleIdx = IndexAt(_recentBounds, p);
            if (visibleIdx < 0) return -1;
            int actual = visibleIdx + _firstVisibleRecent;
            return actual < _recent.Count ? actual : -1;
        }

        private bool HitClearRecent(Point p)
        {
            return !_clearRecentBounds.IsEmpty && _clearRecentBounds.Contains(p);
        }

        // Nothing on disk is touched — this only empties the MRU list — but it is not undoable
        // and it discards every entry at once, so the UI path asks first and defaults to No.
        private void ConfirmAndClearRecentList()
        {
            if (_recent.Count == 0) return;

            DialogResult answer = MessageBox.Show(this,
                "Clear the list of recent solutions?\n\n" +
                _recent.Count + (_recent.Count == 1 ? " entry" : " entries") +
                " will be forgotten. Nothing is deleted from disk.",
                "Clear Recent Solutions", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            ClearRecentList();
        }

        /// <summary>Forget every recent solution and repaint, then announce it so the host can
        /// rebuild its File ▸ Recent Files menu. The dialog-free layer under the button.</summary>
        internal void ClearRecentList()
        {
            _recentFiles.Clear();
            RefreshRecentFiles();
            Raise(RecentsCleared);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Enter:
                case Keys.Space:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_cards.Count == 0) return;

            if (e.KeyCode == Keys.Down)
            {
                _focusCard = Math.Min(_cards.Count - 1, _focusCard + 1);
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                _focusCard = Math.Max(0, _focusCard - 1);
                Invalidate();
                e.Handled = true;
            }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && _focusCard >= 0)
            {
                _cards[_focusCard].Invoke();
                e.Handled = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;
            int recent = RecentIndexAt(e.Location);
            if (recent < 0) return;
            ShowRecentContextMenu(_recent[recent], e.Location);
        }

        // One REUSABLE strip, disposed only with the panel. Never dispose a ContextMenuStrip
        // from its own Closed event: ToolStripManager.ModalMenuFilter keeps a reference and
        // touches the strip again on the NEXT outside-click (CloseActiveDropDown →
        // CreateHandle on the disposed control) — crashed the whole app (crash-20260801-195854).
        private ContextMenuStrip _recentMenu;

        private void ShowRecentContextMenu(string path, Point location)
        {
            if (_recentMenu == null)
            {
                _recentMenu = new ContextMenuStrip();
                if (HelixPythonNamespace.Core.TestEnvironment.OffScreen)   // a test run: invisible (book 4.6.5)
                {
                    _recentMenu.Opacity = 0;
                    _recentMenu.DropShadowEnabled = false;
                }
            }
            _recentMenu.Items.Clear();
            _recentMenu.Items.Add("Open", null, delegate { OpenRecent(path); });
            _recentMenu.Items.Add("Remove from List", null, delegate
            {
                _recentFiles.RemoveRecent(path);
                RefreshRecentFiles();
            });
            _recentMenu.Items.Add(new ToolStripSeparator());
            _recentMenu.Items.Add("Delete from Disk…", null, delegate { ConfirmAndDeleteFromDisk(path); });
            _recentMenu.Show(this, location);
        }

        // Destructive and outward-facing: explicit confirmation naming the exact folder,
        // defaulting to No, and the folder goes to the RECYCLE BIN, never a hard delete.
        private void ConfirmAndDeleteFromDisk(string path)
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(path); } catch { }
            if (string.IsNullOrEmpty(dir)) return;

            DialogResult answer = MessageBox.Show(this,
                "Delete this solution from disk?\n\n" + dir + "\n\n" +
                "The whole folder — all of its projects and files — is moved to the Recycle Bin.",
                "Delete Solution", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            string error;
            if (!DeleteSolutionAndForget(path, toRecycleBin: true, error: out error) &&
                !string.IsNullOrEmpty(error))
            {
                MessageBox.Show(this, error, "Delete Solution", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Delete the solution's folder from disk (already-gone folders count as done —
        /// a stale recent entry), drop it from the recents list, and announce the deletion.</summary>
        internal bool DeleteSolutionAndForget(string path, bool toRecycleBin, out string error)
        {
            error = null;
            string dir = null;
            try { dir = Path.GetDirectoryName(path); } catch { }
            if (string.IsNullOrEmpty(dir))
            {
                error = "Not a valid solution path: " + path;
                return false;
            }

            if (Directory.Exists(dir) && !TryDeleteSolutionDirectory(path, toRecycleBin, out error))
                return false;

            _recentFiles.RemoveRecent(path);
            RefreshRecentFiles();
            EventHandler<string> handler = SolutionDeletedFromDisk;
            if (handler != null) handler(this, path);
            return true;
        }

        /// <summary>Deletes the DIRECTORY containing <paramref name="solutionFilePath"/> —
        /// recycle bin by default; permanent only for callers that ask (tests). Refuses drive roots.</summary>
        internal static bool TryDeleteSolutionDirectory(string solutionFilePath, bool toRecycleBin, out string error)
        {
            error = null;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(solutionFilePath));
                if (string.IsNullOrEmpty(dir))
                {
                    error = "Not a valid solution path: " + solutionFilePath;
                    return false;
                }
                string root = Path.GetPathRoot(dir) ?? string.Empty;
                if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar),
                                  root.TrimEnd(Path.DirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    error = "Refusing to delete a drive root: " + dir;
                    return false;
                }

                if (toRecycleBin)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Directory.Delete(dir, true);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not delete the solution folder: " + ex.Message;
                return false;
            }
        }

        // Only opens a path that still exists, so a stale recent entry cannot fail noisily.
        private void OpenRecent(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            EventHandler<string> handler = OpenRecentClicked;
            if (handler != null) handler(this, path);
        }

        /// <summary>
        /// Shows the hovered row's full path. Row text is ellipsized to the column width, so the
        /// tooltip is how a long path stays readable without widening the window. Only updates when
        /// the row changes, so the tip doesn't flicker on every mouse move.
        /// </summary>
        internal void UpdateRecentTooltip(int index)
        {
            if (_recentTip == null || index == _recentTipIndex) return;
            _recentTipIndex = index;

            int actual = index >= 0 ? index + _firstVisibleRecent : -1;
            string text = (actual >= 0 && actual < _recent.Count) ? _recent[actual] : null;
            _recentTip.SetToolTip(this, string.IsNullOrEmpty(text) ? string.Empty : text);
        }

        // ---- Helpers ------------------------------------------------------------------------

        private static int IndexAt(List<Rectangle> regions, Point p)
        {
            for (int i = 0; i < regions.Count; i++)
                if (regions[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>The name a user recognises: the solution's folder, not "pyproject.toml".</summary>
        internal static string SolutionDisplayName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    string leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(leaf)) return leaf;
                }
                return Path.GetFileName(path);
            }
            catch { return path; }
        }

        private static string Ellipsize(Graphics g, string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text ?? string.Empty;
            if (g.MeasureString(text, font).Width <= maxWidth) return text;

            const string Ellipsis = "…";
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (g.MeasureString(text.Substring(0, mid) + Ellipsis, font).Width <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, lo) + Ellipsis;
        }

        private static Color Blend(Color a, Color b, float towardsB)
        {
            towardsB = Math.Max(0f, Math.Min(1f, towardsB));
            return Color.FromArgb(
                (int)(a.R + ((b.R - a.R) * towardsB)),
                (int)(a.G + ((b.G - a.G) * towardsB)),
                (int)(a.B + ((b.B - a.B) * towardsB)));
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// The "What?" behind the single New card. Anchored to the card rather than to the cursor,
        /// so it appears in the same place whether the card was clicked or reached with the
        /// keyboard — a menu that lands under the pointer is a menu that moves for no reason.
        ///
        /// <para>
        /// Notebook first, because it is the only one of the three that asks nothing of the user
        /// beyond a filename.
        /// </para>
        /// </summary>
        internal void ShowNewMenu()
        {
            EnsureNewMenu();

            int index = _cards.FindIndex(c => c.Glyph == "new");
            Point at = (index >= 0 && index < _cardBounds.Count)
                ? new Point(_cardBounds[index].Left + 24, _cardBounds[index].Bottom - 6)
                : PointToClient(Cursor.Position);
            _newMenu.Show(this, at);
        }

        /// <summary>The items of the New menu, without showing it. Built on first use either way.</summary>
        internal ToolStripItemCollection NewMenuItems
        {
            get { EnsureNewMenu(); return _newMenu.Items; }
        }

        private void EnsureNewMenu()
        {
            if (_newMenu != null) return;
            _newMenu = new ContextMenuStrip();
            _newMenu.Items.Add("Jupyter notebook...", null, delegate { Raise(NewNotebookClicked); });
            _newMenu.Items.Add("Project...", null, delegate { Raise(NewSolutionClicked); });
            _newMenu.Items.Add("Solution...", null, delegate { Raise(NewStandaloneSolutionClicked); });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_recentTip != null) { _recentTip.Dispose(); _recentTip = null; }
                if (_recentMenu != null) { _recentMenu.Dispose(); _recentMenu = null; }
                if (_newMenu != null) { _newMenu.Dispose(); _newMenu = null; }
            }
            base.Dispose(disposing);
        }
    }
}
