using System.Drawing;
using System.Text;
using HelixPythonNamespace.Terminal.Shell.Builtins;

namespace HelixPythonNamespace.Terminal.App.Terminal;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Underline = 2,
    Inverse = 4,
    Dim = 8,
}

public struct TerminalCell
{
    public char Character;
    public Color Foreground;
    public Color Background;
    public CellFlags Flags;
    /// <summary>The OSC 8 link this cell was written inside, or 0. See <see cref="TerminalBuffer.LinkPath"/>.</summary>
    public int LinkId;

    public bool IsEmpty => Character == '\0' || Character == ' ';
}

public sealed class TerminalLine
{
    public List<TerminalCell> Cells { get; } = new();

    /// <summary>True when the line ran past the right margin and continues on the next one.</summary>
    public bool Wrapped { get; set; }

    public string Text
    {
        get
        {
            var sb = new StringBuilder(Cells.Count);
            foreach (var cell in Cells) sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
            return sb.ToString();
        }
    }
}

/// <summary>
/// The screen and its scrollback. Coordinates are absolute line indices into
/// <see cref="Lines"/>; the viewport is a window onto the tail of that list.
/// </summary>
public sealed class TerminalBuffer
{
    private readonly object _gate = new();

    // OSC 8 links: an id per link, tagged on every cell written while it is open. Ids for lines
    // trimmed from scrollback are not reclaimed; the table only holds short strings.
    private readonly Dictionary<int, string?> _links = new();
    private int _nextLinkId = 1;
    private int _openLink;

    public TerminalBuffer(int columns, int rows, int scrollbackLimit)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _childRows = Rows;
        ScrollbackLimit = Math.Max(Rows, scrollbackLimit);
        EnsureRoom();
    }

    public object SyncRoot => _gate;

    public List<TerminalLine> Lines { get; } = new();

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int ScrollbackLimit { get; set; }

    public int CursorRow { get; set; }

    public int CursorColumn { get; set; }

    /// <summary>
    /// Absolute line where the output of the last submitted line begins. Linked names from here to
    /// the end of the buffer are the listing that can be browsed; older ones are plain text.
    /// </summary>
    public int ListingStart { get; set; }

    /// <summary>Starts tagging cells with a new link to <paramref name="uri"/>.</summary>
    public void BeginLink(string uri)
    {
        _openLink = _nextLinkId++;
        _links[_openLink] = TerminalLinks.FromUri(uri);
    }

    /// <summary>Stops tagging cells. Harmless when no link is open.</summary>
    public void EndLink() => _openLink = 0;

    /// <summary>The local path a link leads to; null for an unknown id or a link that is not a file.</summary>
    public string? LinkPath(int id) => _links.TryGetValue(id, out var path) ? path : null;

    /// <summary>
    /// Starts a fresh listing: closes any link left open, drops every entry the link table holds
    /// so an old id can never alias whatever the next link happens to be, and marks this row as
    /// where the listing begins. Ids keep counting up regardless of the table being emptied, so a
    /// cell still tagged with a dropped id simply finds nothing rather than finding the wrong
    /// thing. The caller holds the buffer's lock.
    /// </summary>
    public void StartListing()
    {
        EndLink();
        _links.Clear();
        ListingStart = CursorRow;
    }

    public Color DefaultForeground { get; set; } = Color.Gainsboro;

    public Color DefaultBackground { get; set; } = Color.FromArgb(12, 12, 12);

    public Color CurrentForeground { get; set; } = Color.Gainsboro;

    public Color CurrentBackground { get; set; } = Color.Empty;

    public CellFlags CurrentFlags { get; set; }

    /// <summary>Index of the first visible line. Scrolling back moves this earlier.</summary>
    public int ViewTop { get; set; }

    public bool IsAtBottom => ViewTop >= MaxViewTop;

    public int MaxViewTop => Math.Max(_screenTop, Lines.Count - Rows);

    private int _screenTop;

    /// <summary>
    /// First line of the active screen. Programs address rows against this rather than against
    /// the scroll position, so reading back through the history while one is running does not
    /// drag what it draws along with it.
    ///
    /// It is a position of its own rather than "the last <see cref="Rows"/> lines", and the
    /// difference is the whole of what makes a cleared screen stay cleared. Derived from the end
    /// of the buffer, the screen moves whenever anything removes a line — and the line editor
    /// removes lines every time it redraws the input. A screen cleared to blank rows would fill
    /// itself straight back in with the history underneath as soon as the prompt was drawn on it.
    /// Held here, the screen only ever moves forward, when output runs off the bottom.
    /// </summary>
    public int ScreenTop => _screenTop;

    /// <summary>Last line of the active screen.</summary>
    public int ScreenBottom => _screenTop + Rows - 1;

    // ---------------------------------------------------------- child screen

    private int _childOrigin = -1;

    /// <summary>
    /// The line a program counts its rows from.
    ///
    /// A program on a pseudo-console does not write a stream; it renders a screen by address,
    /// placing the cursor with <c>CSI r;cH</c> and writing only what changed. It is handed a
    /// console it believes it opened on a blank screen, so its row 1 is the top of that screen —
    /// but what it is actually writing on is a screen with the session above it, so every row it
    /// addresses lands that many lines too high and it paints over the prompt and everything
    /// before it. Counting its rows from the line it started on is what puts the two in step:
    /// its row 1 is the line the command was run from, and the session above is left alone.
    ///
    /// It is still told the full height of the window, and it may address every row of it. The
    /// ones past the bottom of the screen simply scroll the screen forward as they are used, so a
    /// command that writes two lines writes them under the prompt, and one that paints a whole
    /// screen takes the whole screen and the session goes into the scrollback behind it.
    ///
    /// The one thing it must not do is move on its own. A pseudo-console repaints by address and
    /// writes only what changed, so the two sides have to agree on which line every row number
    /// names for as long as the program is running; an origin that slid out from under it left
    /// the tails of old lines on rows that were never rewritten. It was taken from
    /// <c>Math.Max(_screenTop, _childOrigin)</c>, which slid on the first row the program
    /// addressed below the bottom of the window — the screen moved forward to make room, the
    /// maximum followed it, and every row after that landed a line further down than the console
    /// believed. It now moves only when the program's own screen scrolls, which is the one time
    /// the console has moved too.
    /// </summary>
    public int AddressingTop => _childOrigin < 0 ? _screenTop : _childOrigin;

    private int _childRows;

    /// <summary>
    /// Rows in the console a program was given, which is not the same as rows in the window.
    ///
    /// A pseudo-console is a screen and nothing else: it holds the rows it was created with and
    /// no history behind them. What a program writes past the top of it is gone before the
    /// terminal is told anything — the console re-renders what is left as a repaint, and there is
    /// no scroll and no lost row in what arrives here. That is the whole of why a Claude Code
    /// session had no history to scroll back through while the same session under <c>cmd</c> has
    /// all of it: <c>cmd</c> has no pseudo-console, and the program's output goes straight into a
    /// console buffer thousands of lines deep.
    ///
    /// Measured: a reply of eighty lines, written in one burst into a console thirty-one rows
    /// tall, reached this terminal as a single repaint of the last twenty-four. The other
    /// fifty-six were never transmitted in any form. Handed a console ninety-three rows tall, the
    /// same reply arrived whole.
    ///
    /// So the console is given the rows the history needs, and the window rides the bottom of it.
    /// The rows above the window are the program's own, laid out by the program, and scrolling
    /// back through them is scrolling back through what it drew. Nothing is reconstructed and
    /// nothing is guessed at: it is the program's history because the program rendered it.
    /// </summary>
    public int ChildRows => _childOrigin < 0 ? Rows : _childRows;

    /// <summary>Last row a program may address, which is a full console height below its origin.</summary>
    public int AddressingBottom => AddressingTop + ChildRows - 1;

    /// <summary>True while a program on a pseudo-console owns the screen.</summary>
    public bool ChildOwnsScreen => _childOrigin >= 0;

    /// <summary>Hands the screen from <paramref name="row"/> down to a program.</summary>
    public void BeginChildScreen(int row, int consoleRows)
    {
        lock (_gate)
        {
            _childOrigin = Math.Max(_screenTop, row);
            _childRows = Math.Max(Rows, consoleRows);
        }
    }

    /// <summary>Takes it back, so rows are the window's own again.</summary>
    public void EndChildScreen()
    {
        lock (_gate)
        {
            _childOrigin = -1;
            _childRows = Rows;
        }
    }

    /// <summary>
    /// Re-sizes the console under a program that is still running, which is how the history it
    /// keeps is grown and trimmed. The console is resized on its side as well, and it repaints
    /// from scratch afterwards, so nothing here has to preserve what was on it.
    /// </summary>
    public void SetChildRows(int consoleRows)
    {
        lock (_gate)
        {
            if (_childOrigin < 0) return;

            var wanted = Math.Max(Rows, consoleRows);
            if (wanted == _childRows) return;

            var growing = wanted > _childRows;
            _childRows = wanted;

            // Growing leaves the window where it is. The rows that have just been added are blank
            // until the program has repainted into them, and moving the window down onto them now
            // would show a screen of nothing for as long as that takes; the program addresses its
            // bottom row every frame, so the window follows within one.
            if (growing) return;

            // Shrinking takes rows off the bottom, and they are the old frame's — the program is
            // about to lay the whole console out again from its origin, so anything past the new
            // bottom is stale and would otherwise be left below the window as rubbish to scroll
            // down into.
            var bottom = _childOrigin + _childRows;
            if (Lines.Count > bottom) Lines.RemoveRange(bottom, Lines.Count - bottom);

            _screenTop = Math.Max(0, Math.Min(_screenTop, bottom - Rows));
            CursorRow = Math.Clamp(CursorRow, _childOrigin, bottom - 1);
            EnsureRoom();
            ScrollToBottom();
        }
    }

    /// <summary>
    /// The longest run of rows a program has left untouched on its console.
    ///
    /// It is the one thing that says whether the console it was given is the right size. A program
    /// with more to show than fits leaves no gaps: what separates its messages is a row or two, so
    /// a long empty run means the console is taller than it has history to fill and the extra rows
    /// would be shown as a hole above its input line. Growing while this stays small and giving
    /// the rows back when it does not is what settles the console on the height of the program's
    /// own history, without anything here having to know what that history is.
    /// </summary>
    public int ChildScreenSlack()
    {
        lock (_gate)
        {
            if (_childOrigin < 0) return 0;

            var longest = 0;
            var run = 0;

            for (var i = AddressingTop; i <= AddressingBottom; i++)
            {
                run = IsBlank(i) ? run + 1 : 0;
                if (run > longest) longest = run;
            }

            return longest;
        }
    }

    private bool IsBlank(int index)
    {
        if (index >= Lines.Count) return true;

        var cells = Lines[index].Cells;
        for (var i = 0; i < cells.Count; i++)
            if (!cells[i].IsEmpty) return false;

        return true;
    }

    /// <summary>
    /// Where the screen goes once output has run past the bottom of it.
    ///
    /// The window shows the bottom of a program's console, and there is only one way to lay the
    /// two over each other that does. Until the program uses a row the window cannot show, it is
    /// writing a line or two under the prompt and the screen stays where it is — that is what
    /// keeps <c>git --version</c> under its own prompt instead of on a screen of its own. The
    /// moment it does, its console takes the window and the session above becomes scrollback.
    ///
    /// Letting the screen trail the cursor instead left a program hanging off the bottom of the
    /// window. The cursor comes to rest in whatever the program draws last — for Claude Code its
    /// input line, three rows above its own bottom — so the window settled three rows short of the
    /// console: the first lines of the session stayed pinned above it, and the last rows of the
    /// program, the input line among them, were below the window where they could not be seen.
    /// </summary>
    private int ChildScreenTop(int trailing) =>
        _childOrigin < 0 ? trailing : Math.Max(_screenTop, AddressingBottom - Rows + 1);

    // ------------------------------------------------ history a program drops
    //
    // There was a reconstruction here and it has been taken out. A program that repaints rather
    // than scrolls never says that anything moved, so the idea was to hold the last frame and,
    // when the next one turned out to be the same picture shifted up, keep the rows that fell
    // off the top. Measured against two real Claude Code sessions, the number of rows that line
    // up as a shifted copy of the previous frame never rose above 6 of 31: it does not scroll
    // its display, it lays the whole frame out again, and there is no shift to find. What the
    // detector did find were coincidental alignments, and the history it built from them came
    // out duplicated and in the wrong order — worse than an empty scrollback, because there is
    // no way to tell which of it to believe.
    //
    // The rows a program genuinely does let scroll still reach the scrollback, through the line
    // feed at the bottom of the screen in NewLine. That is the only signal there is.

    public void ScrollToBottom() => ViewTop = MaxViewTop;

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            Columns = Math.Max(1, columns);
            Rows = Math.Max(1, rows);
            ScrollbackLimit = Math.Max(ScrollbackLimit, Rows);

            // A region measured against the old height may not fit the new one.
            if (ScrollTop >= ChildRows || ScrollBottom >= ChildRows) ResetScrollRegion();

            if (UsingAlternateScreen)
            {
                // There is no scrollback to give an alternate screen, so it is trimmed from the
                // top and padded at the bottom to whatever height it now has.
                while (Lines.Count > Rows) Lines.RemoveAt(0);
                _screenTop = 0;
                _childRows = Rows;
            }
            else if (_childOrigin >= 0)
            {
                // A console is resized along with the window and lays its whole display out again
                // afterwards, at the new width — so the rows it had been given to keep history in
                // are handed back rather than kept. What is on them is the same history in the old
                // shape, and leaving it above the new layout puts every line of it on screen twice.
                // The program re-renders what it still has, and the console grows back as it does.
                if (Lines.Count > _childOrigin) Lines.RemoveRange(_childOrigin, Lines.Count - _childOrigin);

                _screenTop = _childOrigin;
                _childRows = Rows;
            }
            else
            {
                // A resize puts the screen back under the last line anybody has written. Counting
                // from the end of the buffer instead would count the blank rows the screen is
                // padded out with as content, and push that much of it into the scrollback: a
                // window that starts small and is laid out larger would open with a screen's worth
                // of blank lines above the first thing it printed.
                var bottom = Math.Max(CursorRow, LastWrittenLine());
                _screenTop = Math.Max(0, bottom - Rows + 1);
                _childRows = Rows;
            }

            CursorRow = Math.Clamp(CursorRow, _screenTop, ScreenBottom);
            CursorColumn = Math.Min(CursorColumn, Columns);
            EnsureRoom();
            TrimBlankTail();
            ScrollToBottom();
        }
    }

    /// <summary>Index of the last line anything was ever written to, or 0 when nothing has been.</summary>
    private int LastWrittenLine()
    {
        for (var i = Lines.Count - 1; i >= 0; i--)
            if (Lines[i].Cells.Count > 0) return i;

        return 0;
    }

    /// <summary>Drops padding past the bottom of the screen, so there is nothing below to scroll to.</summary>
    private void TrimBlankTail()
    {
        while (Lines.Count > _screenTop + Rows && Lines[^1].Cells.Count == 0) Lines.RemoveAt(Lines.Count - 1);
    }

    public TerminalLine LineAt(int index)
    {
        while (Lines.Count <= index) Lines.Add(new TerminalLine());
        return Lines[index];
    }

    private TerminalCell MakeCell(char c) => new()
    {
        Character = c,
        Foreground = CurrentForeground,
        Background = CurrentBackground,
        Flags = CurrentFlags,
        LinkId = _openLink,
    };

    /// <summary>
    /// An erased cell. It keeps the current background — that is what makes a cleared area of a
    /// coloured panel stay the colour of the panel — but carries none of the other attributes.
    /// </summary>
    private TerminalCell MakeBlank() => new()
    {
        Character = ' ',
        Foreground = DefaultForeground,
        Background = CurrentBackground,
        Flags = CellFlags.None,
    };

    /// <summary>Grows a line so that a cell at <paramref name="column"/> exists to be written.</summary>
    private void PadTo(TerminalLine line, int column)
    {
        while (line.Cells.Count < column) line.Cells.Add(MakeBlank());
    }

    /// <summary>
    /// Columns a character takes up. A program lays its screen out in cells and expects the
    /// terminal to agree: get this wrong and everything after it on the line sits one column out,
    /// which is how a space disappears between two words. Accents and other combining marks
    /// belong to the character before them and take no cell of their own; the CJK and fullwidth
    /// ranges take two.
    /// </summary>
    public static int CharacterWidth(char c)
    {
        if (c is '\0') return 0;

        if (c is (>= '̀' and <= 'ͯ')      // combining diacritics
            or (>= '​' and <= '‏')        // zero-width spaces and marks
            or (>= '⃐' and <= '⃿')        // combining marks for symbols
            or (>= '︀' and <= '️')        // variation selectors
            or '﻿')
            return 0;

        if (c is (>= 'ᄀ' and <= 'ᅟ')      // Hangul Jamo
            or (>= '⺀' and <= '〾')        // CJK radicals and punctuation
            or (>= 'ぁ' and <= '㏿')        // kana and CJK compatibility
            or (>= '㐀' and <= '䶿')        // CJK extension A
            or (>= '一' and <= '鿿')        // CJK unified ideographs
            or (>= 'ꀀ' and <= '꓏')        // Yi
            or (>= '가' and <= '힣')        // Hangul syllables
            or (>= '豈' and <= '﫿')        // CJK compatibility ideographs
            or (>= '︰' and <= '﹯')        // CJK compatibility forms
            or (>= '＀' and <= '｠')        // fullwidth forms
            or (>= '￠' and <= '￦'))
            return 2;

        return 1;
    }

    public void PutChar(char c)
    {
        var width = CharacterWidth(c);

        // A combining mark modifies the character already written; it gets no cell of its own,
        // and taking one would push the rest of the line out of step with the program's idea of it.
        if (width == 0) return;

        // Running past the right margin goes through the same line feed as a newline does, so a
        // wrap at the last row of a scroll region moves the region rather than growing the screen.
        if (CursorColumn + width > Columns)
        {
            LineAt(CursorRow).Wrapped = true;
            CarriageReturn();
            NewLine();
        }

        var line = LineAt(CursorRow);

        // Cells only being filled in to reach the cursor are blanks, not spaces in the colours
        // and attributes that happen to be set. A program repainting a line steps over the parts
        // of it that have not changed rather than rewriting them, and it does that in the middle
        // of whatever it was drawing with — so styling the gap paints bars of inverse video and
        // background colour across everything it skipped.
        while (line.Cells.Count <= CursorColumn) line.Cells.Add(MakeBlank());

        line.Cells[CursorColumn] = MakeCell(c);
        CursorColumn++;

        // The second column of a wide character is held by a cell of its own so that everything
        // after it lands where the program put it.
        if (width != 2) return;

        while (line.Cells.Count <= CursorColumn) line.Cells.Add(MakeCell(' '));
        line.Cells[CursorColumn] = MakeCell('\0');
        CursorColumn++;
    }

    public void NewLine()
    {
        // Inside a scroll region the bottom line is a floor: the region's own lines move up and
        // the line that falls off is discarded, rather than the screen growing and the top of it
        // sliding into the scrollback. An alternate screen is the same, all of it — it has no
        // scrollback to slide into.
        if ((HasPartialScrollRegion || UsingAlternateScreen) && CursorRow >= ScrollRegionBottom)
        {
            ScrollUp(1);
            return;
        }

        CursorRow++;
        EnsureRoom();
    }

    // ------------------------------------------------------------ scrolling

    /// <summary>First row a scroll may touch, counted from the top of the viewport.</summary>
    public int ScrollTop { get; private set; }

    /// <summary>Last row a scroll may touch; negative means the bottom of the viewport.</summary>
    public int ScrollBottom { get; private set; } = -1;

    /// <summary>True when a program has reserved part of the screen, leaving the rest fixed.</summary>
    public bool HasPartialScrollRegion => ScrollTop > 0 || (ScrollBottom >= 0 && ScrollBottom < ChildRows - 1);

    private int ScrollRegionTop => AddressingTop + ScrollTop;

    private int ScrollRegionBottom => AddressingTop + (ScrollBottom < 0 ? ChildRows - 1 : ScrollBottom);

    /// <summary>
    /// Brings the rows a region covers onto the screen. A program's last row can still be below
    /// the bottom of the window while it is starting from part-way down it; using the region is
    /// the program taking that room, and the screen moves forward to give it.
    /// </summary>
    private void EnsureRegionOnScreen()
    {
        var bottom = ScrollRegionBottom;
        if (bottom <= ScreenBottom) return;

        _screenTop = bottom - Rows + 1;
        EnsureRoom();
    }

    /// <summary>Sets the region, in viewport rows. An empty or inverted range resets it.</summary>
    public void SetScrollRegion(int top, int bottom)
    {
        if (top < 0 || bottom >= ChildRows || top >= bottom)
        {
            ResetScrollRegion();
            return;
        }

        ScrollTop = top;
        ScrollBottom = bottom;
        SetCursorInViewport(0, 0);
    }

    public void ResetScrollRegion()
    {
        ScrollTop = 0;
        ScrollBottom = -1;
    }

    /// <summary>
    /// Moves the region's lines up and blanks at the bottom. What leaves the top is kept when the
    /// region is the whole of the main screen — that is the scrollback — and discarded otherwise.
    /// </summary>
    public void ScrollUp(int count) => ScrollRegion(count, up: true);

    public void ScrollDown(int count) => ScrollRegion(count, up: false);

    private void ScrollRegion(int count, bool up)
    {
        EnsureRegionOnScreen();

        var top = ScrollRegionTop;
        var bottom = ScrollRegionBottom;

        LineAt(bottom);
        count = Math.Clamp(count, 1, bottom - top + 1);

        // Scrolling the whole of the main screen is what the scrollback is made of: the lines
        // going off the top are kept and the screen moves down over them. Discarding them where
        // they lay is what emptied the history behind a program that fills the window — every
        // line it printed was thrown away as the next one arrived, so scrolling back showed
        // nothing but the frame that happened to be on screen. A region covering part of the
        // screen is a panel inside it, and an alternate screen has no history to add to, so both
        // of those still lose what falls out of them.
        if (up && !UsingAlternateScreen && !HasPartialScrollRegion)
        {
            _screenTop += count;
            if (_childOrigin >= 0) _childOrigin += count;
            CursorRow += count;
            EnsureRoom();
            return;
        }

        for (var i = 0; i < count; i++)
        {
            if (up)
            {
                Lines.RemoveAt(top);
                Lines.Insert(bottom, new TerminalLine());
            }
            else
            {
                Lines.RemoveAt(bottom);
                Lines.Insert(top, new TerminalLine());
            }
        }
    }

    /// <summary>Opens blank lines at the cursor, pushing the rest of the region down.</summary>
    public void InsertLines(int count)
    {
        EnsureRegionOnScreen();

        var top = ScrollRegionTop;
        var bottom = ScrollRegionBottom;
        if (CursorRow < top || CursorRow > bottom) return;

        LineAt(bottom);
        count = Math.Clamp(count, 1, bottom - CursorRow + 1);

        for (var i = 0; i < count; i++)
        {
            Lines.RemoveAt(bottom);
            Lines.Insert(CursorRow, new TerminalLine());
        }

        CursorColumn = 0;
    }

    /// <summary>Removes lines at the cursor, pulling the rest of the region up.</summary>
    public void DeleteLines(int count)
    {
        EnsureRegionOnScreen();

        var top = ScrollRegionTop;
        var bottom = ScrollRegionBottom;
        if (CursorRow < top || CursorRow > bottom) return;

        LineAt(bottom);
        count = Math.Clamp(count, 1, bottom - CursorRow + 1);

        for (var i = 0; i < count; i++)
        {
            Lines.RemoveAt(CursorRow);
            Lines.Insert(bottom, new TerminalLine());
        }

        CursorColumn = 0;
    }

    // ------------------------------------------------------ editing in place

    /// <summary>
    /// Blanks cells from the cursor without moving it or shortening the line. This is how a
    /// program repainting a screen wipes what it drew last time; with it missing, the previous
    /// frame shows through the gaps in the new one.
    /// </summary>
    public void EraseCharacters(int count)
    {
        var line = LineAt(CursorRow);
        var end = Math.Min(CursorColumn + Math.Max(1, count), Columns);

        PadTo(line, end);
        for (var i = CursorColumn; i < end; i++) line.Cells[i] = MakeBlank();
    }

    /// <summary>Opens blanks at the cursor, shifting the rest of the line right off the margin.</summary>
    public void InsertCharacters(int count)
    {
        var line = LineAt(CursorRow);
        count = Math.Clamp(count, 1, Math.Max(1, Columns - CursorColumn));

        PadTo(line, CursorColumn);
        for (var i = 0; i < count; i++) line.Cells.Insert(CursorColumn, MakeBlank());
        if (line.Cells.Count > Columns) line.Cells.RemoveRange(Columns, line.Cells.Count - Columns);
    }

    /// <summary>Removes cells at the cursor, pulling the rest of the line left.</summary>
    public void DeleteCharacters(int count)
    {
        var line = LineAt(CursorRow);
        if (line.Cells.Count <= CursorColumn) return;

        count = Math.Clamp(count, 1, line.Cells.Count - CursorColumn);
        line.Cells.RemoveRange(CursorColumn, count);
    }

    // ----------------------------------------------------- alternate screen

    private List<TerminalLine>? _mainScreen;
    private int _mainViewTop;
    private int _mainScreenTop;
    private int _mainChildOrigin = -1;
    private int _mainCursorRow;
    private int _mainCursorColumn;

    /// <summary>True while a full-screen program has a screen of its own.</summary>
    public bool UsingAlternateScreen => _mainScreen is not null;

    /// <summary>
    /// Hands a program a screen of its own, keeping the session's out of its way.
    ///
    /// This is what <c>vim</c>, <c>less</c> and <c>htop</c> ask for. Everything they draw happens
    /// on a screen that is thrown away when they exit, so a full-screen program neither fills the
    /// scrollback with frames nor leaves its last one behind: the prompt comes back exactly as it
    /// was left.
    /// </summary>
    public void EnterAlternateScreen()
    {
        if (_mainScreen is not null) return;

        _mainScreen = new List<TerminalLine>(Lines);
        _mainViewTop = ViewTop;
        _mainScreenTop = _screenTop;
        _mainChildOrigin = _childOrigin;
        _mainCursorRow = CursorRow;
        _mainCursorColumn = CursorColumn;

        Lines.Clear();

        ViewTop = 0;
        _screenTop = 0;

        // A screen of its own is all of it; there is no session underneath to start below. It is
        // also exactly the window and no more: a screen that is thrown away when the program exits
        // is not history, so there is nothing for extra rows to hold, and rows the window cannot
        // show would simply be the bottom of `vim` hanging below it.
        if (_childOrigin >= 0) _childOrigin = 0;
        _childRows = Rows;
        CursorRow = 0;
        CursorColumn = 0;
        ResetScrollRegion();
        EnsureRoom();
    }

    public void LeaveAlternateScreen()
    {
        if (_mainScreen is null) return;

        Lines.Clear();
        Lines.AddRange(_mainScreen);
        _mainScreen = null;

        _screenTop = Math.Max(0, _mainScreenTop);
        _childOrigin = _mainChildOrigin;
        CursorRow = Math.Max(0, _mainCursorRow);
        CursorColumn = _mainCursorColumn;
        ResetScrollRegion();
        EnsureRoom();
        ViewTop = Math.Clamp(_mainViewTop, 0, MaxViewTop);
    }

    // -------------------------------------------------------- cursor memory

    private bool _cursorSaved;
    private int _savedRow;
    private int _savedColumn;
    private Color _savedForeground;
    private Color _savedBackground;
    private CellFlags _savedFlags;

    /// <summary>Whether the caret should be drawn; programs hide it while they repaint.</summary>
    public bool CursorVisible { get; set; } = true;

    /// <summary>Row is remembered against the viewport, so later scrolling does not move it.</summary>
    public void SaveCursor()
    {
        _cursorSaved = true;
        _savedRow = CursorRow - AddressingTop;
        _savedColumn = CursorColumn;
        _savedForeground = CurrentForeground;
        _savedBackground = CurrentBackground;
        _savedFlags = CurrentFlags;
    }

    /// <summary>Restoring without a save is defined as going home with attributes reset.</summary>
    public void RestoreCursor()
    {
        SetCursorInViewport(_cursorSaved ? Math.Max(0, _savedRow) : 0, _cursorSaved ? _savedColumn : 0);
        CurrentForeground = _cursorSaved ? _savedForeground : DefaultForeground;
        CurrentBackground = _cursorSaved ? _savedBackground : Color.Empty;
        CurrentFlags = _cursorSaved ? _savedFlags : CellFlags.None;
    }

    /// <summary>Absolute row addressing that leaves the column alone.</summary>
    public void SetCursorRowInViewport(int row)
    {
        CursorRow = AddressingTop + Math.Clamp(row, 0, ChildRows - 1);
        EnsureRoom();
    }

    public void CarriageReturn() => CursorColumn = 0;

    public void Backspace()
    {
        if (CursorColumn > 0) { CursorColumn--; return; }
        if (CursorRow <= AddressingTop) return;

        CursorRow--;
        CursorColumn = Math.Max(0, LineAt(CursorRow).Cells.Count - 1);
    }

    public void Tab()
    {
        var next = (CursorColumn / 8 + 1) * 8;
        while (CursorColumn < Math.Min(next, Columns)) PutChar(' ');
    }

    /// <summary>
    /// Restores what the rest of the class assumes: the cursor sits on a line that exists, the
    /// screen is a whole <see cref="Rows"/> lines long, and the scrollback is no longer than its
    /// limit. Output reaching past the last row of the screen is what moves the screen forward,
    /// and the line it leaves behind is what the scrollback is made of.
    /// </summary>
    public void EnsureRoom()
    {
        if (CursorRow < 0) CursorRow = 0;

        // Output running past the last row of a program's screen is that screen scrolling, and
        // its origin moves with it — the console scrolled too, so the two stay in step. Merely
        // addressing a row inside the screen leaves the origin alone, however far below the
        // window that row happens to be.
        if (_childOrigin >= 0 && CursorRow > AddressingBottom) _childOrigin = CursorRow - ChildRows + 1;

        if (CursorRow > ScreenBottom) _screenTop = ChildScreenTop(CursorRow - Rows + 1);

        while (Lines.Count < _screenTop + Rows) Lines.Add(new TerminalLine());

        // Trimming never eats into the screen itself, however short the limit is set.
        var excess = Math.Min(Lines.Count - ScrollbackLimit, _screenTop);
        if (excess <= 0) return;

        Lines.RemoveRange(0, excess);
        _screenTop -= excess;
        CursorRow -= excess;
        ViewTop = Math.Max(0, ViewTop - excess);
        ListingStart = Math.Max(0, ListingStart - excess);

        // Every absolute row moves when the front of the buffer goes, the program's origin
        // included. Leaving it behind is the same desync by a slower route: a long-running
        // program drifts one row out of step for every line the scrollback drops.
        if (_childOrigin >= 0) _childOrigin = Math.Max(0, _childOrigin - excess);
    }

    /// <summary>
    /// The rows a cursor movement may land on.
    ///
    /// Moving up stops at the top of the screen — or of the scroll region, when the cursor is
    /// inside one. Letting it carry on into the scrollback is not a harmless overshoot: a program
    /// repainting its display moves back over what it drew and erases it, so an unclamped move
    /// blanks and overwrites history the program has no idea is there. That is what took the
    /// earlier output away and left fragments of it on the lines that were rewritten.
    /// </summary>
    private (int Top, int Bottom) CursorBounds()
    {
        if (!HasPartialScrollRegion) return (AddressingTop, AddressingBottom);

        var top = ScrollRegionTop;
        var bottom = ScrollRegionBottom;
        return CursorRow >= top && CursorRow <= bottom ? (top, bottom) : (AddressingTop, AddressingBottom);
    }

    public void MoveCursor(int rowDelta, int columnDelta)
    {
        var (top, bottom) = CursorBounds();

        CursorRow = Math.Clamp(CursorRow + rowDelta, top, bottom);
        CursorColumn = Math.Clamp(CursorColumn + columnDelta, 0, Columns - 1);
        EnsureRoom();
    }

    /// <summary>Moves up a line, scrolling the region down when already at its top.</summary>
    public void ReverseLineFeed()
    {
        var (top, _) = CursorBounds();

        if (CursorRow <= top) ScrollDown(1);
        else CursorRow--;
    }

    /// <summary>Absolute positioning from an escape sequence, which addresses the screen.</summary>
    public void SetCursorInViewport(int row, int column)
    {
        CursorRow = AddressingTop + Math.Clamp(row, 0, ChildRows - 1);
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
        EnsureRoom();
    }

    /// <summary>0: to end of screen, 1: to start, 2: whole screen, 3: screen and scrollback.</summary>
    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var i = CursorRow + 1; i <= AddressingBottom && i < Lines.Count; i++) Lines[i].Cells.Clear();
                break;

            case 1:
                EraseInLine(1);
                for (var i = AddressingTop; i < CursorRow && i < Lines.Count; i++) Lines[i].Cells.Clear();
                break;

            case 2:
                // An alternate screen has no scrollback to move anything into, and a program's
                // own screen starts below the session rather than on top of it. In both cases the
                // literal reading is the right one: blank what the clear covers, where it lies.
                if (UsingAlternateScreen || _childOrigin >= 0)
                {
                    // Clearing the whole display is a program announcing that the screen is its
                    // own, so the window goes over its console now rather than after the frame
                    // that follows has been drawn. Waiting for the drawing to push it there works
                    // too, but the session above stays on screen until the first frame lands on
                    // top of it, which is a visible flash of the old output through the new one.
                    if (_childOrigin >= 0) _screenTop = Math.Max(_screenTop, _childOrigin);

                    EnsureRoom();
                    for (var i = AddressingTop; i <= AddressingBottom && i < Lines.Count; i++) Lines[i].Cells.Clear();

                    CursorRow = AddressingTop;
                    CursorColumn = 0;
                    break;
                }

                RetireScreen();
                break;

            case 3:
                // Only this one is a request to take the scrollback as well.
                Clear();
                break;
        }
    }

    /// <summary>
    /// Gives the program a blank screen by moving the one it is clearing up into the scrollback,
    /// rather than wiping those lines where they lie.
    ///
    /// Erasing in place is the literal reading of the sequence, and it is what leaves a hole: a
    /// program that clears on startup takes the session's history with it, and the empty rows it
    /// left behind stay in the buffer to be scrolled back through. Retiring the screen keeps what
    /// was on it — the banner, the command that started the program — where it can still be read.
    /// </summary>
    private void RetireScreen()
    {
        // A clear onto an already-blank screen must not bury the history under empty rows, so
        // anything blank at the end is reused rather than added to.
        while (Lines.Count > 0 && Lines[^1].Cells.All(cell => cell.IsEmpty)) Lines.RemoveAt(Lines.Count - 1);

        _screenTop = Lines.Count;
        CursorRow = _screenTop;
        CursorColumn = 0;
        EnsureRoom();
        ScrollToBottom();
    }

    /// <summary>0: to end of line, 1: to start of line, 2: whole line.</summary>
    public void EraseInLine(int mode)
    {
        var line = LineAt(CursorRow);

        switch (mode)
        {
            case 0:
                if (line.Cells.Count > CursorColumn) line.Cells.RemoveRange(CursorColumn, line.Cells.Count - CursorColumn);
                break;

            case 1:
                for (var i = 0; i < Math.Min(CursorColumn + 1, line.Cells.Count); i++)
                {
                    var cell = line.Cells[i];
                    cell.Character = ' ';
                    line.Cells[i] = cell;
                }

                break;

            case 2:
                line.Cells.Clear();
                break;
        }
    }

    /// <summary>Drops everything the cursor has moved past, used when the line editor redraws.</summary>
    public void TruncateTo(int row, int column)
    {
        if (row < 0) return;

        while (Lines.Count <= row) Lines.Add(new TerminalLine());
        if (Lines.Count > row + 1) Lines.RemoveRange(row + 1, Lines.Count - row - 1);

        var line = Lines[row];
        if (line.Cells.Count > column) line.Cells.RemoveRange(column, line.Cells.Count - column);
        line.Wrapped = false;

        CursorRow = row;
        CursorColumn = column;

        // The rows below the input still belong to the screen even once nothing is written on
        // them. Letting the buffer end here instead would drag the screen back up over the
        // scrollback — which is exactly what filled a freshly cleared screen back in with the
        // old output the moment the prompt was drawn.
        EnsureRoom();
    }

    public void Clear()
    {
        Lines.Clear();
        _screenTop = 0;
        if (_childOrigin >= 0) _childOrigin = 0;
        CursorRow = 0;
        CursorColumn = 0;
        ViewTop = 0;
        _links.Clear();
        _openLink = 0;
        ListingStart = 0;
        EnsureRoom();
    }

    /// <summary>Text of a rectangular selection, used by copy.</summary>
    public string GetText(int startRow, int startColumn, int endRow, int endColumn)
    {
        if (startRow > endRow || (startRow == endRow && startColumn > endColumn))
        {
            (startRow, endRow) = (endRow, startRow);
            (startColumn, endColumn) = (endColumn, startColumn);
        }

        var sb = new StringBuilder();

        for (var row = startRow; row <= endRow && row < Lines.Count; row++)
        {
            if (row < 0) continue;

            var line = Lines[row];
            var from = row == startRow ? startColumn : 0;
            var to = row == endRow ? endColumn : line.Cells.Count;

            from = Math.Clamp(from, 0, line.Cells.Count);
            to = Math.Clamp(to, 0, line.Cells.Count);

            for (var i = from; i < to; i++) sb.Append(line.Cells[i].Character);

            if (row != endRow && !line.Wrapped) sb.Append(Environment.NewLine);
        }

        return sb.ToString();
    }
}
