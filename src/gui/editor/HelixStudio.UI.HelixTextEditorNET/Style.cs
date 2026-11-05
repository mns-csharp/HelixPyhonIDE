using System;
using System.Drawing;

namespace HelixTextEditorNET
{
    // One entry in the Scintilla style table, plus the well-known style index
    // constants. API-compatible with ScintillaNET 3.6.3's Style class.
    public class Style
    {
        // Well-known style indices.
        public const int Default = 32;      // STYLE_DEFAULT
        public const int LineNumber = 33;   // STYLE_LINENUMBER
        public const int BraceLight = 34;   // STYLE_BRACELIGHT
        public const int BraceBad = 35;     // STYLE_BRACEBAD
        public const int ControlChar = 36;  // STYLE_CONTROLCHAR
        public const int IndentGuide = 37;  // STYLE_INDENTGUIDE
        public const int CallTip = 38;      // STYLE_CALLTIP
        public const int Max = 255;

        // SCE_P_* style indices for Lexer.Python.
        public static class Python
        {
            public const int Default = 0;
            public const int CommentLine = 1;
            public const int Number = 2;
            public const int String = 3;
            public const int Character = 4;
            public const int Word = 5;
            public const int Triple = 6;
            public const int TripleDouble = 7;
            public const int ClassName = 8;
            public const int DefName = 9;
            public const int Operator = 10;
            public const int Identifier = 11;
            public const int CommentBlock = 12;
            public const int StringEol = 13;
            public const int Word2 = 14;
            public const int Decorator = 15;
        }

        // SCE_C_* style indices for Lexer.Cpp.
        public static class Cpp
        {
            public const int Default = 0;
            public const int Comment = 1;
            public const int CommentLine = 2;
            public const int CommentDoc = 3;
            public const int Number = 4;
            public const int Word = 5;
            public const int String = 6;
            public const int Character = 7;
            public const int Uuid = 8;
            public const int Preprocessor = 9;
            public const int Operator = 10;
            public const int Identifier = 11;
            public const int StringEol = 12;
            public const int Verbatim = 13;
            public const int Regex = 14;
            public const int CommentLineDoc = 15;
            public const int Word2 = 16;
            public const int CommentDocKeyword = 17;
            public const int CommentDocKeywordError = 18;
            public const int GlobalClass = 19;
            public const int StringRaw = 20;
            public const int TripleVerbatim = 21;
            public const int HashQuotedString = 22;
            public const int PreprocessorComment = 23;
            public const int PreprocessorCommentDoc = 24;
            public const int UserLiteral = 25;
            public const int TaskMarker = 26;
            public const int EscapeSequence = 27;
        }

        private readonly Scintilla _scintilla;
        private readonly int _index;

        internal Style(Scintilla scintilla, int index)
        {
            _scintilla = scintilla;
            _index = index;
        }

        public int Index
        {
            get { return _index; }
        }

        public string Font
        {
            get { return _scintilla.ViewModel.StyleAt(_index).FontName; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).FontName = value ?? "Consolas";
                _scintilla.StylesChanged();
            }
        }

        public int Size
        {
            get { return (int)_scintilla.ViewModel.StyleAt(_index).SizeF; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).SizeF = value;
                _scintilla.StylesChanged();
            }
        }

        public float SizeF
        {
            get { return _scintilla.ViewModel.StyleAt(_index).SizeF; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).SizeF = value;
                _scintilla.StylesChanged();
            }
        }

        public Color ForeColor
        {
            get { return _scintilla.ViewModel.StyleAt(_index).ForeColor; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).ForeColor = value;
                _scintilla.StylesChanged();
            }
        }

        public Color BackColor
        {
            get { return _scintilla.ViewModel.StyleAt(_index).BackColor; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).BackColor = value;
                _scintilla.StylesChanged();
            }
        }

        public bool Bold
        {
            get { return _scintilla.ViewModel.StyleAt(_index).Bold; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).Bold = value;
                _scintilla.StylesChanged();
            }
        }

        public bool Italic
        {
            get { return _scintilla.ViewModel.StyleAt(_index).Italic; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).Italic = value;
                _scintilla.StylesChanged();
            }
        }

        public bool Underline
        {
            get { return _scintilla.ViewModel.StyleAt(_index).Underline; }
            set
            {
                _scintilla.ViewModel.StyleAt(_index).Underline = value;
                _scintilla.StylesChanged();
            }
        }
    }

    // The style table, indexed by style byte. Mirrors ScintillaNET's StyleCollection API.
    public class StyleCollection
    {
        private readonly Scintilla _scintilla;
        private readonly Style[] _styles;

        internal StyleCollection(Scintilla scintilla)
        {
            _scintilla = scintilla;
            _styles = new Style[Rendering.ViewStyleModel.MaxStyles];
        }

        public Style this[int index]
        {
            get
            {
                if (index < 0) index = 0;
                if (index >= _styles.Length) index = _styles.Length - 1;
                if (_styles[index] == null) _styles[index] = new Style(_scintilla, index);
                return _styles[index];
            }
        }

        public int Count
        {
            get { return _styles.Length; }
        }
    }
}
