using System;
using System.Collections.Generic;

namespace HelixTextEditorNET.Core
{
    // One indicator's value-runs over the document: a sorted, non-overlapping list
    // of [start,end) ranges each carrying an integer value. Port of decoration.h.
    internal sealed class Decoration
    {
        public struct Range
        {
            public int Start;
            public int End;
            public int Value;

            public Range(int start, int end, int value)
            {
                Start = start;
                End = end;
                Value = value;
            }
        }

        private List<Range> _ranges = new List<Range>();

        public bool Empty
        {
            get { return _ranges.Count == 0; }
        }

        public List<Range> Ranges
        {
            get { return _ranges; }
        }

        // Clear any decoration in [start, start+len), then set it to `value`.
        public void FillRange(int start, int len, int value)
        {
            int end = start + len;
            if (end <= start || value == 0)
            {
                ClearRange(start, len);
                return;
            }
            ClearRange(start, len);
            _ranges.Add(new Range(start, end, value));
            _ranges.Sort(delegate (Range a, Range b) { return a.Start.CompareTo(b.Start); });
            Merge();
        }

        // Remove any decoration overlapping [start, start+len), splitting runs that
        // straddle the boundary.
        public void ClearRange(int start, int len)
        {
            int end = start + len;
            if (end <= start) return;
            var outRanges = new List<Range>(_ranges.Count);
            foreach (Range r in _ranges)
            {
                if (r.End <= start || r.Start >= end)
                {
                    outRanges.Add(r);
                    continue;
                }
                if (r.Start < start) outRanges.Add(new Range(r.Start, start, r.Value));
                if (r.End > end) outRanges.Add(new Range(end, r.End, r.Value));
            }
            _ranges = outRanges;
        }

        public int ValueAt(int pos)
        {
            foreach (Range r in _ranges)
                if (pos >= r.Start && pos < r.End) return r.Value;
            return 0;
        }

        // Start/end of the value run containing pos (SCI_INDICATORSTART/END).
        public int StartOfRun(int pos)
        {
            foreach (Range r in _ranges)
                if (pos >= r.Start && pos < r.End) return r.Start;
            return pos;
        }

        public int EndOfRun(int pos)
        {
            foreach (Range r in _ranges)
                if (pos >= r.Start && pos < r.End) return r.End;
            return pos;
        }

        // Keep ranges consistent across document edits.
        public void InsertSpace(int pos, int len)
        {
            for (int i = 0; i < _ranges.Count; i++)
            {
                Range r = _ranges[i];
                if (r.Start >= pos) r.Start += len;
                if (r.End > pos) r.End += len;
                _ranges[i] = r;
            }
        }

        public void DeleteSpace(int pos, int len)
        {
            int end = pos + len;
            var outRanges = new List<Range>(_ranges.Count);
            foreach (Range range in _ranges)
            {
                Range r = range;
                // Clip the deleted span out of the range.
                if (r.Start >= end) r.Start -= len;
                else if (r.Start > pos) r.Start = pos;
                if (r.End >= end) r.End -= len;
                else if (r.End > pos) r.End = pos;
                if (r.End > r.Start) outRanges.Add(r);
            }
            _ranges = outRanges;
        }

        private void Merge()
        {
            var outRanges = new List<Range>();
            foreach (Range r in _ranges)
            {
                if (outRanges.Count > 0)
                {
                    Range last = outRanges[outRanges.Count - 1];
                    if (last.Value == r.Value && last.End >= r.Start)
                    {
                        last.End = Math.Max(last.End, r.End);
                        outRanges[outRanges.Count - 1] = last;
                        continue;
                    }
                }
                outRanges.Add(r);
            }
            _ranges = outRanges;
        }
    }

    // All indicators (lazily created per indicator number). Port of Decorations.
    internal sealed class Decorations
    {
        private readonly SortedDictionary<int, Decoration> _map = new SortedDictionary<int, Decoration>();

        public Decoration For(int indicator)
        {
            Decoration d;
            if (!_map.TryGetValue(indicator, out d))
            {
                d = new Decoration();
                _map[indicator] = d;
            }
            return d;
        }

        public Decoration Find(int indicator)
        {
            Decoration d;
            return _map.TryGetValue(indicator, out d) ? d : null;
        }

        public SortedDictionary<int, Decoration> All
        {
            get { return _map; }
        }

        // Bitmask of indicators with a non-zero value at pos (SCI_INDICATORALLONFOR).
        public int AllOnFor(int pos)
        {
            int mask = 0;
            foreach (KeyValuePair<int, Decoration> kv in _map)
                if (kv.Key < 32 && kv.Value.ValueAt(pos) != 0) mask |= (1 << kv.Key);
            return mask;
        }

        public void InsertSpace(int pos, int len)
        {
            foreach (KeyValuePair<int, Decoration> kv in _map) kv.Value.InsertSpace(pos, len);
        }

        public void DeleteSpace(int pos, int len)
        {
            foreach (KeyValuePair<int, Decoration> kv in _map) kv.Value.DeleteSpace(pos, len);
        }
    }
}
