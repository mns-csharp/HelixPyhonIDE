using System.Text;

namespace HelixPythonNamespace.Terminal.Shell.Expansion;

/// <summary>
/// Shell glob patterns: <c>*</c>, <c>?</c>, <c>[...]</c> with ranges, negation and POSIX
/// character classes. A backslash escapes the following character.
/// </summary>
public static class ShellPattern
{
    public static bool IsMatch(string text, string pattern, bool ignoreCase = false) =>
        Match(text, 0, pattern, 0, ignoreCase);

    /// <summary>True when the pattern contains an unescaped metacharacter and therefore needs matching at all.</summary>
    public static bool HasMetacharacters(string pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\') { i++; continue; }
            if (pattern[i] is '*' or '?' or '[') return true;
        }

        return false;
    }

    /// <summary>Removes the backslash escapes from a pattern, yielding the literal text it denotes.</summary>
    public static string Unescape(string pattern)
    {
        var sb = new StringBuilder(pattern.Length);

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 1 < pattern.Length) i++;
            sb.Append(pattern[i]);
        }

        return sb.ToString();
    }

    /// <summary>Escapes every glob metacharacter so the text matches only itself.</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Length of the shortest (or longest) prefix of <paramref name="text"/> that the pattern
    /// matches, or -1 when there is none. Backs <c>${v#pat}</c> and <c>${v##pat}</c>.
    /// </summary>
    public static int MatchPrefixLength(string text, string pattern, bool longest)
    {
        if (longest)
        {
            for (var length = text.Length; length >= 0; length--)
                if (Match(text, 0, pattern, 0, false, length)) return length;
        }
        else
        {
            for (var length = 0; length <= text.Length; length++)
                if (Match(text, 0, pattern, 0, false, length)) return length;
        }

        return -1;
    }

    /// <summary>
    /// Length of the shortest (or longest) suffix the pattern matches, or -1.
    /// Backs <c>${v%pat}</c> and <c>${v%%pat}</c>.
    /// </summary>
    public static int MatchSuffixLength(string text, string pattern, bool longest)
    {
        if (longest)
        {
            for (var length = text.Length; length >= 0; length--)
                if (Match(text, text.Length - length, pattern, 0, false)) return length;
        }
        else
        {
            for (var length = 0; length <= text.Length; length++)
                if (Match(text, text.Length - length, pattern, 0, false)) return length;
        }

        return -1;
    }

    /// <summary>Finds the first substring matching the pattern; used by <c>${v/pat/rep}</c>.</summary>
    public static bool TryFindMatch(string text, string pattern, int startIndex, out int start, out int length)
    {
        for (var i = startIndex; i <= text.Length; i++)
        {
            // Prefer the longest match at the earliest position, mirroring bash.
            for (var len = text.Length - i; len >= 0; len--)
            {
                if (!Match(text, i, pattern, 0, false, i + len)) continue;
                start = i;
                length = len;
                return true;
            }
        }

        start = -1;
        length = 0;
        return false;
    }

    private static bool Match(string s, int si, string p, int pi, bool ignoreCase, int? limit = null)
    {
        var end = limit ?? s.Length;

        while (true)
        {
            if (pi >= p.Length) return si >= end;

            var pc = p[pi];

            if (pc == '\\' && pi + 1 < p.Length)
            {
                if (si >= end || !CharEquals(s[si], p[pi + 1], ignoreCase)) return false;
                si++;
                pi += 2;
                continue;
            }

            if (pc == '*')
            {
                // Collapse runs of '*' then try every split point.
                while (pi < p.Length && p[pi] == '*') pi++;
                if (pi >= p.Length) return true;

                for (var skip = si; skip <= end; skip++)
                    if (Match(s, skip, p, pi, ignoreCase, end)) return true;

                return false;
            }

            if (pc == '?')
            {
                if (si >= end) return false;
                si++;
                pi++;
                continue;
            }

            if (pc == '[')
            {
                if (si >= end) return false;
                if (!MatchBracket(s[si], p, ref pi, ignoreCase, out var ok))
                {
                    // Unterminated '[' is a literal '['.
                    if (!CharEquals(s[si], '[', ignoreCase)) return false;
                    si++;
                    pi++;
                    continue;
                }

                if (!ok) return false;
                si++;
                continue;
            }

            if (si >= end || !CharEquals(s[si], pc, ignoreCase)) return false;
            si++;
            pi++;
        }
    }

    private static bool CharEquals(char a, char b, bool ignoreCase) =>
        ignoreCase ? char.ToLowerInvariant(a) == char.ToLowerInvariant(b) : a == b;

    /// <summary>
    /// Evaluates a bracket expression starting at <paramref name="pi"/>. Returns false when the
    /// bracket is unterminated (the caller then treats '[' literally); otherwise advances
    /// <paramref name="pi"/> past the ']' and reports the result in <paramref name="matched"/>.
    /// </summary>
    private static bool MatchBracket(char c, string p, ref int pi, bool ignoreCase, out bool matched)
    {
        matched = false;
        var i = pi + 1;
        var negate = false;

        if (i < p.Length && (p[i] == '!' || p[i] == '^')) { negate = true; i++; }

        var any = false;
        var first = true;

        while (i < p.Length)
        {
            if (p[i] == ']' && !first)
            {
                pi = i + 1;
                matched = negate ? !any : any;
                return true;
            }

            first = false;

            if (p[i] == '[' && i + 1 < p.Length && p[i + 1] == ':')
            {
                var close = p.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close > 0)
                {
                    var className = p[(i + 2)..close];
                    if (MatchesClass(c, className)) any = true;
                    i = close + 2;
                    continue;
                }
            }

            var lo = p[i];
            if (lo == '\\' && i + 1 < p.Length) { i++; lo = p[i]; }
            i++;

            if (i + 1 < p.Length && p[i] == '-' && p[i + 1] != ']')
            {
                i++;
                var hi = p[i];
                if (hi == '\\' && i + 1 < p.Length) { i++; hi = p[i]; }
                i++;

                if (ignoreCase)
                {
                    var lc = char.ToLowerInvariant(c);
                    var uc = char.ToUpperInvariant(c);
                    if ((lc >= char.ToLowerInvariant(lo) && lc <= char.ToLowerInvariant(hi)) ||
                        (uc >= lo && uc <= hi) || (c >= lo && c <= hi)) any = true;
                }
                else if (c >= lo && c <= hi)
                {
                    any = true;
                }

                continue;
            }

            if (CharEquals(c, lo, ignoreCase)) any = true;
        }

        return false; // no closing ']'
    }

    private static bool MatchesClass(char c, string className) => className switch
    {
        "alpha" => char.IsLetter(c),
        "digit" => char.IsDigit(c),
        "alnum" => char.IsLetterOrDigit(c),
        "space" => char.IsWhiteSpace(c),
        "upper" => char.IsUpper(c),
        "lower" => char.IsLower(c),
        "punct" => char.IsPunctuation(c) || char.IsSymbol(c),
        "print" => !char.IsControl(c),
        "graph" => !char.IsControl(c) && !char.IsWhiteSpace(c),
        "cntrl" => char.IsControl(c),
        "xdigit" => Uri.IsHexDigit(c),
        "blank" => c is ' ' or '\t',
        "word" => char.IsLetterOrDigit(c) || c == '_',
        _ => false,
    };
}
