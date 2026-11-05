namespace HelixPythonNamespace.Terminal.Shell.Suggestions;

/// <summary>How well typed letters line up with a suggestion; a higher value is a better match.</summary>
public enum MatchClass
{
    Other = 1,
    WordStart = 2,
    Prefix = 3,
    Exact = 4,
}

public sealed record FuzzyMatch(MatchClass Class, double Score, int[] Positions);

/// <summary>
/// Letters typed in order, gaps allowed, ignoring case. The class says how the letters line up —
/// the whole thing, the start of it, the starts of its words, or anywhere — and the score orders
/// matches inside a class: fewer and shorter gaps first, then an earlier first letter.
/// </summary>
public static class FuzzyMatcher
{
    public static FuzzyMatch? Match(string candidate, string typed)
    {
        if (typed.Length == 0) return new FuzzyMatch(MatchClass.Prefix, 0, Array.Empty<int>());
        if (typed.Length > candidate.Length) return null;

        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            var positions = Enumerable.Range(0, typed.Length).ToArray();
            var matchClass = candidate.Length == typed.Length ? MatchClass.Exact : MatchClass.Prefix;
            return new FuzzyMatch(matchClass, 0, positions);
        }

        var wordStarts = MatchWordStarts(candidate, typed);
        if (wordStarts is not null) return new FuzzyMatch(MatchClass.WordStart, Score(wordStarts), wordStarts);

        var anywhere = MatchAnywhere(candidate, typed);
        return anywhere is null ? null : new FuzzyMatch(MatchClass.Other, Score(anywhere), anywhere);
    }

    /// <summary>
    /// A word begins at the start, after a separator (<c>- _ . / \</c> or a space), or where a
    /// lower-case letter is followed by a capital, as in <c>SuggestionEngine</c>.
    /// </summary>
    public static bool IsWordStart(string text, int index) =>
        index == 0 ||
        text[index - 1] is '-' or '_' or '.' or '/' or '\\' or ' ' ||
        (char.IsUpper(text[index]) && char.IsLower(text[index - 1]));

    /// <summary>Each letter must either follow the previous match directly or start a word.</summary>
    private static int[]? MatchWordStarts(string candidate, string typed)
    {
        var positions = new int[typed.Length];
        var next = 0;

        for (var t = 0; t < typed.Length; t++)
        {
            var found = -1;

            if (t > 0 && next < candidate.Length && SameLetter(candidate[next], typed[t]))
            {
                found = next;
            }
            else
            {
                for (var i = next; i < candidate.Length; i++)
                {
                    if (!IsWordStart(candidate, i) || !SameLetter(candidate[i], typed[t])) continue;
                    found = i;
                    break;
                }
            }

            if (found < 0) return null;

            positions[t] = found;
            next = found + 1;
        }

        return positions;
    }

    private static int[]? MatchAnywhere(string candidate, string typed)
    {
        var positions = new int[typed.Length];
        var next = 0;

        for (var t = 0; t < typed.Length; t++)
        {
            var found = -1;
            for (var i = next; i < candidate.Length; i++)
            {
                if (!SameLetter(candidate[i], typed[t])) continue;
                found = i;
                break;
            }

            if (found < 0) return null;

            positions[t] = found;
            next = found + 1;
        }

        return positions;
    }

    private static double Score(int[] positions)
    {
        var gaps = 0;
        for (var i = 1; i < positions.Length; i++) gaps += positions[i] - positions[i - 1] - 1;
        return -gaps - positions[0] * 0.1;
    }

    private static bool SameLetter(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
