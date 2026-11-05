namespace HelixPythonNamespace.Editor.Navigation
{
    using System.Collections.Generic;

    /// <summary>
    /// Whole-word, case-sensitive identifier occurrence finder. Port of the C++
    /// <c>occurrence_matcher</c>. Walks the buffer once, only ever comparing full
    /// identifier runs — so whole-word matching is implicit (never a substring of a
    /// longer name).
    /// </summary>
    public static class OccurrenceMatcher
    {
        public struct Match
        {
            public int Start;  // inclusive
            public int End;    // exclusive
            public Match(int start, int end) { Start = start; End = end; }
        }

        public static bool IsIdentifierStart(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
        }

        public static bool IsIdentifierContinue(char c)
        {
            return IsIdentifierStart(c) || (c >= '0' && c <= '9');
        }

        public static List<Match> FindAll(string haystack, string needle)
        {
            List<Match> matches = new List<Match>();
            if (string.IsNullOrEmpty(needle) || haystack == null || haystack.Length < needle.Length) return matches;
            if (!IsIdentifierStart(needle[0])) return matches;

            int n = haystack.Length;
            int i = 0;
            while (i < n)
            {
                while (i < n && !IsIdentifierStart(haystack[i])) i++;
                if (i >= n) break;

                int start = i;
                while (i < n && IsIdentifierContinue(haystack[i])) i++;
                int end = i;

                if (end - start == needle.Length && string.CompareOrdinal(haystack, start, needle, 0, needle.Length) == 0)
                {
                    matches.Add(new Match(start, end));
                }
            }
            return matches;
        }
    }
}
