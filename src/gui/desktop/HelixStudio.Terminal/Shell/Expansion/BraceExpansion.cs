using System.Globalization;
using HelixPythonNamespace.Terminal.Shell.Lexing;

namespace HelixPythonNamespace.Terminal.Shell.Expansion;

/// <summary>
/// Brace expansion (<c>{a,b}</c>, <c>{1..10}</c>, <c>{a..f..2}</c>). It runs before every other
/// expansion and is purely textual, so only unquoted literal characters take part; quoted text
/// and parameter/command substitutions stay opaque and are carried through untouched.
/// </summary>
public static class BraceExpansion
{
    /// <summary>One character of unquoted literal text, or one indivisible non-literal part.</summary>
    private readonly struct Atom
    {
        public Atom(char c) { Char = c; Part = null; }

        public Atom(WordPart part) { Char = '\0'; Part = part; }

        public char Char { get; }

        public WordPart? Part { get; }

        public bool IsChar => Part is null;
    }

    public static List<Word> Expand(Word word)
    {
        var atoms = Flatten(word);
        if (!atoms.Any(a => a.IsChar && a.Char == '{')) return new List<Word> { word };

        var expanded = ExpandAtoms(atoms);
        if (expanded.Count == 1) return new List<Word> { word };

        return expanded.Select(Rebuild).ToList();
    }

    private static List<Atom> Flatten(Word word)
    {
        var atoms = new List<Atom>();

        foreach (var part in word.Parts)
        {
            if (part is LiteralPart { Quoted: false } literal)
            {
                foreach (var c in literal.Text) atoms.Add(new Atom(c));
            }
            else
            {
                atoms.Add(new Atom(part));
            }
        }

        return atoms;
    }

    private static Word Rebuild(List<Atom> atoms)
    {
        var word = new Word();
        var run = new System.Text.StringBuilder();

        void Flush()
        {
            if (run.Length == 0) return;
            word.Parts.Add(new LiteralPart(run.ToString(), false));
            run.Clear();
        }

        foreach (var atom in atoms)
        {
            if (atom.IsChar) run.Append(atom.Char);
            else { Flush(); word.Parts.Add(atom.Part!); }
        }

        Flush();
        if (word.Parts.Count == 0) word.Parts.Add(new LiteralPart(string.Empty, false));
        return word;
    }

    private static List<List<Atom>> ExpandAtoms(List<Atom> atoms)
    {
        for (var i = 0; i < atoms.Count; i++)
        {
            if (!atoms[i].IsChar) continue;
            if (atoms[i].Char == '\\') { i++; continue; }
            if (atoms[i].Char != '{') continue;

            var close = FindClosingBrace(atoms, i);
            if (close < 0) continue;

            var body = atoms.GetRange(i + 1, close - i - 1);
            var alternatives = SplitTopLevel(body);

            if (alternatives.Count == 1)
            {
                var sequence = TryExpandSequence(body);
                if (sequence is null) continue; // `{x}` is literal text
                alternatives = sequence;
            }

            var preamble = atoms.GetRange(0, i);
            var postscript = atoms.GetRange(close + 1, atoms.Count - close - 1);
            var expandedTails = ExpandAtoms(postscript);
            var results = new List<List<Atom>>();

            foreach (var alternative in alternatives)
            {
                foreach (var expandedAlternative in ExpandAtoms(alternative))
                {
                    foreach (var tail in expandedTails)
                    {
                        var combined = new List<Atom>(preamble.Count + expandedAlternative.Count + tail.Count);
                        combined.AddRange(preamble);
                        combined.AddRange(expandedAlternative);
                        combined.AddRange(tail);
                        results.Add(combined);
                    }
                }
            }

            return results;
        }

        return new List<List<Atom>> { atoms };
    }

    private static int FindClosingBrace(List<Atom> atoms, int open)
    {
        var depth = 0;

        for (var i = open; i < atoms.Count; i++)
        {
            if (!atoms[i].IsChar) continue;
            if (atoms[i].Char == '\\') { i++; continue; }
            if (atoms[i].Char == '{') depth++;
            else if (atoms[i].Char == '}' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>Splits on commas that are not inside a nested brace group. Returns one item when there are none.</summary>
    private static List<List<Atom>> SplitTopLevel(List<Atom> body)
    {
        var parts = new List<List<Atom>>();
        var current = new List<Atom>();
        var depth = 0;

        for (var i = 0; i < body.Count; i++)
        {
            var atom = body[i];

            if (atom.IsChar)
            {
                if (atom.Char == '\\' && i + 1 < body.Count)
                {
                    current.Add(atom);
                    current.Add(body[++i]);
                    continue;
                }

                if (atom.Char == '{') depth++;
                else if (atom.Char == '}') depth--;
                else if (atom.Char == ',' && depth == 0)
                {
                    parts.Add(current);
                    current = new List<Atom>();
                    continue;
                }
            }

            current.Add(atom);
        }

        parts.Add(current);
        return parts;
    }

    /// <summary>Expands <c>{1..10}</c>, <c>{a..z}</c> and the <c>..step</c> variants; null if not a sequence.</summary>
    private static List<List<Atom>>? TryExpandSequence(List<Atom> body)
    {
        if (body.Any(a => !a.IsChar)) return null;

        var text = new string(body.Select(a => a.Char).ToArray());
        var sections = SplitOnDots(text);
        if (sections is null || sections.Count is < 2 or > 3) return null;

        var from = sections[0];
        var to = sections[1];
        long step = 1;

        if (sections.Count == 3)
        {
            if (!long.TryParse(sections[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out step)) return null;
            if (step == 0) return null;
            step = Math.Abs(step);
        }

        if (long.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) &&
            long.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
        {
            // {01..10} keeps the zero padding of the widest endpoint.
            var width = 0;
            if ((from.StartsWith('0') && from.Length > 1) || (to.StartsWith('0') && to.Length > 1))
                width = Math.Max(from.Length, to.Length);

            var values = new List<List<Atom>>();
            if (start <= end)
                for (var v = start; v <= end; v += step) values.Add(ToAtoms(Format(v, width)));
            else
                for (var v = start; v >= end; v -= step) values.Add(ToAtoms(Format(v, width)));

            return values;
        }

        if (from.Length == 1 && to.Length == 1 && !char.IsDigit(from[0]) && !char.IsDigit(to[0]))
        {
            var values = new List<List<Atom>>();
            if (from[0] <= to[0])
                for (var c = from[0]; c <= to[0]; c = (char)(c + step)) values.Add(ToAtoms(c.ToString()));
            else
                for (var c = from[0]; c >= to[0]; c = (char)(c - step)) values.Add(ToAtoms(c.ToString()));

            return values;
        }

        return null;
    }

    private static string Format(long value, int width) =>
        width <= 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : (value < 0 ? "-" : string.Empty) + Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(value < 0 ? width - 1 : width, '0');

    private static List<Atom> ToAtoms(string text) => text.Select(c => new Atom(c)).ToList();

    private static List<string>? SplitOnDots(string text)
    {
        var parts = new List<string>();
        var start = 0;

        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '.' || text[i + 1] != '.') continue;
            parts.Add(text[start..i]);
            start = i + 2;
            i++;
        }

        if (start == 0) return null;
        parts.Add(text[start..]);
        return parts.Any(p => p.Length == 0) ? null : parts;
    }
}
