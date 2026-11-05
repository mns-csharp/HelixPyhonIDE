using System;
using System.Collections.Generic;

namespace HelixTextEditorNET.Core
{
    // A position with optional virtual space past the end of line. Port of
    // selection.h SelectionPosition; virtual space is carried for faithfulness and
    // stays 0 in practice.
    internal struct SelectionPosition
    {
        public int Position;
        public int VirtualSpace;

        public SelectionPosition(int pos, int virtualSpace)
        {
            Position = pos;
            VirtualSpace = virtualSpace;
        }

        public SelectionPosition(int pos) : this(pos, 0)
        {
        }

        public static bool operator ==(SelectionPosition a, SelectionPosition b)
        {
            return a.Position == b.Position && a.VirtualSpace == b.VirtualSpace;
        }

        public static bool operator !=(SelectionPosition a, SelectionPosition b)
        {
            return !(a == b);
        }

        public override bool Equals(object obj)
        {
            return obj is SelectionPosition && this == (SelectionPosition)obj;
        }

        public override int GetHashCode()
        {
            return Position * 397 ^ VirtualSpace;
        }
    }

    // One caret/anchor pair.
    internal struct SelectionRange
    {
        public SelectionPosition Caret;
        public SelectionPosition Anchor;

        public SelectionRange(int pos)
        {
            Caret = new SelectionPosition(pos);
            Anchor = new SelectionPosition(pos);
        }

        public SelectionRange(int caret, int anchor)
        {
            Caret = new SelectionPosition(caret);
            Anchor = new SelectionPosition(anchor);
        }

        public int Start
        {
            get { return Math.Min(Caret.Position, Anchor.Position); }
        }

        public int End
        {
            get { return Math.Max(Caret.Position, Anchor.Position); }
        }

        public bool Empty
        {
            get { return Caret == Anchor; }
        }

        public void ClearTo(int pos)
        {
            Caret = new SelectionPosition(pos);
            Anchor = Caret;
        }
    }

    // The full selection: one or more ranges, a "main" range, and a selection type.
    // Port of selection.h Selection.
    internal sealed class SelectionModel
    {
        public enum SelTypes { Stream, Rectangle, Lines, Thin }

        private readonly List<SelectionRange> _ranges = new List<SelectionRange>();
        private int _mainRange;

        public SelTypes SelType = SelTypes.Stream;

        public SelectionModel()
        {
            _ranges.Add(new SelectionRange(0));
        }

        public int Count
        {
            get { return _ranges.Count; }
        }

        public List<SelectionRange> Ranges
        {
            get { return _ranges; }
        }

        public SelectionRange Main
        {
            get { return _ranges[_mainRange]; }
            set { _ranges[_mainRange] = value; }
        }

        public int MainIndex
        {
            get { return _mainRange; }
        }

        public void SetMain(int i)
        {
            if (i >= 0 && i < _ranges.Count) _mainRange = i;
        }

        public void SetSelection(SelectionRange r)
        {
            _ranges.Clear();
            _ranges.Add(r);
            _mainRange = 0;
        }

        public void AddSelection(SelectionRange r)
        {
            _ranges.Add(r);
            _mainRange = _ranges.Count - 1;
        }

        // Collapse every range to its caret (clears selected text, keeps carets).
        public void Collapse()
        {
            for (int i = 0; i < _ranges.Count; i++)
            {
                SelectionRange r = _ranges[i];
                r.Anchor = r.Caret;
                _ranges[i] = r;
            }
        }

        public void DropAdditionalRanges()
        {
            SelectionRange m = Main;
            _ranges.Clear();
            _ranges.Add(m);
            _mainRange = 0;
        }

        public bool IsRectangular
        {
            get { return SelType == SelTypes.Rectangle || SelType == SelTypes.Thin; }
        }

        // Adjust all positions after a document insertion / deletion.
        public void InsertedAt(int pos, int len)
        {
            for (int i = 0; i < _ranges.Count; i++)
            {
                SelectionRange r = _ranges[i];
                if (r.Caret.Position >= pos) r.Caret.Position += len;
                if (r.Anchor.Position >= pos) r.Anchor.Position += len;
                _ranges[i] = r;
            }
        }

        public void DeletedAt(int pos, int len)
        {
            for (int i = 0; i < _ranges.Count; i++)
            {
                SelectionRange r = _ranges[i];
                r.Caret.Position = ShiftDelete(r.Caret.Position, pos, len);
                r.Anchor.Position = ShiftDelete(r.Anchor.Position, pos, len);
                _ranges[i] = r;
            }
        }

        private static int ShiftDelete(int p, int at, int len)
        {
            if (p > at + len) return p - len;
            if (p > at) return at;
            return p;
        }
    }
}
