using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// Ranks completion items against a typed prefix. Tier encodes match-quality
    /// (0 = best, 5 = rejected); subscore breaks ties (smaller = better).
    /// Mirrors VS 2017 IntelliSense ordering intent: exact-case prefix &gt; case-
    /// insensitive prefix &gt; CamelCase abbreviation &gt; substring &gt; fuzzy.
    /// </summary>
    public static class CompletionRanker
    {
        public struct Score
        {
            public int Tier;       // 0..4 include; 5 = rejected
            public int Subscore;   // tie-breaker, lower is better
            public bool Accepted { get { return Tier < 5; } }
        }

        public static Score ScoreItem(string prefix, string label)
        {
            if (string.IsNullOrEmpty(prefix))
                return new Score { Tier = 0, Subscore = (label ?? string.Empty).Length };
            if (string.IsNullOrEmpty(label))
                return new Score { Tier = 5 };

            // Tier 0: exact-case prefix.
            if (label.StartsWith(prefix, StringComparison.Ordinal))
                return new Score { Tier = 0, Subscore = label.Length };

            // Tier 1: case-insensitive prefix.
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new Score { Tier = 1, Subscore = label.Length };

            // Tier 2: CamelCase abbreviation (every prefix char matches a capital/boundary in label).
            if (IsCamelCaseMatch(prefix, label))
                return new Score { Tier = 2, Subscore = label.Length };

            // Tier 3: contiguous substring, case-insensitive.
            int idx = label.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return new Score { Tier = 3, Subscore = idx * 100 + label.Length };

            // Tier 4: fuzzy — every char of prefix appears in order in label.
            if (IsFuzzyMatch(prefix, label))
                return new Score { Tier = 4, Subscore = label.Length };

            return new Score { Tier = 5 };
        }

        public static List<CompletionItem> FilterAndRank(
            IEnumerable<CompletionItem> items, string prefix)
        {
            var kept = new List<(Score score, CompletionItem item)>();
            foreach (var it in items)
            {
                var s = ScoreItem(prefix, it.Label);
                if (s.Accepted) kept.Add((s, it));
            }
            kept.Sort((a, b) =>
            {
                int c = a.score.Tier.CompareTo(b.score.Tier);
                if (c != 0) return c;
                c = a.score.Subscore.CompareTo(b.score.Subscore);
                if (c != 0) return c;
                return string.Compare(a.item.Label, b.item.Label, StringComparison.Ordinal);
            });
            var result = new List<CompletionItem>(kept.Count);
            foreach (var k in kept) result.Add(k.item);
            return result;
        }

        private static bool IsCamelCaseMatch(string prefix, string label)
        {
            int pi = 0;
            // Must match the first char of a "word" run — uppercase letters or the first char
            // after an underscore / start.
            for (int li = 0; li < label.Length && pi < prefix.Length; li++)
            {
                bool isBoundary = li == 0
                    || char.IsUpper(label[li])
                    || (li > 0 && label[li - 1] == '_');
                if (!isBoundary) continue;
                if (char.ToLowerInvariant(label[li]) == char.ToLowerInvariant(prefix[pi]))
                    pi++;
            }
            return pi == prefix.Length;
        }

        private static bool IsFuzzyMatch(string prefix, string label)
        {
            int pi = 0;
            for (int li = 0; li < label.Length && pi < prefix.Length; li++)
            {
                if (char.ToLowerInvariant(label[li]) == char.ToLowerInvariant(prefix[pi]))
                    pi++;
            }
            return pi == prefix.Length;
        }
    }
}
