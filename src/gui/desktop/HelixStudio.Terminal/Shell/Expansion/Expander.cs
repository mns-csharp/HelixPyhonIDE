using System.Text;
using HelixPythonNamespace.Terminal.Shell.Lexing;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Expansion;

/// <summary>
/// Performs the expansions a word goes through, in the order bash defines them: brace, tilde,
/// parameter, command and arithmetic substitution, then field splitting, then pathname
/// expansion, then quote removal.
/// </summary>
public sealed class Expander
{
    private readonly ShellContext _context;

    public Expander(ShellContext context) => _context = context;

    /// <summary>Full expansion of a command's argument words.</summary>
    public List<string> ExpandWords(IEnumerable<Word> words)
    {
        var results = new List<string>();

        foreach (var word in words)
        {
            foreach (var braced in BraceExpansion.Expand(word))
            {
                foreach (var field in ExpandFields(braced))
                {
                    if (_context.Options.NoGlob) { results.Add(field.Plain); continue; }

                    var pattern = field.ToPattern();
                    var matches = ShellPattern.HasMetacharacters(pattern)
                        ? PathGlob.Expand(pattern, _context.WorkingDirectory)
                        : null;

                    if (matches is null) results.Add(field.Plain);
                    else results.AddRange(matches);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Expansion without field splitting or globbing, for places that take exactly one word:
    /// redirection targets, assignment right-hand sides, <c>case</c> subjects.
    /// </summary>
    public string ExpandToString(Word? word)
    {
        if (word is null) return string.Empty;

        var field = new Field();
        AppendParts(word.Parts, field, splitOnIfs: false, out _);
        return field.Plain;
    }

    /// <summary>Expands an assignment value: no splitting, but a lone glob still expands (as in <c>x=*.txt</c> it does not).</summary>
    public string ExpandAssignmentValue(Word? word) => ExpandToString(word);

    /// <summary>
    /// Expands a word that will be used as a pattern (<c>case</c> labels, <c>[[ x == p ]]</c>),
    /// keeping unquoted metacharacters live and escaping the quoted ones.
    /// </summary>
    public string ExpandToPattern(Word? word)
    {
        if (word is null) return string.Empty;

        var field = new Field();
        AppendParts(word.Parts, field, splitOnIfs: false, out _);
        return field.ToPattern();
    }

    /// <summary>Expands a word into the fields it produces, without pathname expansion.</summary>
    public List<Field> ExpandFields(Word word)
    {
        var open = new Field();
        AppendParts(word.Parts, open, splitOnIfs: true, out var fields);

        var split = new List<Field>();
        foreach (var field in fields) split.AddRange(field.SplitOnIfs(_context.Ifs));

        return split.Where(f => f.Text.Length > 0 || f.HasQuotedContent).ToList();
    }

    // ------------------------------------------------------- part walking

    private void AppendParts(List<WordPart> parts, Field field, bool splitOnIfs, out List<Field> fields)
    {
        fields = new List<Field> { field };
        var current = field;

        foreach (var part in parts)
        {
            switch (part)
            {
                case LiteralPart literal:
                    current.Append(literal.Text, literal.Quoted);
                    if (literal.Quoted) current.HasQuotedContent = true;
                    break;

                case TildePart tilde:
                    current.Append(ExpandTilde(tilde.User), literal: true);
                    break;

                case ArithmeticPart arithmetic:
                {
                    var value = ArithmeticEvaluator.Evaluate(ExpandNested(arithmetic.Expression), _context);
                    current.Append(value.ToString(), arithmetic.Quoted);
                    if (arithmetic.Quoted) current.HasQuotedContent = true;
                    break;
                }

                case CommandSubstitutionPart substitution:
                {
                    var output = RunCommandSubstitution(substitution.Script);
                    current.Append(output, substitution.Quoted);
                    if (substitution.Quoted) current.HasQuotedContent = true;
                    break;
                }

                case ProcessSubstitutionPart processSubstitution:
                    current.Append(CreateProcessSubstitution(processSubstitution), literal: true);
                    break;

                case ParameterPart parameter:
                    current = ExpandParameter(parameter, current, fields, splitOnIfs);
                    break;
            }
        }
    }

    /// <summary>Expands <c>$name</c>/<c>${...}</c>, which may open new fields when it yields a list.</summary>
    private Field ExpandParameter(ParameterPart parameter, Field current, List<Field> fields, bool splitOnIfs)
    {
        var name = parameter.Name;

        if (parameter.Indirect)
        {
            var pointer = _context.GetValue(name);
            if (string.IsNullOrEmpty(pointer)) return current;
            name = pointer;
        }

        var isList = name is "@" or "*" || parameter.Subscript is "@" or "*";
        var wantsSeparateFields = name == "@" || parameter.Subscript == "@";

        if (isList)
        {
            var items = ListValues(name, parameter.Subscript);

            if (parameter.Length)
            {
                current.Append(items.Count.ToString(), parameter.Quoted);
                return current;
            }

            if (parameter.Operator != ParameterOperator.None)
                items = items.Select(item => ApplyOperator(parameter, item, name) ?? string.Empty).ToList();

            if (!splitOnIfs || !wantsSeparateFields)
            {
                var separator = _context.Ifs.Length > 0 ? _context.Ifs[0].ToString() : " ";
                current.Append(string.Join(separator, items), parameter.Quoted);
                if (parameter.Quoted) current.HasQuotedContent = true;
                return current;
            }

            if (items.Count == 0)
            {
                // "$@" with no positional parameters produces no field at all.
                return current;
            }

            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    current = new Field();
                    fields.Add(current);
                }

                current.Append(items[i], parameter.Quoted);
                if (parameter.Quoted) current.HasQuotedContent = true;
            }

            return current;
        }

        var value = ResolveScalar(parameter, name);

        if (parameter.Length)
        {
            current.Append((value?.Length ?? 0).ToString(), parameter.Quoted);
            if (parameter.Quoted) current.HasQuotedContent = true;
            return current;
        }

        current.Append(value ?? string.Empty, parameter.Quoted);
        if (parameter.Quoted) current.HasQuotedContent = true;
        return current;
    }

    private List<string> ListValues(string name, string? subscript)
    {
        if (name is "@" or "*") return _context.PositionalParameters.ToList();

        var variable = _context.Lookup(name);
        if (variable is null) return new List<string>();
        return variable.AsList().ToList();
    }

    private string? ResolveScalar(ParameterPart parameter, string name)
    {
        string? value;

        if (parameter.Subscript is { Length: > 0 } subscript)
        {
            var variable = _context.Lookup(name);
            var list = variable?.AsList();
            var index = (int)ArithmeticEvaluator.Evaluate(subscript, _context);
            if (index < 0 && list is not null) index += list.Count;
            value = list is not null && index >= 0 && index < list.Count ? list[index] : null;
        }
        else
        {
            value = _context.GetValue(name);
        }

        if (parameter.Operator == ParameterOperator.None)
        {
            if (value is null && _context.Options.ErrorOnUnset && !IsSpecial(name))
                throw new ShellException($"{name}: unbound variable");

            return value;
        }

        return ApplyOperator(parameter, value, name);
    }

    private static bool IsSpecial(string name) =>
        name.Length == 1 && !char.IsLetter(name[0]) && name[0] != '_';

    private string? ApplyOperator(ParameterPart parameter, string? value, string name)
    {
        var unset = value is null || (parameter.ColonForm && value.Length == 0);

        switch (parameter.Operator)
        {
            case ParameterOperator.UseDefault:
                return unset ? ExpandToString(parameter.Argument) : value;

            case ParameterOperator.AssignDefault:
            {
                if (!unset) return value;
                var replacement = ExpandToString(parameter.Argument);
                _context.SetValue(name, replacement);
                return replacement;
            }

            case ParameterOperator.ErrorIfUnset:
            {
                if (!unset) return value;
                var message = ExpandToString(parameter.Argument);
                throw new ShellException($"{name}: {(message.Length > 0 ? message : "parameter null or not set")}");
            }

            case ParameterOperator.UseAlternate:
                return unset ? string.Empty : ExpandToString(parameter.Argument);
        }

        value ??= string.Empty;

        switch (parameter.Operator)
        {
            case ParameterOperator.RemoveSmallestPrefix:
            case ParameterOperator.RemoveLargestPrefix:
            {
                var pattern = ExpandToPattern(parameter.Argument);
                var length = ShellPattern.MatchPrefixLength(value, pattern, parameter.Operator == ParameterOperator.RemoveLargestPrefix);
                return length < 0 ? value : value[length..];
            }

            case ParameterOperator.RemoveSmallestSuffix:
            case ParameterOperator.RemoveLargestSuffix:
            {
                var pattern = ExpandToPattern(parameter.Argument);
                var length = ShellPattern.MatchSuffixLength(value, pattern, parameter.Operator == ParameterOperator.RemoveLargestSuffix);
                return length < 0 ? value : value[..^length];
            }

            case ParameterOperator.ReplaceFirst:
            case ParameterOperator.ReplaceAll:
            {
                var pattern = ExpandToPattern(parameter.Argument);
                var replacement = ExpandToString(parameter.Replacement);
                return Replace(value, pattern, replacement, parameter.Operator == ParameterOperator.ReplaceAll);
            }

            case ParameterOperator.Substring:
            {
                var argument = ExpandToString(parameter.Argument);
                var colon = FindTopLevelColon(argument);
                var offsetText = colon < 0 ? argument : argument[..colon];
                var lengthText = colon < 0 ? null : argument[(colon + 1)..];

                var offset = (int)ArithmeticEvaluator.Evaluate(offsetText, _context);
                if (offset < 0) offset = Math.Max(0, value.Length + offset);
                if (offset > value.Length) return string.Empty;

                if (lengthText is null || lengthText.Trim().Length == 0) return value[offset..];

                var length = (int)ArithmeticEvaluator.Evaluate(lengthText, _context);
                if (length < 0) length = Math.Max(0, value.Length + length - offset);
                length = Math.Min(length, value.Length - offset);
                return value.Substring(offset, length);
            }

            case ParameterOperator.UpperCase:
                return ChangeCase(value, parameter, char.ToUpperInvariant);

            case ParameterOperator.LowerCase:
                return ChangeCase(value, parameter, char.ToLowerInvariant);
        }

        return value;
    }

    private string ChangeCase(string value, ParameterPart parameter, Func<char, char> convert)
    {
        if (value.Length == 0) return value;

        var pattern = ExpandToPattern(parameter.Argument);
        var matchesAll = pattern.Length == 0;

        if (!parameter.AllCharacters)
        {
            if (matchesAll || ShellPattern.IsMatch(value[0].ToString(), pattern))
                return convert(value[0]) + value[1..];

            return value;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(matchesAll || ShellPattern.IsMatch(c.ToString(), pattern) ? convert(c) : c);

        return sb.ToString();
    }

    private static int FindTopLevelColon(string text)
    {
        var depth = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '[') depth++;
            else if (text[i] is ')' or ']') depth--;
            else if (text[i] == ':' && depth == 0) return i;
        }

        return -1;
    }

    private static string Replace(string value, string pattern, string replacement, bool all)
    {
        // An anchored pattern (^ is spelled '#' / '%' in bash) is not supported here; bash's
        // ${v/#pat} and ${v/%pat} forms are handled by treating the marker as part of the pattern.
        var anchorStart = pattern.StartsWith('#');
        var anchorEnd = pattern.StartsWith('%');
        if (anchorStart || anchorEnd) pattern = pattern[1..];

        if (anchorStart)
        {
            var length = ShellPattern.MatchPrefixLength(value, pattern, longest: true);
            return length < 0 ? value : replacement + value[length..];
        }

        if (anchorEnd)
        {
            var length = ShellPattern.MatchSuffixLength(value, pattern, longest: true);
            return length < 0 ? value : value[..^length] + replacement;
        }

        var sb = new StringBuilder();
        var index = 0;

        while (index <= value.Length)
        {
            if (!ShellPattern.TryFindMatch(value, pattern, index, out var start, out var length))
            {
                sb.Append(value[index..]);
                break;
            }

            sb.Append(value[index..start]).Append(replacement);
            index = length == 0 ? start + 1 : start + length;

            if (length == 0 && start < value.Length) sb.Append(value[start]);

            if (!all)
            {
                sb.Append(value[Math.Min(index, value.Length)..]);
                break;
            }
        }

        return sb.ToString();
    }

    // -------------------------------------------------------------- tilde

    private string ExpandTilde(string user)
    {
        if (user.Length == 0) return ShellContext.HomeDirectory;
        if (user == "+") return _context.WorkingDirectory;
        if (user == "-") return _context.GetValue("OLDPWD") ?? _context.WorkingDirectory;

        // ~someuser: guess a sibling of the current user's profile directory.
        var parent = Path.GetDirectoryName(ShellContext.HomeDirectory);
        if (parent is not null)
        {
            var candidate = Path.Combine(parent, user);
            if (Directory.Exists(candidate)) return candidate;
        }

        return "~" + user;
    }

    // -------------------------------------------------------- substitution

    private string ExpandNested(string text)
    {
        // Arithmetic bodies may themselves contain $(...) and ${...}.
        if (!text.Contains('$') && !text.Contains('`')) return text;

        var word = Lexer.SubWord(text);
        return ExpandToString(word);
    }

    private string RunCommandSubstitution(string script)
    {
        var interpreter = _context.Interpreter
            ?? throw new ShellException("command substitution is not available in this context");

        var output = interpreter.RunCapture(script, _context);
        return output.TrimEnd('\n', '\r');
    }

    private string CreateProcessSubstitution(ProcessSubstitutionPart part)
    {
        var interpreter = _context.Interpreter
            ?? throw new ShellException("process substitution is not available in this context");

        var path = Path.Combine(Path.GetTempPath(), "stt-" + Guid.NewGuid().ToString("N") + ".tmp");
        _context.TemporaryFiles.Add(path);

        if (part.Input)
        {
            var output = interpreter.RunCapture(part.Script, _context);
            File.WriteAllText(path, output);
        }
        else
        {
            File.WriteAllText(path, string.Empty);

            // The consumer writes to the file; feed it to the script once the outer command ends.
            _context.DeferredActions.Add(() =>
            {
                using var stream = File.OpenRead(path);
                interpreter.RunWithInput(part.Script, _context, stream);
            });
        }

        return path;
    }
}

/// <summary>
/// A field under construction. Alongside the text it keeps one flag per character saying whether
/// that character came from quoted input, which is what decides splitting and globbing.
/// </summary>
public sealed class Field
{
    public StringBuilder Text { get; } = new();

    public List<bool> Literal { get; } = new();

    /// <summary>True when any quotes appeared, so an empty result still counts as a field.</summary>
    public bool HasQuotedContent { get; set; }

    public string Plain => Text.ToString();

    public void Append(string text, bool literal)
    {
        Text.Append(text);
        for (var i = 0; i < text.Length; i++) Literal.Add(literal);
    }

    /// <summary>Renders the field as a glob pattern, escaping every character that was quoted.</summary>
    public string ToPattern()
    {
        var sb = new StringBuilder(Text.Length);
        var text = Text.ToString();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (Literal[i] && c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Splits on unquoted IFS characters, following the usual whitespace-run rules.</summary>
    public List<Field> SplitOnIfs(string ifs)
    {
        if (ifs.Length == 0) return new List<Field> { this };

        var text = Text.ToString();
        var whitespace = ifs.Where(char.IsWhiteSpace).ToHashSet();
        var others = ifs.Where(c => !char.IsWhiteSpace(c)).ToHashSet();

        var fields = new List<Field>();
        var current = new Field { HasQuotedContent = HasQuotedContent };
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (!Literal[i] && whitespace.Contains(c))
            {
                // A run of IFS whitespace is one delimiter and never produces an empty field.
                if (current.Text.Length > 0 || current.HasQuotedContent)
                {
                    fields.Add(current);
                    current = new Field();
                }

                while (i < text.Length && !Literal[i] && whitespace.Contains(text[i])) i++;
                continue;
            }

            if (!Literal[i] && others.Contains(c))
            {
                fields.Add(current);
                current = new Field();
                i++;
                continue;
            }

            current.Append(c.ToString(), Literal[i]);
            i++;
        }

        fields.Add(current);
        return fields;
    }
}
