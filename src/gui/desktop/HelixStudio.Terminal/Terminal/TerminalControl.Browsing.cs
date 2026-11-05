using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Terminal.Shell.Builtins;

namespace HelixPythonNamespace.Terminal.App.Terminal;

/// <summary>
/// Browsing the names the last <c>ls</c> printed, in place. Arrow keys move a highlight over them
/// and Enter opens one; a click highlights and a double-click opens. A folder is <c>cd</c>'d into
/// and anything else opens in its program. Any other key ends browsing and does its usual job.
/// </summary>
public sealed partial class TerminalControl
{
    // Only the link id is kept, not the item itself: scrollback trimmed or the screen cleared
    // while at the prompt (a background job's output) can move or drop the cells a cached
    // ListingItem would still point at, and the item is re-collected fresh everywhere it is
    // needed instead of trusting a stale one. 0 means nothing is highlighted.
    private int _highlightId;
    private bool _browseDismissed;

    /// <summary>
    /// The prompt is back, no child owns the console, nothing has been typed, and the suggestion
    /// list isn't sitting over the listing.
    /// </summary>
    private bool CanBrowse => _editing && !RawInputMode && _edit.Length == 0 && _popup is not { Visible: true };

    private List<ListingItem> ListingItems()
    {
        lock (_buffer.SyncRoot) return ListingNavigator.Collect(_buffer);
    }

    /// <summary>The highlighted item among <paramref name="items"/>, or null once it is gone.</summary>
    private ListingItem? CurrentHighlight(IReadOnlyList<ListingItem> items) =>
        _highlightId == 0 ? null : items.FirstOrDefault(i => i.LinkId == _highlightId);

    /// <summary>
    /// A line was submitted: what it prints is the next listing. A link a program forgot to close
    /// is closed here, so it cannot tag the rest of the session.
    /// </summary>
    private void StartNewListing()
    {
        lock (_buffer.SyncRoot) _buffer.StartListing();

        _highlightId = 0;
        _browseDismissed = false;
    }

    /// <summary>True when the key was browsing's. Any other key ends browsing and is left to do its usual job.</summary>
    private bool HandleBrowsingKey(KeyEventArgs e)
    {
        if (!CanBrowse || _browseDismissed) return false;

        switch (e.KeyCode)
        {
            // Modifiers, lock and system keys, and paging the scrollback, leave browsing alone.
            case Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.PageUp or Keys.PageDown
                or Keys.Capital or Keys.NumLock or Keys.Scroll or Keys.LWin or Keys.RWin or Keys.Apps:
                return false;

            case Keys.Left or Keys.Right or Keys.Up or Keys.Down when e.Modifiers == Keys.None:
                var items = ListingItems();
                if (items.Count == 0) return false;   // nothing listed: arrows recall history

                var current = CurrentHighlight(items);

                // Up alone never starts browsing: with nothing highlighted yet it is still asking
                // for history, exactly as it always has. Down, Left and Right do start it.
                if (current is null && e.KeyCode == Keys.Up) return false;

                SetHighlight(ListingNavigator.Move(items, current, Direction(e.KeyCode)));
                return true;

            case Keys.Enter when e.Modifiers == Keys.None && _highlightId != 0:
                var openItem = CurrentHighlight(ListingItems());
                EndBrowsing();
                if (openItem is not null) Open(openItem);   // gone (stale): nothing to open
                return true;

            default:
                EndBrowsing();
                return false;
        }
    }

    private static BrowseDirection Direction(Keys key) => key switch
    {
        Keys.Left => BrowseDirection.Left,
        Keys.Right => BrowseDirection.Right,
        Keys.Up => BrowseDirection.Up,
        _ => BrowseDirection.Down,
    };

    /// <summary>Clears the highlight. Up and Down recall history again until the next listing.</summary>
    private void EndBrowsing()
    {
        _browseDismissed = true;
        if (_highlightId == 0) return;

        _highlightId = 0;
        Invalidate();
    }

    private void SetHighlight(ListingItem? item)
    {
        _highlightId = item?.LinkId ?? 0;

        if (item is not null)
        {
            int top, rows;
            lock (_buffer.SyncRoot)
            {
                top = _buffer.ViewTop;
                rows = _buffer.Rows;
            }

            if (item.Row < top || item.Row >= top + rows) ScrollTo(item.Row - rows / 2);
        }

        Invalidate();
    }

    /// <summary>
    /// A folder is <c>cd</c>'d into; anything else opens in whatever Windows opens it with.
    /// Nothing opens unless it shows its own file name and its path is not remote: an OSC 8
    /// link's text is free, so a program's output could otherwise show "notes.txt" while linking
    /// elsewhere, and a network share is not something this can safely hand to <c>cd</c> or
    /// <see cref="Process.Start(ProcessStartInfo)"/> sight unseen.
    /// </summary>
    private void Open(ListingItem item)
    {
        bool canOpen;
        lock (_buffer.SyncRoot) canOpen = ListingNavigator.CanOpen(_buffer, item);
        if (!canOpen) return;

        if (Directory.Exists(item.Path))
        {
            SubmitLine("cd " + TerminalLinks.QuoteForShell(item.Path));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // The file has gone, or nothing is associated with it. Either way there is nothing to open.
        }
    }

    /// <summary>A plain click (no drag) on a name of the current listing highlights it.</summary>
    private bool ClickListing(Point location)
    {
        if (!CanBrowse || ItemAt(location) is not { } item) return false;

        _browseDismissed = false;
        ClearSelection();
        SetHighlight(item);
        return true;
    }

    /// <summary>A double-click on a name of the current listing opens it. False leaves it to word selection.</summary>
    private bool DoubleClickListing(Point location)
    {
        if (!CanBrowse || ItemAt(location) is not { } item) return false;

        EndBrowsing();
        Open(item);
        return true;
    }

    private ListingItem? ItemAt(Point location)
    {
        int row;
        lock (_buffer.SyncRoot) row = _buffer.ViewTop + (int)(location.Y / _cellSize.Height);

        var column = (int)(location.X / _cellSize.Width);
        return ListingNavigator.At(ListingItems(), row, column);
    }

    // The highlighted item's cells for the paint under way, refreshed once per pass rather than
    // looked up per cell. A cached ListingItem would go stale the same way _highlightId would if
    // kept as one; collecting fresh every paint means a link the scrollback has since dropped just
    // stops being highlighted instead of Marked reading past the end of a trimmed line.
    private IReadOnlyList<(int Row, int Column)>? _paintHighlightCells;

    /// <summary>Refreshes <see cref="_paintHighlightCells"/> for one paint. The caller holds the buffer's lock.</summary>
    private void RefreshHighlightForPaint(TerminalBuffer buffer)
    {
        _paintHighlightCells = CanBrowse && _highlightId != 0
            ? ListingNavigator.Collect(buffer).FirstOrDefault(i => i.LinkId == _highlightId)?.Cells
            : null;
    }

    /// <summary>Drawn in inverse video: part of the text selection, or of the highlighted name.</summary>
    private bool Marked(int row, int column, int top, int topColumn, int bottom, int bottomColumn) =>
        IsSelected(row, column, top, topColumn, bottom, bottomColumn) ||
        _paintHighlightCells?.Contains((row, column)) == true;
}
