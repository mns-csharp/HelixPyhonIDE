using System;

namespace HelixTextEditorNET
{
    // Public enums matching the original ScintillaNET 3.6.3 names and underlying
    // SCI_* values, so consumer code (including raw int casts like (Lexer)111)
    // compiles and behaves unchanged.

    public enum Lexer
    {
        Container = 0,
        Null = 1,
        Python = 2,
        Cpp = 3,
        Html = 4,
        Xml = 5,
        Perl = 6,
        Sql = 7,
        Vb = 8,
        Properties = 9,
        Batch = 12,
        Lua = 15,
        Pascal = 18,
        Ada = 20,
        Lisp = 21,
        Ruby = 22,
        VbScript = 28,
        Asm = 34,
        Fortran = 36,
        Css = 38,
        Verilog = 41,
        BlitzBasic = 66,
        PureBasic = 67,
        PhpScript = 69,
        Smalltalk = 72,
        Cmake = 80,
        R = 86,
        Markdown = 98,
        Rust = 111,
        Json = 120,
    }

    public enum WrapMode
    {
        None = 0,
        Word = 1,
        Char = 2,
        Whitespace = 3,
    }

    public enum IndentView
    {
        None = 0,
        Real = 1,
        LookForward = 2,
        LookBoth = 3,
    }

    public enum EdgeMode
    {
        None = 0,
        Line = 1,
        Background = 2,
    }

    public enum MarginType
    {
        Symbol = 0,
        Number = 1,
        BackColor = 2,
        ForeColor = 3,
        Text = 4,
        RightText = 5,
        Color = 6,
    }

    public enum MarkerSymbol
    {
        Circle = 0,
        RoundRect = 1,
        Arrow = 2,
        SmallRect = 3,
        ShortArrow = 4,
        Empty = 5,
        ArrowDown = 6,
        Minus = 7,
        Plus = 8,
        VLine = 9,
        LCorner = 10,
        TCorner = 11,
        BoxPlus = 12,
        BoxPlusConnected = 13,
        BoxMinus = 14,
        BoxMinusConnected = 15,
        LCornerCurve = 16,
        TCornerCurve = 17,
        CirclePlus = 18,
        CirclePlusConnected = 19,
        CircleMinus = 20,
        CircleMinusConnected = 21,
        Background = 22,
        DotDotDot = 23,
        Arrows = 24,
        FullRect = 26,
        LeftRect = 27,
        Available = 28,
        Underline = 29,
        RgbaImage = 30,
        Bookmark = 31,
        Character = 10000,
    }

    [Flags]
    public enum AutomaticFold
    {
        None = 0,
        Show = 1,
        Click = 2,
        Change = 4,
    }

    public enum FoldAction
    {
        Contract = 0,
        Expand = 1,
        Toggle = -1,
    }

    [Flags]
    public enum FoldLevelFlags
    {
        None = 0,
        White = 0x1000,
        Header = 0x2000,
    }

    public enum IndicatorStyle
    {
        Plain = 0,
        Squiggle = 1,
        TT = 2,
        Diagonal = 3,
        Strike = 4,
        Hidden = 5,
        Box = 6,
        RoundBox = 7,
        StraightBox = 8,
        Dash = 9,
        Dots = 10,
        SquiggleLow = 11,
        DotBox = 12,
        SquigglePixmap = 13,
        CompositionThick = 14,
        CompositionThin = 15,
        FullBox = 16,
        TextFore = 17,
        Point = 18,
        PointCharacter = 19,
    }

    public enum Order
    {
        Presorted = 0,
        PerformSort = 1,
        Custom = 2,
    }

    [Flags]
    public enum SearchFlags
    {
        None = 0,
        WholeWord = 0x2,
        MatchCase = 0x4,
        WordStart = 0x00100000,
        Regex = 0x00200000,
        Posix = 0x00400000,
        Cxx11Regex = 0x00800000,
    }

    [Flags]
    public enum UpdateChange
    {
        Content = 0x1,
        Selection = 0x2,
        VScroll = 0x4,
        HScroll = 0x8,
    }

    // SCI_* command message ids (the subset with named members in HelixTextEditorNET).
    public enum Command
    {
        LineDown = 2300,
        LineUp = 2301,
        CharLeft = 2304,
        CharRight = 2306,
        WordLeft = 2308,
        WordRight = 2310,
        Home = 2312,
        LineEnd = 2314,
        DocumentStart = 2316,
        DocumentEnd = 2318,
        PageUp = 2320,
        PageDown = 2322,
        DeleteBack = 2326,
        Tab = 2327,
        BackTab = 2328,
        NewLine = 2329,
        Undo = 2176,
        Redo = 2011,
        Cut = 2177,
        Copy = 2178,
        Paste = 2179,
        Clear = 2180,
        SelectAll = 2013,
        LineDelete = 2338,
        LineTranspose = 2339,
        LineDuplicate = 2404,
        SelectionDuplicate = 2469,
        MoveSelectedLinesUp = 2620,
        MoveSelectedLinesDown = 2621,
    }

    public enum Eol
    {
        CrLf = 0,
        Cr = 1,
        Lf = 2,
    }
}
