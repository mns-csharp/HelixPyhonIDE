using System;
using System.Collections.Generic;

namespace HelixTextEditorNET.Rendering
{
    /// <summary>
    /// Word-wrap break points: where each document line is split so it fits the text area.
    ///
    /// Wrapping is a VIEW concern only — the document is never modified and no newline is
    /// inserted. A wrapped document line simply occupies several display rows, which is the same
    /// shape the port already supports for annotations (<c>ContractionState.AnnotationGetLines</c>),
    /// so folding, markers and the display/document line mapping continue to work unchanged.
    ///
    /// Results are cached per line and thrown away whenever the wrap width, tab width or document
    /// version changes — measuring every line on every paint would make scrolling quadratic in
    /// file size.
    /// </summary>
    internal sealed class WrapLayout
    {
        private readonly Dictionary<int, int[]> _breaks = new Dictionary<int, int[]>();
        private double _width = -1;
        private int _tabWidth = -1;
        private int _version = -1;
        private bool _enabled;

        /// <summary>Whether wrapping is on. Turning it off empties the cache.</summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                _breaks.Clear();
            }
        }

        /// <summary>
        /// Drop cached break points when anything that changes them changes. Called before each
        /// paint; a width change of less than a pixel is ignored so ordinary repaints are free.
        /// </summary>
        public void Sync(double wrapWidth, int tabWidth, int documentVersion)
        {
            if (Math.Abs(wrapWidth - _width) > 0.5 || tabWidth != _tabWidth || documentVersion != _version)
            {
                _width = wrapWidth;
                _tabWidth = tabWidth;
                _version = documentVersion;
                _breaks.Clear();
            }
        }

        /// <summary>
        /// Start offsets of each display row of <paramref name="line"/>, relative to the line
        /// start. Always begins with 0; a line that fits returns a single element, which is the
        /// unwrapped case and costs nothing.
        /// </summary>
        public int[] BreaksFor(int line, string text, Func<string, double> measure)
        {
            if (!_enabled || _width <= 0 || string.IsNullOrEmpty(text))
                return SingleRow;

            int[] cached;
            if (_breaks.TryGetValue(line, out cached)) return cached;

            List<int> starts = new List<int> { 0 };
            int rowStart = 0;
            int lastBreakable = -1;

            for (int i = 0; i < text.Length; i++)
            {
                // Remember the last point we could break at, so a long line breaks between words
                // rather than mid-word whenever that is possible.
                if (IsBreakable(text[i])) lastBreakable = i;

                if (measure(text.Substring(rowStart, i - rowStart + 1)) <= _width) continue;

                // Break AFTER the last breakable character so trailing spaces stay on the row
                // that ends with them, matching how editors normally wrap.
                int cut = (lastBreakable > rowStart) ? lastBreakable + 1 : i;
                if (cut <= rowStart) cut = rowStart + 1;      // never make an empty row
                starts.Add(cut);
                rowStart = cut;
                lastBreakable = -1;
            }

            int[] result = starts.ToArray();
            _breaks[line] = result;
            return result;
        }

        /// <summary>Display rows occupied by a line (1 when it fits, or wrapping is off).</summary>
        public int RowsFor(int line, string text, Func<string, double> measure)
        {
            return BreaksFor(line, text, measure).Length;
        }

        /// <summary>The row index within the line that contains <paramref name="offset"/>.</summary>
        public static int RowOfOffset(int[] breaks, int offset)
        {
            for (int r = breaks.Length - 1; r >= 0; r--)
                if (offset >= breaks[r]) return r;
            return 0;
        }

        private static readonly int[] SingleRow = { 0 };

        // Break after whitespace, and after punctuation that conventionally ends a token, so a
        // long path or dotted name breaks at a separator instead of an arbitrary character.
        private static bool IsBreakable(char c)
        {
            return c == ' ' || c == '\t' || c == '-' || c == ',' || c == ';'
                || c == '/' || c == '\\' || c == ')' || c == ']' || c == '}';
        }
    }
}
