using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HelixTextEditorNET.Core
{
    internal enum CharacterClass { Space, NewLine, Word, Punctuation }

    // The document model: text (CellBuffer) plus parallel per-character style bytes,
    // per-line markers / fold levels / line state, character classification, word
    // boundaries, and search. Port of core/document.{h,cpp}.
    internal sealed class DocumentModel
    {
        public const int InvalidPosition = -1;
        public const int FoldLevelBase = 0x400;  // SC_FOLDLEVELBASE

        private readonly CellBuffer _cb = new CellBuffer();
        private readonly SplitVector<byte> _styles = new SplitVector<byte>();  // one style byte per character
        private int _stylingPos;
        private int _dirtyStart = InvalidPosition;  // lowest changed pos since last ResetDirty()
        private readonly List<int> _markers = new List<int>();    // per-line marker bitmask
        private readonly List<int> _levels = new List<int>();     // per-line fold level
        private readonly List<int> _lineState = new List<int>();  // per-line lexer state
        private readonly Decorations _decorations = new Decorations();

        // Re-exposes CellBuffer's precise (atLine, count) line-change notifications so
        // EditorEngine can keep ContractionState's expanded/visible/annotation-row state
        // (per-line, same shape as _markers/_levels/_lineState below) correctly aligned too.
        public event Action<int, int> LinesInserted;
        public event Action<int, int> LinesDeleted;

        public DocumentModel()
        {
            _markers.Add(0);
            _levels.Add(FoldLevelBase);
            _lineState.Add(0);
            _cb.LinesInserted += OnCellBufferLinesInserted;
            _cb.LinesDeleted += OnCellBufferLinesDeleted;
        }

        // Insert/remove AT the actual edit position (mirroring CellBuffer._lineStarts) instead of
        // ResizeLineData()'s truncate/append-at-the-end, which only kept these arrays the right
        // LENGTH, not correctly ALIGNED to which document line each entry actually describes.
        private void OnCellBufferLinesInserted(int atLine, int count)
        {
            if (count <= 0) return;
            var zeros = new int[count];
            var levels = new int[count];
            for (int i = 0; i < count; i++) levels[i] = FoldLevelBase;
            _markers.InsertRange(Math.Min(atLine, _markers.Count), zeros);
            _levels.InsertRange(Math.Min(atLine, _levels.Count), levels);
            _lineState.InsertRange(Math.Min(atLine, _lineState.Count), zeros);
            var h = LinesInserted;
            if (h != null) h(atLine, count);
        }

        private void OnCellBufferLinesDeleted(int atLine, int count)
        {
            if (count <= 0 || atLine < 0 || atLine >= _markers.Count) return;
            int n = Math.Min(count, _markers.Count - atLine);
            if (n <= 0) return;
            _markers.RemoveRange(atLine, n);
            _levels.RemoveRange(atLine, n);
            _lineState.RemoveRange(atLine, n);
            var h = LinesDeleted;
            if (h != null) h(atLine, n);
        }

        // ---- text ----

        public int Length
        {
            get { return _cb.Length; }
        }

        public char CharAt(int pos)
        {
            return _cb.CharAt(pos);
        }

        public string GetText(int pos, int len)
        {
            return _cb.GetText(pos, len);
        }

        public void GetCharRange(char[] buffer, int pos, int len)
        {
            _cb.GetCharRange(buffer, pos, len);
        }

        public int LinesTotal
        {
            get { return _cb.Lines; }
        }

        public int LineStart(int line)
        {
            return _cb.LineStart(line);
        }

        // Position before the line's EOL.
        public int LineEnd(int line)
        {
            if (line >= LinesTotal - 1) return Length;
            int end = LineStart(line + 1);
            if (end > 0 && CharAt(end - 1) == '\n') --end;
            if (end > 0 && CharAt(end - 1) == '\r') --end;
            return end;
        }

        public int LineFromPosition(int pos)
        {
            return _cb.LineFromPosition(pos);
        }

        // Fires after any successful text change (used by the control to sync
        // selection, notify TextChanged, and schedule re-lex).
        public event Action<int, int, bool> TextModified;  // (position, length, isInsert)

        public int InsertString(int position, string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            _cb.InsertString(position, s);
            _styles.InsertValue(position, s.Length, 0);
            _decorations.InsertSpace(position, s.Length);
            ResizeLineData();
            if (_dirtyStart == InvalidPosition || position < _dirtyStart) _dirtyStart = position;
            var handler = TextModified;
            if (handler != null) handler(position, s.Length, true);
            return s.Length;
        }

        public bool DeleteChars(int position, int deleteLength)
        {
            if (deleteLength <= 0) return false;
            if (position < 0 || position + deleteLength > Length) return false;
            _cb.DeleteChars(position, deleteLength);
            _styles.DeleteRange(position, deleteLength);
            _decorations.DeleteSpace(position, deleteLength);
            ResizeLineData();
            if (_dirtyStart == InvalidPosition || position < _dirtyStart) _dirtyStart = position;
            var handler = TextModified;
            if (handler != null) handler(position, deleteLength, false);
            return true;
        }

        public void SetText(string text)
        {
            if (Length > 0) DeleteChars(0, Length);
            if (!string.IsNullOrEmpty(text)) InsertString(0, text);
        }

        // ---- styling (per-character style byte) ----

        public byte StyleAt(int pos)
        {
            return _styles.ValueAt(pos);
        }

        public void SetStyleFor(int start, int length, byte style)
        {
            _styles.FillRange(start, length, style);
        }

        public void StartStyling(int pos)
        {
            _stylingPos = pos;
        }

        public void SetStyles(int length, byte style)
        {
            SetStyleFor(_stylingPos, length, style);
            _stylingPos += length;
        }

        // ---- dirty range (for incremental re-lex / re-fold) ----

        public int DirtyStart
        {
            get { return _dirtyStart; }
        }

        public void ResetDirty()
        {
            _dirtyStart = InvalidPosition;
        }

        // ---- character classification + words ----

        public CharacterClass ClassifyChar(char ch)
        {
            if (ch == '\r' || ch == '\n') return CharacterClass.NewLine;
            if (ch == ' ' || ch == '\t' || (ch >= '\u0009' && ch <= '\u000d')) return CharacterClass.Space;
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch >= '\u0080') return CharacterClass.Word;
            return CharacterClass.Punctuation;
        }

        public bool IsWordChar(char ch)
        {
            return ClassifyChar(ch) == CharacterClass.Word;
        }

        public int WordStartPosition(int pos, bool onlyWordCharacters)
        {
            CharacterClass ccStart = CharacterClass.Word;
            if (pos > 0)
            {
                ccStart = ClassifyChar(CharAt(pos - 1));
                if (onlyWordCharacters && ccStart != CharacterClass.Word) return pos;
            }
            while (pos > 0 && ClassifyChar(CharAt(pos - 1)) == ccStart) --pos;
            return pos;
        }

        public int WordEndPosition(int pos, bool onlyWordCharacters)
        {
            CharacterClass ccStart = CharacterClass.Word;
            if (pos < Length)
            {
                ccStart = ClassifyChar(CharAt(pos));
                if (onlyWordCharacters && ccStart != CharacterClass.Word) return pos;
            }
            while (pos < Length && ClassifyChar(CharAt(pos)) == ccStart) ++pos;
            return pos;
        }

        public bool IsWordAt(int start, int end)
        {
            bool startBoundary = (start <= 0) ||
                                 !IsWordChar(CharAt(start - 1)) ||
                                 !IsWordChar(CharAt(start));
            bool endBoundary = (end >= Length) ||
                               !IsWordChar(CharAt(end)) ||
                               !IsWordChar(CharAt(end - 1));
            return startBoundary && endBoundary;
        }

        // Position of the brace matching the one at `pos` (same style only, so braces
        // inside comments/strings are ignored), or InvalidPosition. Handles ()[]{}.
        public int BraceMatch(int pos)
        {
            if (pos < 0 || pos >= Length) return InvalidPosition;
            char brace = CharAt(pos);
            char opposite;
            int dir;
            switch (brace)
            {
                case '(': opposite = ')'; dir = 1; break;
                case ')': opposite = '('; dir = -1; break;
                case '[': opposite = ']'; dir = 1; break;
                case ']': opposite = '['; dir = -1; break;
                case '{': opposite = '}'; dir = 1; break;
                case '}': opposite = '{'; dir = -1; break;
                default: return InvalidPosition;
            }
            byte styleStart = StyleAt(pos);
            int depth = 0;
            for (int p = pos; p >= 0 && p < Length; p += dir)
            {
                if (StyleAt(p) != styleStart) continue;  // skip braces in other contexts
                char c = CharAt(p);
                if (c == brace) ++depth;
                else if (c == opposite)
                {
                    --depth;
                    if (depth == 0) return p;
                }
            }
            return InvalidPosition;
        }

        // ---- search ----

        // Returns the match position in [minPos, maxPos), or InvalidPosition.
        // `lengthFound` is set to the match length. Supports plain + regex search.
        public int FindText(int minPos, int maxPos, string search, bool caseSensitive,
                            bool wholeWord, bool regex, out int lengthFound)
        {
            lengthFound = 0;
            if (string.IsNullOrEmpty(search)) return InvalidPosition;
            minPos = Math.Max(0, minPos);
            maxPos = Math.Min(Length, maxPos);
            if (minPos >= maxPos) return InvalidPosition;

            string hay = GetText(minPos, maxPos - minPos);

            if (regex)
            {
                Regex re;
                try
                {
                    RegexOptions opts = RegexOptions.None;
                    if (!caseSensitive) opts |= RegexOptions.IgnoreCase;
                    re = new Regex(search, opts);
                }
                catch (ArgumentException)
                {
                    return InvalidPosition;  // invalid pattern behaves as "not found"
                }
                int searchStart = 0;
                while (searchStart <= hay.Length)
                {
                    Match m = re.Match(hay, searchStart);
                    if (!m.Success) break;
                    int matchStart = minPos + m.Index;
                    int matchLen = m.Length;
                    if (!wholeWord || IsWordAt(matchStart, matchStart + matchLen))
                    {
                        lengthFound = matchLen;
                        return matchStart;
                    }
                    searchStart = m.Index + Math.Max(1, m.Length);
                }
                return InvalidPosition;
            }

            StringComparison cmp = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            int from = 0;
            while (true)
            {
                int found = hay.IndexOf(search, from, cmp);
                if (found < 0) return InvalidPosition;
                int matchStart = minPos + found;
                int matchLen = search.Length;
                if (!wholeWord || IsWordAt(matchStart, matchStart + matchLen))
                {
                    lengthFound = matchLen;
                    return matchStart;
                }
                from = found + 1;
            }
        }

        // ---- per-line markers / fold levels / line state ----

        public int AddMark(int line, int markerNum)
        {
            if (line < 0 || line >= LinesTotal) return -1;
            _markers[line] |= (1 << markerNum);
            return markerNum;
        }

        public void DeleteMark(int line, int markerNum)
        {
            if (line < 0 || line >= LinesTotal) return;
            _markers[line] &= ~(1 << markerNum);
        }

        public void DeleteAllMarks(int markerNum)
        {
            for (int i = 0; i < _markers.Count; i++) _markers[i] &= ~(1 << markerNum);
        }

        public int MarkersAtLine(int line)
        {
            if (line < 0 || line >= LinesTotal) return 0;
            return _markers[line];
        }

        public int LineFromMarkerNext(int lineStart, int markerMask)
        {
            for (int l = Math.Max(0, lineStart); l < LinesTotal; l++)
                if ((_markers[l] & markerMask) != 0) return l;
            return -1;
        }

        public int LineFromMarkerPrevious(int lineStart, int markerMask)
        {
            for (int l = Math.Min(lineStart, LinesTotal - 1); l >= 0; l--)
                if ((_markers[l] & markerMask) != 0) return l;
            return -1;
        }

        public void SetLevel(int line, int level)
        {
            if (line >= 0 && line < LinesTotal) _levels[line] = level;
        }

        public int GetLevel(int line)
        {
            if (line < 0 || line >= LinesTotal) return FoldLevelBase;
            return _levels[line];
        }

        public void SetLineState(int line, int state)
        {
            if (line >= 0 && line < LinesTotal) _lineState[line] = state;
        }

        public int GetLineState(int line)
        {
            if (line < 0 || line >= LinesTotal) return 0;
            return _lineState[line];
        }

        // ---- indicators (decorations) ----

        public void IndicatorFillRange(int indicator, int start, int len, int value)
        {
            _decorations.For(indicator).FillRange(start, len, value);
        }

        public void IndicatorClearRange(int indicator, int start, int len)
        {
            _decorations.For(indicator).ClearRange(start, len);
        }

        public int IndicatorValueAt(int indicator, int pos)
        {
            Decoration d = _decorations.Find(indicator);
            return d != null ? d.ValueAt(pos) : 0;
        }

        public int IndicatorStart(int indicator, int pos)
        {
            Decoration d = _decorations.Find(indicator);
            return d != null ? d.StartOfRun(pos) : pos;
        }

        public int IndicatorEnd(int indicator, int pos)
        {
            Decoration d = _decorations.Find(indicator);
            return d != null ? d.EndOfRun(pos) : pos;
        }

        public int IndicatorsAllOnFor(int pos)
        {
            return _decorations.AllOnFor(pos);
        }

        public Decorations Indicators
        {
            get { return _decorations; }
        }

        // ---- undo ----

        public void SetUndoCollection(bool collecting)
        {
            _cb.SetUndoCollection(collecting);
        }

        public void BeginUndoAction()
        {
            _cb.BeginUndoAction();
        }

        public void EndUndoAction()
        {
            _cb.EndUndoAction();
        }

        public bool CanUndo
        {
            get { return _cb.CanUndo; }
        }

        public bool CanRedo
        {
            get { return _cb.CanRedo; }
        }

        public void DeleteUndoHistory()
        {
            _cb.DeleteUndoHistory();
        }

        public void SetSavePoint()
        {
            _cb.SetSavePoint();
        }

        public bool IsSavePoint
        {
            get { return _cb.IsSavePoint; }
        }

        public int Undo()
        {
            int caret = _cb.Undo();
            AfterUndoRedo();
            return caret;
        }

        public int Redo()
        {
            int caret = _cb.Redo();
            AfterUndoRedo();
            return caret;
        }

        private void AfterUndoRedo()
        {
            // The buffer changed shape without going through Insert/Delete: reset the
            // style array to match and mark everything dirty for a full re-lex.
            _styles.Clear();
            _styles.InsertValue(0, Length, 0);
            ResizeLineData();
            _dirtyStart = 0;
            var handler = TextModified;
            if (handler != null) handler(0, Length, true);
        }

        private void ResizeLineData()
        {
            int n = LinesTotal;
            while (_markers.Count > n) _markers.RemoveAt(_markers.Count - 1);
            while (_markers.Count < n) _markers.Add(0);
            while (_levels.Count > n) _levels.RemoveAt(_levels.Count - 1);
            while (_levels.Count < n) _levels.Add(FoldLevelBase);
            while (_lineState.Count > n) _lineState.RemoveAt(_lineState.Count - 1);
            while (_lineState.Count < n) _lineState.Add(0);
        }
    }
}
