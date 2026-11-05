using System;

namespace HelixTextEditorNET.Core
{
    // A vector with a movable "gap" at the current insertion point, giving O(1)
    // amortized insert/delete near the gap — the core container Scintilla uses for
    // the text buffer and per-character style bytes. Port of core/split_vector.h.
    internal sealed class SplitVector<T>
    {
        private T[] _body = new T[0];
        private int _lengthBody;   // logical element count (excludes the gap)
        private int _part1Length;  // elements before the gap (== gap position)
        private int _gapLength;    // size of the gap

        public int Length
        {
            get { return _lengthBody; }
        }

        public T ValueAt(int position)
        {
            if (position < 0 || position >= _lengthBody) return default(T);
            if (position < _part1Length) return _body[position];
            return _body[position + _gapLength];
        }

        public void SetValueAt(int position, T v)
        {
            if (position < 0 || position >= _lengthBody) return;
            if (position < _part1Length) _body[position] = v;
            else _body[position + _gapLength] = v;
        }

        public T this[int position]
        {
            get { return ValueAt(position); }
        }

        // Bulk-set `count` elements from `position` to `value` across the two split
        // halves — far cheaper than a per-element SetValueAt loop (the per-token
        // styling hot path).
        public void FillRange(int position, int count, T value)
        {
            if (count <= 0 || position < 0 || position >= _lengthBody) return;
            int end = Math.Min(position + count, _lengthBody);
            int p1end = Math.Min(end, _part1Length);
            for (int i = position; i < p1end; i++) _body[i] = value;
            int p2begin = Math.Max(position, _part1Length);
            for (int i = p2begin; i < end; i++) _body[i + _gapLength] = value;
        }

        public void Clear()
        {
            _body = new T[0];
            _lengthBody = 0;
            _part1Length = 0;
            _gapLength = 0;
        }

        // Insert `count` copies of `value` at `position`.
        public void InsertValue(int position, int count, T value)
        {
            if (count <= 0) return;
            RoomFor(count);
            GapTo(position);
            for (int i = 0; i < count; i++) _body[_part1Length + i] = value;
            _lengthBody += count;
            _part1Length += count;
            _gapLength -= count;
        }

        // Insert `count` elements from `source` starting at `positionFrom`.
        public void InsertFromArray(int positionToInsert, T[] source, int positionFrom, int count)
        {
            if (count <= 0) return;
            RoomFor(count);
            GapTo(positionToInsert);
            Array.Copy(source, positionFrom, _body, _part1Length, count);
            _lengthBody += count;
            _part1Length += count;
            _gapLength -= count;
        }

        // Delete `count` elements starting at `position`.
        public void DeleteRange(int position, int deleteLength)
        {
            if (deleteLength <= 0) return;
            if (position < 0 || position + deleteLength > _lengthBody) return;
            if (position == _part1Length)
            {
                // Deletion just after the gap: grow the gap forward.
                _gapLength += deleteLength;
            }
            else if (position + deleteLength == _part1Length)
            {
                // Deletion just before the gap: shrink part1.
                _part1Length -= deleteLength;
                _gapLength += deleteLength;
            }
            else
            {
                GapTo(position);
                _gapLength += deleteLength;
            }
            _lengthBody -= deleteLength;
        }

        // Copy a logical range out into `buffer`.
        public void GetRange(T[] buffer, int position, int retrieveLength)
        {
            if (retrieveLength <= 0) return;
            int end = Math.Min(position + retrieveLength, _lengthBody);
            int p1end = Math.Min(end, _part1Length);
            int written = 0;
            if (position < p1end)
            {
                Array.Copy(_body, position, buffer, 0, p1end - position);
                written = p1end - position;
            }
            int p2begin = Math.Max(position, _part1Length);
            if (p2begin < end)
            {
                Array.Copy(_body, p2begin + _gapLength, buffer, written, end - p2begin);
            }
        }

        private void GapTo(int position)
        {
            if (position == _part1Length) return;
            if (position < _part1Length)
            {
                // Move gap left: shift elements [position, part1Length) right by gap.
                Array.Copy(_body, position, _body, position + _gapLength, _part1Length - position);
            }
            else
            {
                // Move gap right: shift elements after the gap left.
                Array.Copy(_body, _part1Length + _gapLength, _body, _part1Length, position - _part1Length);
            }
            _part1Length = position;
        }

        private void RoomFor(int insertionLength)
        {
            if (_gapLength >= insertionLength) return;
            // Grow: move gap to the end, then enlarge with headroom.
            GapTo(_lengthBody);
            int newGap = Math.Max(insertionLength + _lengthBody / 4 + 8, 16);
            Array.Resize(ref _body, _lengthBody + newGap);
            _gapLength = newGap;
        }
    }
}
