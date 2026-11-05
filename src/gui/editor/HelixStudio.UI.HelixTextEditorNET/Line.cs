using System;
using System.Collections;
using System.Collections.Generic;

namespace HelixTextEditorNET
{
    // One document line. API-compatible with ScintillaNET 3.6.3's Line.
    public class Line
    {
        private readonly Scintilla _scintilla;
        private readonly int _index;

        internal Line(Scintilla scintilla, int index)
        {
            _scintilla = scintilla;
            _index = index;
        }

        public int Index
        {
            get { return _index; }
        }

        // First character position of the line.
        public int Position
        {
            get { return _scintilla.Document.LineStart(_index); }
        }

        // Position just past the line, including its EOL characters.
        public int EndPosition
        {
            get
            {
                if (_index + 1 < _scintilla.Document.LinesTotal)
                    return _scintilla.Document.LineStart(_index + 1);
                return _scintilla.Document.Length;
            }
        }

        // The line's text including its EOL characters.
        public string Text
        {
            get
            {
                int start = Position;
                return _scintilla.Document.GetText(start, EndPosition - start);
            }
        }

        public int Length
        {
            get { return EndPosition - Position; }
        }

        // Pixel height of the line.
        public int Height
        {
            get { return _scintilla.LineHeightPixels; }
        }

        // Display (visible) line index accounting for collapsed folds.
        public int DisplayIndex
        {
            get { return _scintilla.Contraction.DisplayFromDoc(_index); }
        }

        public bool Visible
        {
            get { return _scintilla.Contraction.GetVisible(_index); }
        }

        public bool Expanded
        {
            get
            {
                _scintilla.SyncFoldLines();
                return _scintilla.Contraction.GetExpanded(_index);
            }
            set
            {
                _scintilla.SyncFoldLines();
                _scintilla.Contraction.SetExpanded(_index, value);
                _scintilla.RecalcFoldVisibility();
            }
        }

        public FoldLevelFlags FoldLevelFlags
        {
            get { return (FoldLevelFlags)(_scintilla.Document.GetLevel(_index) & ~Lexing.Folding.FoldLevelNumberMask); }
        }

        public int FoldLevel
        {
            get { return _scintilla.Document.GetLevel(_index) & Lexing.Folding.FoldLevelNumberMask; }
        }

        // Last line that is a child of this fold header.
        public int LastChild
        {
            get { return _scintilla.GetLastChild(_index, -1); }
        }

        public int FoldParent
        {
            get { return _scintilla.GetFoldParent(_index); }
        }

        // Bitmask of markers on this line.
        public uint MarkerGet()
        {
            return (uint)_scintilla.Document.MarkersAtLine(_index);
        }

        public int MarkerAdd(int marker)
        {
            int result = _scintilla.Document.AddMark(_index, marker);
            _scintilla.InvalidateView();
            return result;
        }

        public void MarkerDelete(int marker)
        {
            _scintilla.Document.DeleteMark(_index, marker);
            _scintilla.InvalidateView();
        }

        // Expand any collapsed parents so this line is on screen.
        public void EnsureVisible()
        {
            _scintilla.EnsureLineVisible(_index);
        }

        public void Goto()
        {
            _scintilla.GotoPosition(Position);
        }

        public void ToggleFold()
        {
            _scintilla.ToggleFoldLine(_index);
        }
    }

    public class LineCollection : IEnumerable<Line>
    {
        private readonly Scintilla _scintilla;

        internal LineCollection(Scintilla scintilla)
        {
            _scintilla = scintilla;
        }

        public int Count
        {
            get { return _scintilla.Document.LinesTotal; }
        }

        public Line this[int index]
        {
            get
            {
                if (index < 0) index = 0;
                int count = Count;
                if (index >= count) index = count - 1;
                return new Line(_scintilla, index);
            }
        }

        public IEnumerator<Line> GetEnumerator()
        {
            int count = Count;
            for (int i = 0; i < count; i++) yield return new Line(_scintilla, i);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
