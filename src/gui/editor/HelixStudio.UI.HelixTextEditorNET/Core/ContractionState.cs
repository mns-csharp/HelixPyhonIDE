using System;
using System.Collections.Generic;

namespace HelixTextEditorNET.Core
{
    // Tracks per-line fold expansion + visibility, and per-line annotation row counts
    // (extra blank display rows reserved after a line, e.g. for BlockScopeRenderer's
    // Allman-style virtual braces), and maps between document lines and on-screen
    // (display) lines/rows. Port of view/contraction_state.h, extended with a
    // Scintilla-annotation-style row reservation the original didn't have.
    internal sealed class ContractionState
    {
        private readonly List<bool> _expanded = new List<bool>();
        private readonly List<bool> _visible = new List<bool>();
        private readonly List<int> _annotationLines = new List<int>();
        private int _hiddenCount;        // cached count of folded-away lines
        private int _annotationRowTotal; // cached sum of annotation rows on VISIBLE lines

        public int LinesInDoc
        {
            get { return _expanded.Count; }
        }

        // Resize to `n` document lines, preserving existing state. New lines are
        // expanded + visible with no annotation rows.
        public void SetDocLines(int n)
        {
            if (n < 0) n = 0;
            while (_expanded.Count > n) _expanded.RemoveAt(_expanded.Count - 1);
            while (_expanded.Count < n) _expanded.Add(true);
            while (_visible.Count > n) _visible.RemoveAt(_visible.Count - 1);
            while (_visible.Count < n) _visible.Add(true);
            while (_annotationLines.Count > n) _annotationLines.RemoveAt(_annotationLines.Count - 1);
            while (_annotationLines.Count < n) _annotationLines.Add(0);
            RecountHidden();            // resize may have dropped hidden lines; keep the cache exact
            RecountAnnotationRows();    // ditto for annotation rows
        }

        public void Clear()
        {
            _expanded.Clear();
            _visible.Clear();
            _annotationLines.Clear();
            _hiddenCount = 0;
            _annotationRowTotal = 0;
        }

        // Insert/remove AT the actual edit line (mirroring CellBuffer's own line-start splicing
        // — see the LinesInserted/LinesDeleted events on CellBuffer/DocumentModel) instead of
        // SetDocLines's truncate/append-at-the-end, which only kept these lists the right
        // LENGTH after an edit, not correctly ALIGNED to which document line each entry actually
        // describes — a line inserted/deleted anywhere but the very end of the file left fold
        // expand/visible state and annotation row counts associated with the wrong line from
        // that point on (symptom: Up/Down navigation "getting stuck", especially near fold
        // points, and virtual-brace annotation rows drifting from their real block).
        public void InsertLines(int atLine, int count)
        {
            if (count <= 0) return;
            atLine = Math.Max(0, Math.Min(atLine, _expanded.Count));
            var trueList = new bool[count];
            for (int i = 0; i < count; i++) trueList[i] = true;
            _expanded.InsertRange(atLine, trueList);
            _visible.InsertRange(atLine, trueList);
            _annotationLines.InsertRange(atLine, new int[count]);
            // New lines are visible with 0 annotation rows by construction, so _hiddenCount and
            // _annotationRowTotal (both sums over VISIBLE lines) don't need adjusting.
        }

        public void DeleteLines(int atLine, int count)
        {
            if (count <= 0 || atLine < 0 || atLine >= _expanded.Count) return;
            int n = Math.Min(count, _expanded.Count - atLine);
            if (n <= 0) return;
            // Subtract the removed lines' contribution to the cached totals BEFORE removing them.
            for (int i = atLine; i < atLine + n; i++)
            {
                if (!_visible[i]) _hiddenCount--;
                else _annotationRowTotal -= _annotationLines[i];
            }
            _expanded.RemoveRange(atLine, n);
            _visible.RemoveRange(atLine, n);
            _annotationLines.RemoveRange(atLine, n);
        }

        // ---- annotations: extra blank display rows reserved right after a line ----

        public int AnnotationGetLines(int docLine)
        {
            return (docLine >= 0 && docLine < _annotationLines.Count) ? _annotationLines[docLine] : 0;
        }

        public void AnnotationSetLines(int docLine, int numLines)
        {
            if (numLines < 0) numLines = 0;
            if (!InRange(docLine)) return;
            int delta = numLines - _annotationLines[docLine];
            if (delta == 0) return;
            _annotationLines[docLine] = numLines;
            if (GetVisible(docLine)) _annotationRowTotal += delta;
        }

        public void AnnotationClearAll()
        {
            for (int i = 0; i < _annotationLines.Count; i++) _annotationLines[i] = 0;
            _annotationRowTotal = 0;
        }

        // O(1): true when at least one line has a reserved annotation row. Lets callers
        // keep the cheap "every line is exactly one display row" fast path when nothing
        // uses annotations (the common case).
        public bool HasAnnotations
        {
            get { return _annotationRowTotal != 0; }
        }

        public bool GetExpanded(int docLine)
        {
            return At(_expanded, docLine, true);
        }

        public void SetExpanded(int docLine, bool expanded)
        {
            if (InRange(docLine)) _expanded[docLine] = expanded;
        }

        public bool GetVisible(int docLine)
        {
            return At(_visible, docLine, true);
        }

        public void SetVisible(int docLine, bool visible)
        {
            if (!InRange(docLine)) return;
            if (_visible[docLine] == visible) return;
            _visible[docLine] = visible;
            _hiddenCount += visible ? -1 : 1;
            _annotationRowTotal += (visible ? 1 : -1) * _annotationLines[docLine];
        }

        public void SetAllVisible(bool visible)
        {
            for (int i = 0; i < _visible.Count; i++) _visible[i] = visible;
            _hiddenCount = visible ? 0 : LinesInDoc;
            RecountAnnotationRows();
        }

        // O(1): true when no line is folded away (the common case).
        public bool AllLinesVisible
        {
            get { return _hiddenCount == 0; }
        }

        // Number of on-screen display rows: one per visible document line, plus every
        // visible line's own reserved annotation rows.
        public int LinesDisplayed
        {
            get { return Math.Max(1, LinesInDoc - _hiddenCount + _annotationRowTotal); }
        }

        // display row -> document line. Each visible line contributes 1 + its annotation
        // row count; a row that lands within a line's annotation rows still resolves to
        // that line (its "host"), matching how a click on an annotation is inert.
        public int DocFromDisplay(int displayLine)
        {
            if (displayLine < 0) return 0;
            int seen = 0;
            for (int l = 0; l < LinesInDoc; l++)
            {
                if (!_visible[l]) continue;
                int rows = 1 + _annotationLines[l];
                if (displayLine < seen + rows) return l;
                seen += rows;
            }
            return Math.Max(0, LinesInDoc - 1);
        }

        // document line -> display row of that line's OWN top row (i.e. not counting its
        // own trailing annotation rows, which come right after it). Clamps to the nearest
        // preceding visible line if docLine itself is hidden.
        public int DisplayFromDoc(int docLine)
        {
            int seen = 0;
            for (int l = 0; l < LinesInDoc && l < docLine; l++)
                if (_visible[l]) seen += 1 + _annotationLines[l];
            return seen;
        }

        // Next/previous VISIBLE document line, skipping folded-away lines. Used for single-step
        // Up/Down arrow navigation instead of DisplayFromDoc/DocFromDisplay's display-ROW math:
        // a line's reserved annotation rows (BlockScopeRenderer's virtual braces) are decorative
        // extra rows attached to that same line, not separate navigable stops, but DocFromDisplay
        // resolves any display row landing within them back to that line — so walking by display
        // row made a single Down press from an annotated line (every block header/last-content
        // line — i.e. every fold point) need one extra press per reserved row to actually leave
        // it, while Up (approached from the row after the block) always resolved in one press.
        // Stepping by real document line instead is symmetric and matches real Scintilla, where
        // annotations never consume arrow-key travel.
        public int NextVisibleLine(int docLine)
        {
            for (int l = docLine + 1; l < LinesInDoc; l++)
                if (_visible[l]) return l;
            return Math.Max(0, LinesInDoc - 1);
        }

        public int PrevVisibleLine(int docLine)
        {
            for (int l = docLine - 1; l >= 0; l--)
                if (_visible[l]) return l;
            return 0;
        }

        private bool InRange(int l)
        {
            return l >= 0 && l < LinesInDoc;
        }

        private static bool At(List<bool> v, int l, bool dflt)
        {
            return (l >= 0 && l < v.Count) ? v[l] : dflt;
        }

        private void RecountHidden()
        {
            _hiddenCount = 0;
            foreach (bool v in _visible)
                if (!v) ++_hiddenCount;
        }

        private void RecountAnnotationRows()
        {
            _annotationRowTotal = 0;
            for (int i = 0; i < _annotationLines.Count; i++)
                if (i < _visible.Count && _visible[i]) _annotationRowTotal += _annotationLines[i];
        }
    }
}
