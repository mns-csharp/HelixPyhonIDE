namespace HelixPythonNamespace.Terminal.App.Terminal;

public enum BrowseDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>One name in a listing: its link, the path it leads to, the cell it starts on, and every cell it covers.</summary>
public sealed record ListingItem(int LinkId, string Path, int Row, int Column, IReadOnlyList<(int Row, int Column)> Cells)
{
    public bool Covers(int row, int column) => Cells.Contains((row, column));
}

/// <summary>
/// The names of the current listing, read off the screen, and where an arrow key goes from one of
/// them. It knows nothing of windows or keys, so it can be tested on a buffer alone.
/// </summary>
public static class ListingNavigator
{
    /// <summary>
    /// Every run of cells sharing a link, from <see cref="TerminalBuffer.ListingStart"/> to the end,
    /// in buffer order. A run carries on to the next row only when the row wrapped. Links that lead
    /// to no local path are left out. The caller holds the buffer's lock.
    /// </summary>
    public static List<ListingItem> Collect(TerminalBuffer buffer)
    {
        var items = new List<ListingItem>();
        List<(int Row, int Column)>? cells = null;
        int id = 0, lastRow = -1, lastColumn = -1;

        void Finish()
        {
            if (cells is not null && buffer.LinkPath(id) is { } path)
                items.Add(new ListingItem(id, path, cells[0].Row, cells[0].Column, cells));

            cells = null;
        }

        for (var row = Math.Max(0, buffer.ListingStart); row < buffer.Lines.Count; row++)
        {
            var line = buffer.Lines[row];

            for (var column = 0; column < line.Cells.Count; column++)
            {
                var cellId = line.Cells[column].LinkId;
                if (cellId == 0) { Finish(); continue; }

                var continues = cells is not null && cellId == id &&
                    ((row == lastRow && column == lastColumn + 1) ||
                     (row == lastRow + 1 && column == 0 && buffer.Lines[lastRow].Wrapped));

                if (!continues)
                {
                    Finish();
                    cells = new List<(int Row, int Column)>();
                    id = cellId;
                }

                cells!.Add((row, column));
                lastRow = row;
                lastColumn = column;
            }

            if (!line.Wrapped) Finish();
        }

        Finish();
        return items;
    }

    /// <summary>
    /// The item an arrow key moves to. With nothing highlighted yet, it is the first item. Left and
    /// Right stay on the row; Up and Down go to the nearest row that has items and pick the one
    /// starting closest to the current column. At an edge the current item is kept.
    /// </summary>
    public static ListingItem? Move(IReadOnlyList<ListingItem> items, ListingItem? current, BrowseDirection direction)
    {
        if (items.Count == 0) return null;
        if (current is null) return items[0];

        switch (direction)
        {
            case BrowseDirection.Left:
                return items.Where(i => i.Row == current.Row && i.Column < current.Column).MaxBy(i => i.Column) ?? current;

            case BrowseDirection.Right:
                return items.Where(i => i.Row == current.Row && i.Column > current.Column).MinBy(i => i.Column) ?? current;

            default:
                var up = direction == BrowseDirection.Up;
                var rows = items.Select(i => i.Row).Where(r => up ? r < current.Row : r > current.Row).ToList();
                if (rows.Count == 0) return current;

                var row = up ? rows.Max() : rows.Min();
                return items.Where(i => i.Row == row).MinBy(i => Math.Abs(i.Column - current.Column)) ?? current;
        }
    }

    /// <summary>The item covering a cell, for a mouse click.</summary>
    public static ListingItem? At(IReadOnlyList<ListingItem> items, int row, int column) =>
        items.FirstOrDefault(i => i.Covers(row, column));

    /// <summary>
    /// True when the text shown for <paramref name="item"/> is its own file name. An OSC 8 link's
    /// text is whatever the program chose to print, so without this check a program could show
    /// "notes.txt" while linking to some other path entirely — a share, an executable — and
    /// opening it would run that instead. False, rather than an exception, for a cell a scrollback
    /// trim or a screen clear has since moved out from under: the highlight it belonged to is
    /// stale and the caller is expected to drop it. The caller holds the buffer's lock.
    /// </summary>
    public static bool ShowsItsOwnName(TerminalBuffer buffer, ListingItem item)
    {
        var chars = new List<char>(item.Cells.Count);

        foreach (var (row, column) in item.Cells)
        {
            if (row < 0 || row >= buffer.Lines.Count) return false;

            var cells = buffer.Lines[row].Cells;
            if (column < 0 || column >= cells.Count) return false;

            var c = cells[column].Character;
            if (c != '\0') chars.Add(c);   // '\0' is the trailing half of a wide character
        }

        var shown = new string(chars.ToArray());

        // ls -F marks a folder with a trailing '/' and an executable with '*'.
        if (shown.Length > 0 && shown[^1] is '/' or '*') shown = shown[..^1];

        // A combining mark takes no cell of its own (TerminalBuffer.PutChar drops it), so the name
        // as shown never has one even when the file's real name does. Dropping it from the path's
        // side too is what keeps "Zoë.txt" matching what four cells actually spell out.
        var fileName = Path.GetFileName(item.Path.TrimEnd('\\'));
        var comparable = new string(fileName.Where(c => TerminalBuffer.CharacterWidth(c) != 0).ToArray());

        return string.Equals(shown, comparable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="item"/> is safe to open at all: it shows its own name, and its path
    /// is not a remote one. The caller holds the buffer's lock.
    /// </summary>
    // ponytail: refuses every UNC and \\?\ path outright rather than allow a tab its own share;
    // add that once opening from inside a network location is actually needed.
    public static bool CanOpen(TerminalBuffer buffer, ListingItem item) =>
        !item.Path.StartsWith(@"\\", StringComparison.Ordinal) && ShowsItsOwnName(buffer, item);
}
