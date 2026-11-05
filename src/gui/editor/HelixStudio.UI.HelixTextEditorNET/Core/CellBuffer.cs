using System;
using System.Collections.Generic;

namespace HelixTextEditorNET.Core
{
    // A single undoable edit. Port of cell_buffer.h UndoAction.
    internal struct UndoAction
    {
        public enum ActionType { Insert, Remove }

        public ActionType Type;
        public int Position;
        public string Text;
        public bool StartsGroup;  // first action of an undo group
    }

    // Linear undo/redo history of insert/remove actions, with begin/end grouping.
    internal sealed class UndoHistory
    {
        private readonly List<UndoAction> _actions = new List<UndoAction>();
        private int _currentAction;  // index of next action to (re)do
        private int _savePoint;
        private int _groupDepth;
        private bool _groupStartPending;

        public void AppendAction(UndoAction.ActionType type, int pos, string text)
        {
            // Truncate any redo history.
            if (_currentAction < _actions.Count)
                _actions.RemoveRange(_currentAction, _actions.Count - _currentAction);
            var a = new UndoAction
            {
                Type = type,
                Position = pos,
                Text = text,
                StartsGroup = _groupStartPending || _groupDepth == 0
            };
            _groupStartPending = false;
            _actions.Add(a);
            _currentAction = _actions.Count;
            if (_savePoint > _currentAction) _savePoint = -1;  // save point lost in truncated redo
        }

        public void BeginUndoAction()
        {
            if (_groupDepth == 0) _groupStartPending = true;
            ++_groupDepth;
        }

        public void EndUndoAction()
        {
            if (_groupDepth > 0) --_groupDepth;
        }

        public bool CanUndo
        {
            get { return _currentAction > 0; }
        }

        public bool CanRedo
        {
            get { return _currentAction < _actions.Count; }
        }

        // Step back one group; returns the actions in the order to reverse each
        // effect (newest-first). Empty if nothing to do.
        public List<UndoAction> Undo()
        {
            var group = new List<UndoAction>();
            if (!CanUndo) return group;
            do
            {
                --_currentAction;
                group.Add(_actions[_currentAction]);
            } while (_currentAction > 0 && !_actions[_currentAction].StartsGroup);
            return group;
        }

        // Step forward one group; returns the actions oldest-first (re-apply order).
        public List<UndoAction> Redo()
        {
            var group = new List<UndoAction>();
            if (!CanRedo) return group;
            do
            {
                group.Add(_actions[_currentAction]);
                ++_currentAction;
            } while (_currentAction < _actions.Count && !_actions[_currentAction].StartsGroup);
            return group;
        }

        public void DeleteUndoHistory()
        {
            _actions.Clear();
            _currentAction = 0;
            _savePoint = 0;
            _groupDepth = 0;
            _groupStartPending = false;
        }

        public void SetSavePoint()
        {
            _savePoint = _currentAction;
        }

        public bool IsSavePoint
        {
            get { return _savePoint == _currentAction; }
        }
    }

    // Document text storage: characters in a SplitVector plus a line-start index and
    // an undo history. Port of cell_buffer.{h,cpp}. Styles are tracked separately in
    // Document.
    internal sealed class CellBuffer
    {
        public const int InvalidPosition = -1;

        private readonly SplitVector<char> _substance = new SplitVector<char>();
        private readonly List<int> _lineStarts = new List<int>();  // [i] = start offset of line i
        private readonly UndoHistory _undo = new UndoHistory();
        private bool _collectingUndo = true;

        // Fired with the EXACT (line index, count) a line insert/delete happened at — the
        // authoritative source every other per-line array (DocumentModel's markers/fold-levels/
        // line-state, ContractionState's expanded/visible/annotation rows) needs to shift
        // correctly. Without this, consumers were resizing by comparing old/new total line count
        // and truncating/appending only at the END of their own lists — correct for edits at the
        // end of the file, silently wrong for edits anywhere else: a line inserted/deleted above
        // existing per-line state (fold levels, breakpoints, annotations, collapsed regions)
        // left ALL of it associated with the wrong (shifted) line index from that point on.
        public event Action<int, int> LinesInserted;   // (atLine, count)
        public event Action<int, int> LinesDeleted;    // (atLine, count)

        public CellBuffer()
        {
            _lineStarts.Add(0);
        }

        public int Length
        {
            get { return _substance.Length; }
        }

        public char CharAt(int position)
        {
            return _substance.ValueAt(position);
        }

        public void GetCharRange(char[] buffer, int position, int lengthRetrieve)
        {
            _substance.GetRange(buffer, position, lengthRetrieve);
        }

        public string GetText(int position, int lengthRetrieve)
        {
            if (lengthRetrieve <= 0) return string.Empty;
            var buffer = new char[lengthRetrieve];
            _substance.GetRange(buffer, position, lengthRetrieve);
            return new string(buffer);
        }

        public int Lines
        {
            get { return _lineStarts.Count; }
        }

        public int LineStart(int line)
        {
            if (line < 0) return 0;
            if (line >= Lines) return Length;
            return _lineStarts[line];
        }

        public int LineFromPosition(int pos)
        {
            if (pos <= 0) return 0;
            if (pos >= Length) return Lines - 1;
            // Binary search for the greatest start <= pos: UpperBound(pos) - 1.
            int idx = UpperBound(pos);
            return idx - 1;
        }

        // Index of the first line start strictly greater than pos.
        private int UpperBound(int pos)
        {
            int lo = 0, hi = _lineStarts.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_lineStarts[mid] <= pos) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        public void SetUndoCollection(bool collecting)
        {
            _collectingUndo = collecting;
        }

        public bool IsCollectingUndo
        {
            get { return _collectingUndo; }
        }

        public void BeginUndoAction()
        {
            _undo.BeginUndoAction();
        }

        public void EndUndoAction()
        {
            _undo.EndUndoAction();
        }

        public bool CanUndo
        {
            get { return _undo.CanUndo; }
        }

        public bool CanRedo
        {
            get { return _undo.CanRedo; }
        }

        public void DeleteUndoHistory()
        {
            _undo.DeleteUndoHistory();
        }

        public void SetSavePoint()
        {
            _undo.SetSavePoint();
        }

        public bool IsSavePoint
        {
            get { return _undo.IsSavePoint; }
        }

        public void InsertString(int position, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (_collectingUndo)
                _undo.AppendAction(UndoAction.ActionType.Insert, position, s);
            BasicInsertString(position, s);
        }

        public void DeleteChars(int position, int deleteLength)
        {
            if (deleteLength <= 0) return;
            if (_collectingUndo)
            {
                string removed = GetText(position, deleteLength);
                _undo.AppendAction(UndoAction.ActionType.Remove, position, removed);
            }
            BasicDeleteChars(position, deleteLength);
        }

        // Apply one undo group; returns the position to move the caret to (or
        // InvalidPosition if nothing happened).
        public int Undo()
        {
            List<UndoAction> group = _undo.Undo();
            int caret = InvalidPosition;
            foreach (UndoAction a in group)
            {
                if (a.Type == UndoAction.ActionType.Insert)
                {
                    // Reverse an insert by removing the inserted text.
                    BasicDeleteChars(a.Position, a.Text.Length);
                    caret = a.Position;
                }
                else
                {
                    // Reverse a remove by re-inserting.
                    BasicInsertString(a.Position, a.Text);
                    caret = a.Position + a.Text.Length;
                }
            }
            return caret;
        }

        public int Redo()
        {
            List<UndoAction> group = _undo.Redo();
            int caret = InvalidPosition;
            foreach (UndoAction a in group)
            {
                if (a.Type == UndoAction.ActionType.Insert)
                {
                    BasicInsertString(a.Position, a.Text);
                    caret = a.Position + a.Text.Length;
                }
                else
                {
                    BasicDeleteChars(a.Position, a.Text.Length);
                    caret = a.Position;
                }
            }
            return caret;
        }

        private void BasicInsertString(int position, string s)
        {
            int insertLength = s.Length;
            char[] chars = s.ToCharArray();
            _substance.InsertFromArray(position, chars, 0, insertLength);
            // Maintain the line index incrementally instead of rescanning the whole
            // buffer on every edit: shift existing starts after `position`, then
            // splice in starts for any newlines inside the inserted text.
            int firstAfter = UpperBound(position);
            int splitLine = firstAfter - 1;                                    // line containing `position`
            bool atLineStart = splitLine >= 0 && _lineStarts[splitLine] == position;
            for (int i = firstAfter; i < _lineStarts.Count; i++) _lineStarts[i] += insertLength;
            List<int> newStarts = null;
            for (int k = 0; k < insertLength; k++)
            {
                if (chars[k] == '\n')
                {
                    if (newStarts == null) newStarts = new List<int>();
                    newStarts.Add(position + k + 1);
                }
            }
            if (newStarts != null)
            {
                _lineStarts.InsertRange(firstAfter, newStarts);
                var h = LinesInserted;
                if (h != null)
                {
                    // Inserting exactly AT an existing line's start (not strictly inside it) is
                    // "whole new lines prepended before `splitLine`", not "`splitLine` split in
                    // two" — splitLine's own per-line state (fold expand/visible, breakpoints,
                    // etc.) belongs with its ORIGINAL content, which is now pushed down past the
                    // new lines, not with the (empty) new prefix. Report the insert at splitLine
                    // itself so callers push splitLine's existing state down by newStarts.Count,
                    // rather than at firstAfter (== splitLine+1), which would strand it on the
                    // new prefix instead. Strictly-inside-a-line splits keep the original
                    // firstAfter behavior — the leading fragment reasonably keeps the identity.
                    int atLine = atLineStart ? splitLine : firstAfter;
                    h(atLine, newStarts.Count);
                }
            }
        }

        private void BasicDeleteChars(int position, int deleteLength)
        {
            int end = position + deleteLength;
            _substance.DeleteRange(position, deleteLength);
            // Drop the line starts that fell inside (position, end] (their newlines
            // were deleted), then shift the rest down.
            int lo = UpperBound(position);
            int hi = UpperBound(end);
            for (int i = hi; i < _lineStarts.Count; i++) _lineStarts[i] -= deleteLength;
            if (hi > lo)
            {
                _lineStarts.RemoveRange(lo, hi - lo);
                var h = LinesDeleted;
                if (h != null) h(lo, hi - lo);
            }
        }
    }
}
