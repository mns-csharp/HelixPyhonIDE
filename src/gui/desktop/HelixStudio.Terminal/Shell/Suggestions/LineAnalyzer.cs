using System.Text;

namespace HelixPythonNamespace.Terminal.Shell.Suggestions;

public enum LinePosition
{
    Command,
    Option,
    OptionValue,
    Argument,
    Variable,
    Redirect,
}

/// <summary>
/// What the caret is on. <see cref="WordStart"/> and <see cref="WordLength"/> are the raw span in
/// the line, quotes included, which is what accepting a suggestion replaces; <see cref="Word"/> is
/// the same text unescaped, which is what is matched.
/// </summary>
public sealed record LineContext(
    LinePosition Position,
    int WordStart,
    int WordLength,
    string Word,
    CommandSpec? Spec,
    IReadOnlyList<string> CommandPath,
    int ArgumentIndex,
    string? ArgumentKind,
    IReadOnlyList<ChoiceSpec> Choices,
    bool OffersSubcommands);

/// <summary>
/// Reads the text before the caret the way the shell will, but forgivingly: a line being typed is
/// unfinished by nature, so an open quote runs to the caret instead of being an error. That is why
/// this does not use <c>Lexer</c>, which rejects exactly such lines.
/// </summary>
public static class LineAnalyzer
{
    private const string Escapable = " \t'\"\\|&;()<>$";

    private enum TokenKind { Word, Separator, Redirect }

    private sealed record Token(TokenKind Kind, string Text, int Start, int End);

    public static LineContext Analyze(string line, int caret, SpecLibrary specs)
    {
        caret = Math.Clamp(caret, 0, line.Length);
        var text = line[..caret];
        var tokens = Scan(text);

        // The current word is the last token only if it runs right up to the caret.
        Token? current = tokens.Count > 0 && tokens[^1].Kind == TokenKind.Word && tokens[^1].End == caret ? tokens[^1] : null;
        var before = current is null ? tokens : tokens.Take(tokens.Count - 1).ToList();

        var wordStart = current?.Start ?? caret;
        var word = current?.Text ?? string.Empty;

        var segmentStart = before.FindLastIndex(t => t.Kind == TokenKind.Separator) + 1;
        var segment = before.Skip(segmentStart).ToList();

        if (segment.Count > 0 && segment[^1].Kind == TokenKind.Redirect)
            return Make(LinePosition.Redirect, ArgumentKinds.Path);

        var words = segment.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text)
            .SkipWhile(IsAssignment).ToList();

        if (words.Count == 0)
            return word.StartsWith('$') ? Make(LinePosition.Variable, null) : Make(LinePosition.Command, null);

        var path = new List<string> { words[0] };
        var spec = specs.Find(words[0]);
        var argumentIndex = 0;
        OptionSpec? pendingValue = null;

        foreach (var w in words.Skip(1))
        {
            if (pendingValue is not null)
            {
                pendingValue = null;
                continue;
            }

            if (w.StartsWith('-') && w.Length > 1)
            {
                var option = spec?.Option(w.Split('=')[0]);
                if (option?.Value is not null && !w.Contains('=')) pendingValue = option;
                continue;
            }

            var sub = argumentIndex == 0 ? spec?.Subcommand(w) : null;
            if (sub is not null)
            {
                spec = sub;
                path.Add(w);
                continue;
            }

            argumentIndex++;
        }

        if (pendingValue is not null)
            return Make(LinePosition.OptionValue, pendingValue.Value, pendingValue.Choices, spec, path, argumentIndex);

        if (word.StartsWith('$'))
            return Make(LinePosition.Variable, null, null, spec, path, argumentIndex);

        if (word.StartsWith('-'))
            return Make(LinePosition.Option, null, null, spec, path, argumentIndex);

        var argument = spec?.Argument(argumentIndex);
        var kind = argument?.Kind ?? (spec is { Args.Count: 0, Subcommands.Count: > 0 } ? null : ArgumentKinds.Path);
        var offersSubcommands = argumentIndex == 0 && spec is { Subcommands.Count: > 0 };

        return new LineContext(LinePosition.Argument, wordStart, caret - wordStart, word, spec, path,
            argumentIndex, kind, argument?.Choices ?? (IReadOnlyList<ChoiceSpec>)Array.Empty<ChoiceSpec>(), offersSubcommands);

        LineContext Make(LinePosition position, string? kindOf, IReadOnlyList<ChoiceSpec>? choices = null,
            CommandSpec? ofSpec = null, IReadOnlyList<string>? ofPath = null, int index = 0) =>
            new(position, wordStart, caret - wordStart, word, ofSpec, ofPath ?? Array.Empty<string>(), index,
                kindOf, choices ?? Array.Empty<ChoiceSpec>(), false);
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        if (equals <= 0) return false;
        if (!(char.IsLetter(word[0]) || word[0] == '_')) return false;
        return word[..equals].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static List<Token> Scan(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is ' ' or '\t' or '\n')
            {
                i++;
                continue;
            }

            if (c is '|' or '&' or ';' or '(' or ')')
            {
                var length = i + 1 < text.Length && text[i + 1] == c && c is '|' or '&' ? 2 : 1;
                tokens.Add(new Token(TokenKind.Separator, text.Substring(i, length), i, i + length));
                i += length;
                continue;
            }

            if (c is '<' or '>')
            {
                var length = c == '>' && i + 1 < text.Length && text[i + 1] == '>' ? 2 : 1;
                tokens.Add(new Token(TokenKind.Redirect, text.Substring(i, length), i, i + length));
                i += length;
                continue;
            }

            var start = i;
            var word = new StringBuilder();
            char? quote = null;

            while (i < text.Length)
            {
                var ch = text[i];

                if (quote is not null)
                {
                    if (ch == quote) { quote = null; i++; }
                    else if (quote == '"' && ch == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$') { word.Append(text[i + 1]); i += 2; }
                    else { word.Append(ch); i++; }
                    continue;
                }

                if (ch is '\'' or '"') { quote = ch; i++; continue; }

                if (ch == '\\' && i + 1 < text.Length && Escapable.Contains(text[i + 1]))
                {
                    word.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch is ' ' or '\t' or '\n' or '|' or '&' or ';' or '(' or ')' or '<' or '>') break;

                word.Append(ch);
                i++;
            }

            tokens.Add(new Token(TokenKind.Word, word.ToString(), start, i));
        }

        return tokens;
    }
}
