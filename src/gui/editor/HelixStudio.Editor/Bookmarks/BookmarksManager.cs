namespace HelixPythonNamespace.Editor.Bookmarks
{
    using System;
    using System.Collections.Generic;

    /// <summary>One bookmarked line. Port of the C++ <c>BookmarkEntry</c>.</summary>
    public sealed class BookmarkEntry
    {
        public string FilePath = "";
        public int Line;        // 1-based
        public string Label = "";
    }

    /// <summary>
    /// Per-document bookmark state and navigation, decoupled from any editor control
    /// (the editor syncs Scintilla markers off these events). Port of the C++
    /// <c>BookmarksManager</c> with the Scintilla-marker coupling removed: callers pass
    /// the active file and 1-based line. <c>Next</c>/<c>Previous</c> wrap around.
    /// </summary>
    public sealed class BookmarksManager
    {
        public const int MarkerBookmark = 7;

        private readonly Dictionary<string, List<int>> _linesByFile = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        public event Action BookmarksChanged;

        /// <summary>Toggle the bookmark at (file, line). Returns the new present state.</summary>
        public bool Toggle(string filePath, int line)
        {
            bool present = !IsBookmarked(filePath, line);
            Remember(filePath, line, present);
            Action h = BookmarksChanged;
            if (h != null) h();
            return present;
        }

        public bool IsBookmarked(string filePath, int line)
        {
            List<int> vec;
            return _linesByFile.TryGetValue(filePath, out vec) && vec.Contains(line);
        }

        public IReadOnlyList<int> LinesFor(string filePath)
        {
            List<int> vec;
            return _linesByFile.TryGetValue(filePath, out vec) ? (IReadOnlyList<int>)vec : new List<int>();
        }

        /// <summary>Next bookmark line strictly after <paramref name="currentLine"/>, wrapping to the first. -1 when none.</summary>
        public int Next(string filePath, int currentLine)
        {
            List<int> vec;
            if (!_linesByFile.TryGetValue(filePath, out vec) || vec.Count == 0) return -1;
            foreach (int l in vec) if (l > currentLine) return l;
            return vec[0]; // wrap
        }

        /// <summary>Previous bookmark line strictly before <paramref name="currentLine"/>, wrapping to the last. -1 when none.</summary>
        public int Previous(string filePath, int currentLine)
        {
            List<int> vec;
            if (!_linesByFile.TryGetValue(filePath, out vec) || vec.Count == 0) return -1;
            for (int i = vec.Count - 1; i >= 0; i--) if (vec[i] < currentLine) return vec[i];
            return vec[vec.Count - 1]; // wrap
        }

        public List<BookmarkEntry> All()
        {
            List<BookmarkEntry> outList = new List<BookmarkEntry>();
            foreach (KeyValuePair<string, List<int>> kv in _linesByFile)
                foreach (int line in kv.Value)
                    outList.Add(new BookmarkEntry { FilePath = kv.Key, Line = line });
            outList.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.FilePath, b.FilePath);
                return c != 0 ? c : a.Line.CompareTo(b.Line);
            });
            return outList;
        }

        /// <summary>Replace the bookmark set for a file (e.g. when restoring persisted state).</summary>
        public void SetLines(string filePath, IEnumerable<int> lines)
        {
            List<int> vec = new List<int>(lines);
            vec.Sort();
            _linesByFile[filePath] = vec;
            Action h = BookmarksChanged;
            if (h != null) h();
        }

        private void Remember(string filePath, int line, bool present)
        {
            List<int> vec;
            if (!_linesByFile.TryGetValue(filePath, out vec)) { vec = new List<int>(); _linesByFile[filePath] = vec; }
            int idx = vec.IndexOf(line);
            if (present) { if (idx < 0) vec.Add(line); }
            else { if (idx >= 0) vec.RemoveAt(idx); }
            vec.Sort();
        }
    }
}
