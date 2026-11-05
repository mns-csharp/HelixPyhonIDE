using System;
using System.Collections.Generic;

namespace HelixTextEditorNET.Core
{
    // Platform-independent editor controller: owns the DocumentModel, selection,
    // and fold contraction state, and implements the editing operations. Port of
    // editor/editor.{h,cpp} Editor (typed methods instead of SCI_* dispatch).
    internal sealed class EditorEngine
    {
        public const int InvalidPosition = -1;

        private readonly DocumentModel _doc = new DocumentModel();
        private readonly SelectionModel _sel = new SelectionModel();
        private readonly ContractionState _cont = new ContractionState();

        private int _braceA = InvalidPosition;  // highlighted brace pair
        private int _braceB = InvalidPosition;
        private bool _braceBad;
        private int _targetStart;  // search/replace target range
        private int _targetEnd;
        private int _searchFlags;
        private string _lastSearch = string.Empty;
        private int _indicatorCurrent;
        private int _indicatorValue = 1;

        public EditorEngine()
        {
            // Keep fold expand/visible state and annotation-row counts aligned to the correct
            // document line across edits — see the comment on ContractionState.InsertLines.
            _doc.LinesInserted += (atLine, count) => _cont.InsertLines(atLine, count);
            _doc.LinesDeleted += (atLine, count) => _cont.DeleteLines(atLine, count);
        }

        public DocumentModel Doc
        {
            get { return _doc; }
        }

        public SelectionModel Sel
        {
            get { return _sel; }
        }

        public ContractionState Cont
        {
            get { return _cont; }
        }

        public int IndicatorCurrent
        {
            get { return _indicatorCurrent; }
            set { _indicatorCurrent = value; }
        }

        public int IndicatorValue
        {
            get { return _indicatorValue; }
            set { _indicatorValue = value; }
        }

        public int BraceA
        {
            get { return _braceA; }
        }

        public int BraceB
        {
            get { return _braceB; }
        }

        public bool BraceBad
        {
            get { return _braceBad; }
        }

        public int TargetStart
        {
            get { return _targetStart; }
            set { _targetStart = value; }
        }

        public int TargetEnd
        {
            get { return _targetEnd; }
            set { _targetEnd = value; }
        }

        public int SearchFlags
        {
            get { return _searchFlags; }
            set { _searchFlags = value; }
        }

        public int MainCaret
        {
            get { return _sel.Main.Caret.Position; }
        }

        public void SetEmptySelection(int pos)
        {
            pos = Clamp(pos, 0, _doc.Length);
            _sel.SetSelection(new SelectionRange(pos));
        }

        public void MoveCaret(int pos, bool extend)
        {
            pos = Clamp(pos, 0, _doc.Length);
            _sel.DropAdditionalRanges();
            _sel.SelType = SelectionModel.SelTypes.Stream;
            SelectionRange r = _sel.Main;
            r.Caret = new SelectionPosition(pos);
            if (!extend) r.Anchor = r.Caret;
            _sel.Main = r;
        }

        // Order range indices by Start position, ascending. Editing walks low->high
        // and carries a cumulative `delta` so each later range's original positions
        // are corrected for the net length change of all earlier edits.
        private int[] RangesLowToHigh()
        {
            var idx = new int[_sel.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            List<SelectionRange> ranges = _sel.Ranges;
            Array.Sort(idx, delegate (int a, int b) { return ranges[a].Start.CompareTo(ranges[b].Start); });
            return idx;
        }

        public void ReplaceSelectionWith(string s)
        {
            _doc.BeginUndoAction();
            int delta = 0;
            int addLen = s.Length;
            foreach (int i in RangesLowToHigh())
            {
                SelectionRange r = _sel.Ranges[i];
                int start = r.Start + delta;
                int selLen = r.End - r.Start;
                if (selLen > 0) _doc.DeleteChars(start, selLen);
                if (addLen > 0) _doc.InsertString(start, s);
                r.ClearTo(start + addLen);
                _sel.Ranges[i] = r;
                delta += addLen - selLen;
            }
            _doc.EndUndoAction();
        }

        public void InsertText(string s)
        {
            ReplaceSelectionWith(s);
        }

        public void DeleteBack()
        {
            _doc.BeginUndoAction();
            int delta = 0;
            foreach (int i in RangesLowToHigh())
            {
                SelectionRange r = _sel.Ranges[i];
                if (!r.Empty)
                {
                    int start = r.Start + delta;
                    int len = r.End - r.Start;
                    _doc.DeleteChars(start, len);
                    r.ClearTo(start);
                    delta -= len;
                }
                else if (r.Caret.Position > 0)
                {
                    int p = r.Caret.Position + delta;
                    _doc.DeleteChars(p - 1, 1);
                    r.ClearTo(p - 1);
                    delta -= 1;
                }
                _sel.Ranges[i] = r;
            }
            _doc.EndUndoAction();
        }

        public void DeleteForward()
        {
            _doc.BeginUndoAction();
            int delta = 0;
            foreach (int i in RangesLowToHigh())
            {
                SelectionRange r = _sel.Ranges[i];
                if (!r.Empty)
                {
                    int start = r.Start + delta;
                    int len = r.End - r.Start;
                    _doc.DeleteChars(start, len);
                    r.ClearTo(start);
                    delta -= len;
                }
                else if (r.Caret.Position < _doc.Length)
                {
                    _doc.DeleteChars(r.Caret.Position + delta, 1);
                    r.ClearTo(r.Caret.Position + delta);
                    delta -= 1;
                }
                _sel.Ranges[i] = r;
            }
            _doc.EndUndoAction();
        }

        public void ClearSelection()
        {
            _doc.BeginUndoAction();
            int delta = 0;
            foreach (int i in RangesLowToHigh())
            {
                SelectionRange r = _sel.Ranges[i];
                if (!r.Empty)
                {
                    int start = r.Start + delta;
                    int len = r.End - r.Start;
                    _doc.DeleteChars(start, len);
                    r.ClearTo(start);
                    delta -= len;
                }
                _sel.Ranges[i] = r;
            }
            _doc.EndUndoAction();
        }

        public void SelectAll()
        {
            _sel.SetSelection(new SelectionRange(_doc.Length, 0));
        }

        public void Undo()
        {
            int caret = _doc.Undo();
            if (caret != InvalidPosition) SetEmptySelection(caret);
        }

        public void Redo()
        {
            int caret = _doc.Redo();
            if (caret != InvalidPosition) SetEmptySelection(caret);
        }

        // ---- folding ----

        public void SyncFoldLines()
        {
            _cont.SetDocLines(_doc.LinesTotal);
        }

        // Last descendant line of a fold header (SCI_GETLASTCHILD).
        public int GetLastChild(int line, int level)
        {
            int levelNum = (level < 0)
                ? (_doc.GetLevel(line) & Lexing.Folding.FoldLevelNumberMask)
                : (level & Lexing.Folding.FoldLevelNumberMask);
            int n = _doc.LinesTotal;
            int last = line;
            for (int l = line + 1; l < n; l++)
            {
                int lv = _doc.GetLevel(l);
                if ((lv & Lexing.Folding.FoldLevelWhiteFlag) != 0) continue;  // blanks don't end a fold
                if ((lv & Lexing.Folding.FoldLevelNumberMask) <= levelNum) break;
                last = l;
            }
            return last;
        }

        public int GetFoldParent(int line)
        {
            int levelNum = _doc.GetLevel(line) & Lexing.Folding.FoldLevelNumberMask;
            for (int l = line - 1; l >= 0; l--)
            {
                int lv = _doc.GetLevel(l);
                if ((lv & Lexing.Folding.FoldLevelWhiteFlag) != 0) continue;
                if ((lv & Lexing.Folding.FoldLevelHeaderFlag) != 0 &&
                    (lv & Lexing.Folding.FoldLevelNumberMask) < levelNum) return l;
            }
            return -1;
        }

        // Recompute per-line visibility from the expanded flags + fold levels.
        public void RecalcFoldVisibility()
        {
            SyncFoldLines();
            _cont.SetAllVisible(true);
            int n = _doc.LinesTotal;
            // A line is hidden iff it lies inside any contracted fold.
            for (int h = 0; h < n; h++)
            {
                int lv = _doc.GetLevel(h);
                if ((lv & Lexing.Folding.FoldLevelHeaderFlag) == 0) continue;
                if (_cont.GetExpanded(h)) continue;
                int last = GetLastChild(h, -1);
                for (int l = h + 1; l <= last && l < n; l++) _cont.SetVisible(l, false);
            }
        }

        public void FoldLine(int line, FoldAction action)
        {
            SyncFoldLines();
            if ((_doc.GetLevel(line) & Lexing.Folding.FoldLevelHeaderFlag) == 0) return;
            bool expanded = _cont.GetExpanded(line);
            bool newExpanded;
            if (action == FoldAction.Contract) newExpanded = false;
            else if (action == FoldAction.Expand) newExpanded = true;
            else newExpanded = !expanded;  // toggle
            _cont.SetExpanded(line, newExpanded);
            RecalcFoldVisibility();
        }

        public void ToggleFold(int line)
        {
            FoldLine(line, FoldAction.Toggle);
        }

        public void FoldAll(FoldAction action)
        {
            SyncFoldLines();
            int n = _doc.LinesTotal;
            bool contractAll = action == FoldAction.Contract;
            if (action == FoldAction.Toggle)
            {
                // Toggle on the state of the first header line.
                contractAll = true;
                for (int l = 0; l < n; l++)
                {
                    if ((_doc.GetLevel(l) & Lexing.Folding.FoldLevelHeaderFlag) != 0)
                    {
                        contractAll = _cont.GetExpanded(l);
                        break;
                    }
                }
            }
            for (int l = 0; l < n; l++)
                if ((_doc.GetLevel(l) & Lexing.Folding.FoldLevelHeaderFlag) != 0)
                    _cont.SetExpanded(l, !contractAll);
            RecalcFoldVisibility();
        }

        // Expand every collapsed parent fold so `line` becomes visible.
        public void EnsureLineVisible(int line)
        {
            SyncFoldLines();
            int parent = GetFoldParent(line);
            while (parent >= 0)
            {
                _cont.SetExpanded(parent, true);
                parent = GetFoldParent(parent);
            }
            RecalcFoldVisibility();
        }

        // ---- brace matching ----

        public void SetBraceHighlight(int a, int b)
        {
            _braceA = a;
            _braceB = b;
            _braceBad = false;
        }

        public void SetBraceBad(int pos)
        {
            _braceA = pos;
            _braceB = InvalidPosition;
            _braceBad = pos != InvalidPosition;
        }

        // ---- line editing ----

        // Split region on '\n' into logical lines (no newlines); trailingNL reports
        // whether the region ended with '\n'.
        private static List<string> SplitLines(string region, out bool trailingNL)
        {
            var outLines = new List<string>();
            var cur = new System.Text.StringBuilder();
            foreach (char c in region)
            {
                if (c == '\n')
                {
                    outLines.Add(cur.ToString());
                    cur.Length = 0;
                }
                else
                {
                    cur.Append(c);
                }
            }
            trailingNL = region.Length > 0 && region[region.Length - 1] == '\n';
            if (!trailingNL) outLines.Add(cur.ToString());
            return outLines;
        }

        private static string JoinLines(List<string> lines, bool trailingNL)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]);
                if (i + 1 < lines.Count) sb.Append('\n');
            }
            if (trailingNL) sb.Append('\n');
            return sb.ToString();
        }

        public void MoveSelectedLines(int dir)
        {
            int n = _doc.LinesTotal;
            int lineTop = _doc.LineFromPosition(_sel.Main.Start);
            int lineBot = _doc.LineFromPosition(_sel.Main.End);
            // Native Scintilla parity: a selection that merely touches the start of a
            // line does not include that line in the moved block.
            if (lineBot > lineTop && !_sel.Main.Empty && _sel.Main.End == _doc.LineStart(lineBot))
                lineBot--;
            if (dir < 0 && lineTop == 0) return;
            if (dir > 0 && lineBot >= n - 1) return;

            if (dir < 0)
            {
                int regStart = _doc.LineStart(lineTop - 1);
                int regEnd = (lineBot + 1 < n) ? _doc.LineStart(lineBot + 1) : _doc.Length;
                bool trailingNL;
                List<string> lines = SplitLines(_doc.GetText(regStart, regEnd - regStart), out trailingNL);
                if (lines.Count < 2) return;
                // Rotate the previous line to the bottom of the block.
                string first = lines[0];
                lines.RemoveAt(0);
                lines.Add(first);
                string outText = JoinLines(lines, trailingNL);
                _doc.BeginUndoAction();
                _doc.DeleteChars(regStart, regEnd - regStart);
                _doc.InsertString(regStart, outText);
                _doc.EndUndoAction();
                int newStart = regStart;
                int movedLen = _doc.LineStart(lineBot) - regStart;
                _sel.SetSelection(new SelectionRange(newStart + movedLen, newStart));
            }
            else
            {
                int regStart = _doc.LineStart(lineTop);
                int regEnd = (lineBot + 2 < n) ? _doc.LineStart(lineBot + 2) : _doc.Length;
                bool trailingNL;
                List<string> lines = SplitLines(_doc.GetText(regStart, regEnd - regStart), out trailingNL);
                if (lines.Count < 2) return;
                // Rotate the next line to the top of the block.
                string last = lines[lines.Count - 1];
                lines.RemoveAt(lines.Count - 1);
                lines.Insert(0, last);
                string outText = JoinLines(lines, trailingNL);
                _doc.BeginUndoAction();
                _doc.DeleteChars(regStart, regEnd - regStart);
                _doc.InsertString(regStart, outText);
                _doc.EndUndoAction();
                int newStart = _doc.LineStart(lineTop + 1);
                _sel.SetSelection(new SelectionRange(newStart));
            }
        }

        // SCI_SELECTIONDUPLICATE: duplicate the selected text, or the caret line
        // when the selection is empty.
        public void SelectionDuplicate()
        {
            SelectionRange main = _sel.Main;
            _doc.BeginUndoAction();
            if (main.Empty)
            {
                int line = _doc.LineFromPosition(main.Caret.Position);
                int start = _doc.LineStart(line);
                int end = (line + 1 < _doc.LinesTotal) ? _doc.LineStart(line + 1) : _doc.Length;
                string lineText = _doc.GetText(start, end - start);
                if (lineText.Length == 0 || lineText[lineText.Length - 1] != '\n')
                    lineText = "\n" + lineText;
                _doc.InsertString(end, lineText);
            }
            else
            {
                string selText = _doc.GetText(main.Start, main.End - main.Start);
                _doc.InsertString(main.End, selText);
            }
            _doc.EndUndoAction();
        }

        // ---- find / target ----

        // Find `text` in [from,to) honoring `flags`; returns match start (sets
        // lenOut) or InvalidPosition. `to<from` searches backward (last match).
        public int FindInRange(string text, int flags, int from, int to, out int lenOut)
        {
            bool caseSensitive = (flags & (int)HelixTextEditorNET.SearchFlags.MatchCase) != 0;
            bool wholeWord = (flags & (int)HelixTextEditorNET.SearchFlags.WholeWord) != 0;
            bool regex = (flags & (int)HelixTextEditorNET.SearchFlags.Regex) != 0;
            if (to >= from)
            {
                return _doc.FindText(from, to, text, caseSensitive, wholeWord, regex, out lenOut);
            }
            // Backward: scan forward in [to,from) and keep the last match.
            int best = InvalidPosition;
            int bestLen = 0;
            int scan = to;
            while (true)
            {
                int len;
                int p = _doc.FindText(scan, from, text, caseSensitive, wholeWord, regex, out len);
                if (p == InvalidPosition) break;
                best = p;
                bestLen = len;
                scan = p + Math.Max(1, len);
            }
            lenOut = bestLen;
            return best;
        }

        // SCI_SEARCHINTARGET: search within [targetStart,targetEnd); on a match the
        // target collapses to the match. Returns the match position or -1.
        public int SearchInTarget(string text)
        {
            _lastSearch = text ?? string.Empty;
            int len;
            int p = FindInRange(_lastSearch, _searchFlags, _targetStart, _targetEnd, out len);
            if (p == InvalidPosition) return -1;
            _targetStart = p;
            _targetEnd = p + len;
            return p;
        }

        // SCI_REPLACETARGET.
        public int ReplaceTarget(string text)
        {
            string repl = text ?? string.Empty;
            _doc.BeginUndoAction();
            _doc.DeleteChars(_targetStart, _targetEnd - _targetStart);
            if (repl.Length > 0) _doc.InsertString(_targetStart, repl);
            _doc.EndUndoAction();
            _targetEnd = _targetStart + repl.Length;
            return repl.Length;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
