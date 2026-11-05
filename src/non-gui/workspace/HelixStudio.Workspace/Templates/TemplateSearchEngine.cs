using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Templates
{
    /// <summary>Active facet filters for the template picker (SRS §4 "Filtering"). Null/empty = no constraint.</summary>
    public sealed class TemplateFilter
    {
        public string Language;
        public string Platform;
        public string ProjectType;
        public string Framework;
        public string Category;

        public bool Matches(ProjectTemplate t)
        {
            return Ok(Language, t.Language) && Ok(Platform, t.Platform) && Ok(ProjectType, t.ProjectType)
                && Ok(Framework, t.Framework) && Ok(Category, t.Category);
        }
        private static bool Ok(string want, string have)
        {
            return string.IsNullOrEmpty(want) || string.Equals(want, have, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>One search hit: the template and its relevance score (higher = better).</summary>
    public sealed class TemplateHit
    {
        public ProjectTemplate Template;
        public int Score;
    }

    /// <summary>
    /// Search + rank templates (SRS §4 "Search Behavior"): substring, acronym (cmd -> CUDA Molecular Dynamics),
    /// tag, language/framework, and a light fuzzy (subsequence) match, combined with facet filters. Pure and
    /// deterministic so the picker is testable without the UI (SRS §14). An empty query returns all filtered
    /// templates, name-ordered — the default browse view.
    /// </summary>
    public static class TemplateSearchEngine
    {
        public static List<TemplateHit> Search(TemplateCatalog catalog, string query, TemplateFilter filter = null)
        {
            var hits = new List<TemplateHit>();
            string q = (query ?? "").Trim().ToLowerInvariant();
            foreach (var t in catalog.Templates)
            {
                if (filter != null && !filter.Matches(t)) continue;
                int score = q.Length == 0 ? 1 : ScoreOne(t, q);
                if (score > 0) hits.Add(new TemplateHit { Template = t, Score = score });
            }
            // Rank by score desc, then name asc for stable ordering.
            hits.Sort((a, b) =>
            {
                if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
                return string.Compare(a.Template.Name, b.Template.Name, System.StringComparison.OrdinalIgnoreCase);
            });
            return hits;
        }

        private static int ScoreOne(ProjectTemplate t, string q)
        {
            int score = 0;
            string name = t.Name.ToLowerInvariant();
            string lang = t.Language.ToLowerInvariant();
            string fw = t.Framework.ToLowerInvariant();

            if (name == q) score += 100;
            else if (name.StartsWith(q)) score += 60;
            else if (name.Contains(q)) score += 40;

            if (lang == q || lang.Replace(" c", "").Replace("#", "") == q) score += 45;   // "cuda"/"c#" language hits
            else if (lang.Contains(q)) score += 25;
            if (fw.Contains(q)) score += 20;

            if (t.Acronym() == q) score += 55;                                            // "cmd" -> CUDA Molecular Dynamics
            foreach (string tag in t.Tags) if (tag.ToLowerInvariant() == q) { score += 30; break; }

            if (score == 0 && t.SearchCorpus().Contains(q)) score += 15;                  // description/author/platform
            // Light fuzzy fallback: query as a subsequence of the NAME (spaces removed), e.g. "cudcon" -> "CUDA Console".
            // Matching against the whole corpus is too permissive (almost any short query subsequences some description).
            if (score == 0 && q.Length >= 3 && IsSubsequence(q, name.Replace(" ", ""))) score += 5;
            return score;
        }

        /// <summary>True if every char of <paramref name="q"/> appears in <paramref name="text"/> in order.</summary>
        internal static bool IsSubsequence(string q, string text)
        {
            int i = 0;
            for (int j = 0; j < text.Length && i < q.Length; j++) if (text[j] == q[i]) i++;
            return i == q.Length;
        }
    }
}
