using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Terminal.Shell.Suggestions;

namespace HelixPythonNamespace.Terminal.App;

/// <summary>
/// The suggestion list under the caret, drawn after Visual Studio's: a glyph for what each item
/// is, the letters that matched in bold, a short description, and a side pane with more about the
/// highlighted one. It never takes focus — typing stays in the terminal, which moves the highlight
/// and accepts on its behalf.
/// </summary>
public sealed class SuggestionPopup : Form
{
    private const int VisibleRows = 8;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private Color _background;
    private Color _foreground;
    private Font? _glyphFont;
    private Font? _boldFont;
    private int _highlight;
    private int _top;
    private string _detail = string.Empty;
    private string? _detailFetchItem;

    public SuggestionPopup(Color background, Color foreground)
    {
        // Never drawn, since there is no border; it names the window for tools that list them.
        Text = "Suggestions";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9f);
        ApplyTheme(background, foreground);
    }

    public SuggestionSet? Set { get; private set; }

    public Suggestion? Highlighted => Set is { Items.Count: > 0 } set ? set.Items[_highlight] : null;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return parameters;
        }
    }

    public void ApplyTheme(Color background, Color foreground)
    {
        _background = Blend(background, foreground, 0.06f);
        _foreground = foreground;
        BackColor = _background;
        Invalidate();
    }

    private int Px(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private int RowHeight => Math.Max(Font.Height, Px(16)) + Px(6);

    private int ListWidth => Px(420);

    private int PaneWidth => Px(280);

    private Font GlyphFont => _glyphFont ??= new Font(
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets",
        Px(12), GraphicsUnit.Pixel);

    private Font BoldFont => _boldFont ??= new Font(Font, FontStyle.Bold);

    public void ShowFor(SuggestionSet set, Rectangle anchor)
    {
        var previous = Highlighted?.InsertText;
        Set = set;

        var index = previous is null ? -1 : set.Items.ToList().FindIndex(s => s.InsertText == previous);
        _highlight = Math.Max(0, index);
        _top = Math.Clamp(_top, 0, Math.Max(0, set.Items.Count - VisibleRows));
        EnsureHighlightVisible();
        UpdateDetail();

        var rows = Math.Min(VisibleRows, set.Items.Count);
        var size = new Size(ListWidth + PaneWidth, Math.Max(rows * RowHeight, Px(96)) + Px(2));
        var area = Screen.FromRectangle(anchor).WorkingArea;

        var x = Math.Clamp(anchor.Left - Px(24), area.Left, Math.Max(area.Left, area.Right - size.Width));
        var y = anchor.Bottom + size.Height <= area.Bottom ? anchor.Bottom : Math.Max(area.Top, anchor.Top - size.Height);

        Bounds = new Rectangle(new Point(x, y), size);

        if (!Visible) Show();
        Invalidate();
    }

    public void MoveHighlight(int delta)
    {
        if (Set is not { Items.Count: > 0 } set) return;

        _highlight = (_highlight + delta + set.Items.Count) % set.Items.Count;
        EnsureHighlightVisible();
        UpdateDetail();
        Invalidate();
    }

    public void PageHighlight(int direction)
    {
        if (Set is not { Items.Count: > 0 } set) return;

        _highlight = Math.Clamp(_highlight + direction * VisibleRows, 0, set.Items.Count - 1);
        EnsureHighlightVisible();
        UpdateDetail();
        Invalidate();
    }

    public void HideIfShown()
    {
        if (Visible) Hide();
        Set = null;
    }

    private void EnsureHighlightVisible()
    {
        if (_highlight < _top) _top = _highlight;
        if (_highlight >= _top + VisibleRows) _top = _highlight - VisibleRows + 1;
    }

    /// <summary>Files and folders are described only when highlighted: one disk look, not fifty. Fetches path descriptions on a background thread to avoid blocking the UI.</summary>
    private void UpdateDetail()
    {
        var item = Highlighted;

        if (item is null)
        {
            _detail = string.Empty;
            _detailFetchItem = null;
            return;
        }

        _detail = item.Detail;

        if (item.FullPath is null)
        {
            _detailFetchItem = null;
            return;
        }

        // Fetch the path description on a background thread to avoid blocking the UI.
        var fetchingItem = item.InsertText;
        _detailFetchItem = fetchingItem;
        var fullPath = item.FullPath;

        _ = Task.Run(() =>
        {
            string description;
            try
            {
                description = SuggestionEngine.DescribePath(fullPath);
            }
            catch (Exception)
            {
                description = string.Empty;
            }

            if (IsHandleCreated && !IsDisposed && _detailFetchItem == fetchingItem)
            {
                BeginInvoke(() =>
                {
                    if (_detailFetchItem == fetchingItem)
                    {
                        _detail = description;
                        Invalidate();
                    }
                });
            }
        });
    }

    // --------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(_background);

        using (var border = new Pen(Blend(_background, _foreground, 0.3f)))
            graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        if (Set is not { } set) return;

        for (var row = 0; row < VisibleRows && _top + row < set.Items.Count; row++)
            DrawItem(graphics, set.Items[_top + row], new Rectangle(1, 1 + row * RowHeight, ListWidth - 1, RowHeight), _top + row == _highlight);

        using (var divider = new Pen(Blend(_background, _foreground, 0.2f)))
            graphics.DrawLine(divider, ListWidth, 1, ListWidth, Height - 2);

        TextRenderer.DrawText(graphics, _detail, Font,
            new Rectangle(ListWidth + Px(10), Px(8), PaneWidth - Px(20), Height - Px(16)),
            Blend(_background, _foreground, 0.8f),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private void DrawItem(Graphics graphics, Suggestion item, Rectangle bounds, bool highlighted)
    {
        if (highlighted)
        {
            using var brush = new SolidBrush(Blend(_background, _foreground, 0.18f));
            graphics.FillRectangle(brush, bounds);
        }

        var glyphBounds = new Rectangle(bounds.X + Px(6), bounds.Y, Px(20), bounds.Height);
        TextRenderer.DrawText(graphics, Glyph(item.Kind), GlyphFont, glyphBounds, GlyphColour(item.Kind),
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

        // The name, one run per stretch of matched or unmatched letters, the matched ones bold.
        var x = glyphBounds.Right + Px(6);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var matched = item.MatchedPositions.ToHashSet();
        var text = item.DisplayText;
        var nameLimit = bounds.X + bounds.Width * 3 / 5;

        for (var i = 0; i < text.Length && x < nameLimit;)
        {
            var bold = matched.Contains(i);
            var end = i;
            while (end < text.Length && matched.Contains(end) == bold) end++;

            var run = text[i..end];
            var font = bold ? BoldFont : Font;
            var width = TextRenderer.MeasureText(graphics, run, font, Size.Empty, flags).Width;
            TextRenderer.DrawText(graphics, run, font, new Rectangle(x, bounds.Y, width + 1, bounds.Height), _foreground, flags);
            x += width;
            i = end;
        }

        var descriptionLeft = Math.Max(x + Px(12), bounds.X + bounds.Width / 2);
        TextRenderer.DrawText(graphics, item.Description, Font,
            new Rectangle(descriptionLeft, bounds.Y, bounds.Right - descriptionLeft - Px(6), bounds.Height),
            Blend(_background, _foreground, 0.55f), flags | TextFormatFlags.EndEllipsis);
    }

    private static string Glyph(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Command => "\uE756",
        SuggestionKind.Subcommand => "\uE76C",
        SuggestionKind.Option => "\uE713",
        SuggestionKind.Folder => "\uE8B7",
        SuggestionKind.File => "\uE8A5",
        SuggestionKind.Variable => "\uE943",
        SuggestionKind.Job => "\uE768",
        SuggestionKind.Branch => "\uE8AB",
        SuggestionKind.Remote => "\uE774",
        SuggestionKind.History => "\uE81C",
        _ => "\uE8FD",
    };

    private Color GlyphColour(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Command or SuggestionKind.Subcommand => Color.FromArgb(0xB1, 0x80, 0xD7),
        SuggestionKind.Option => Color.FromArgb(0x4F, 0xC1, 0xFF),
        SuggestionKind.Folder => Color.FromArgb(0xDC, 0xB6, 0x7A),
        SuggestionKind.Variable => Color.FromArgb(0x9C, 0xDC, 0xFE),
        SuggestionKind.Branch or SuggestionKind.Remote => Color.FromArgb(0x4E, 0xC9, 0xB0),
        _ => Blend(_background, _foreground, 0.7f),
    };

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R * (1 - amount) + b.R * amount),
        (int)(a.G * (1 - amount) + b.G * amount),
        (int)(a.B * (1 - amount) + b.B * amount));

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _glyphFont?.Dispose();
        _glyphFont = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _glyphFont?.Dispose();
            _boldFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}

