using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Expansion;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Builtins;

/// <summary>Text-processing utilities: the filters that make pipelines worth having.</summary>
public static class TextCommands
{
    public static void Register(BuiltinRegistry registry)
    {
        registry.Register("cat", "cat [-nEA] [file ...]", Cat);
        registry.Register("head", "head [-n count] [-c bytes] [file ...]", Head);
        registry.Register("tail", "tail [-n count] [-f] [file ...]", Tail);
        registry.Register("wc", "wc [-lwcm] [file ...]", Wc);
        registry.Register("grep", "grep [-invcloqrwxEFH] pattern [file ...]", Grep);
        registry.Register("egrep", "egrep pattern [file ...]", Grep);
        registry.Register("fgrep", "fgrep pattern [file ...]", Grep);
        registry.Register("sed", "sed [-n] [-e] script [file ...]", Sed);
        registry.Register("sort", "sort [-nrufb] [-k field] [-t sep] [file ...]", Sort);
        registry.Register("uniq", "uniq [-cdui] [file]", Uniq);
        registry.Register("cut", "cut -f list [-d sep] | -c list [file ...]", Cut);
        registry.Register("tr", "tr [-ds] set1 [set2]", Tr);
        registry.Register("tee", "tee [-a] file ...", Tee);
        registry.Register("rev", "rev [file ...]", Rev);
        registry.Register("nl", "nl [file ...]", Nl);
        registry.Register("xargs", "xargs [-n count] [-I marker] command", Xargs);
        registry.Register("diff", "diff file1 file2", Diff);
        registry.Register("awk", "awk [-F sep] 'program' [file ...]", Awk);
        registry.Register("more", "more [file ...]", Cat);
        registry.Register("less", "less [file ...]", Cat);
    }

    // ------------------------------------------------------ input plumbing

    /// <summary>Yields a reader per operand, or one for standard input when there are none.</summary>
    private static IEnumerable<(string Name, TextReader Reader, IDisposable? Owned)> OpenInputs(
        ShellContext context, IReadOnlyList<string> operands, string command)
    {
        if (operands.Count == 0)
        {
            yield return ("-", context.Io.In, null);
            yield break;
        }

        foreach (var operand in operands)
        {
            if (operand == "-")
            {
                yield return ("-", context.Io.In, null);
                continue;
            }

            var path = context.ResolvePath(operand);
            StreamReader reader;

            if (Directory.Exists(path))
            {
                Interpreter.WriteError(context, $"{command}: {operand}: Is a directory");
                continue;
            }

            try
            {
                reader = new StreamReader(File.OpenRead(path));
            }
            catch (Exception)
            {
                Interpreter.WriteError(context, $"{command}: {operand}: No such file or directory");
                continue;
            }

            using (reader) yield return (operand, reader, reader);
        }
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    // ----------------------------------------------------------------- cat

    private static int Cat(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var number = options.Has('n') || options.Has('b');
        var showEnds = options.Has('E') || options.Has('A');
        var count = 0;
        var status = 0;
        var any = false;

        foreach (var (_, reader, _) in OpenInputs(context, options.Operands, "cat"))
        {
            any = true;

            foreach (var line in ReadLines(reader))
            {
                var text = showEnds ? line + "$" : line;
                context.Io.Out.WriteLine(number ? $"{++count,6}\t{text}" : text);
            }
        }

        if (!any && options.Operands.Count > 0) status = 1;
        context.Io.Out.Flush();
        return status;
    }

    private static int Head(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = ParseCountOptions(args, out var count, defaultCount: 10);
        var multiple = options.Operands.Count > 1;
        var first = true;

        foreach (var (name, reader, _) in OpenInputs(context, options.Operands, "head"))
        {
            if (multiple)
            {
                if (!first) context.Io.Out.WriteLine();
                context.Io.Out.WriteLine($"==> {name} <==");
            }

            first = false;
            var emitted = 0;

            foreach (var line in ReadLines(reader))
            {
                if (emitted++ >= count) break;
                context.Io.Out.WriteLine(line);
            }
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static int Tail(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = ParseCountOptions(args, out var count, defaultCount: 10);
        var multiple = options.Operands.Count > 1;
        var first = true;

        foreach (var (name, reader, _) in OpenInputs(context, options.Operands, "tail"))
        {
            if (multiple)
            {
                if (!first) context.Io.Out.WriteLine();
                context.Io.Out.WriteLine($"==> {name} <==");
            }

            first = false;

            var buffer = new Queue<string>();

            foreach (var line in ReadLines(reader))
            {
                buffer.Enqueue(line);
                if (buffer.Count > count) buffer.Dequeue();
            }

            foreach (var line in buffer) context.Io.Out.WriteLine(line);
        }

        context.Io.Out.Flush();

        if (!options.Has('f')) return 0;

        // -f keeps printing new lines until the user interrupts.
        var path = options.Operands.Count > 0 ? context.ResolvePath(options.Operands[0]) : null;
        if (path is null || !File.Exists(path)) return 0;

        var position = new FileInfo(path).Length;

        while (!context.CancellationToken.IsCancellationRequested)
        {
            Thread.Sleep(300);
            var length = new FileInfo(path).Length;
            if (length <= position) continue;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            foreach (var line in ReadLines(reader)) context.Io.Out.WriteLine(line);
            context.Io.Out.Flush();
            position = length;
        }

        return 0;
    }

    /// <summary>Accepts both <c>-n 5</c> and the historic <c>-5</c> spelling.</summary>
    private static CommandLine ParseCountOptions(IReadOnlyList<string> args, out int count, int defaultCount)
    {
        var normalised = new List<string> { args[0] };
        count = defaultCount;

        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].Length > 1 && args[i][0] == '-' && args[i].Skip(1).All(char.IsAsciiDigit))
            {
                count = int.Parse(args[i][1..]);
                continue;
            }

            normalised.Add(args[i]);
        }

        var options = CommandLine.Parse(normalised, "nc");
        if (int.TryParse(options.Value('n'), out var parsed)) count = parsed;
        return options;
    }

    private static int Wc(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var showAll = !options.Has('l') && !options.Has('w') && !options.Has('c') && !options.Has('m');

        long totalLines = 0, totalWords = 0, totalBytes = 0, totalChars = 0;
        var fileCount = 0;

        foreach (var (name, reader, _) in OpenInputs(context, options.Operands, "wc"))
        {
            // Read the whole input: counting newlines is only correct if we can see whether the
            // last line actually ends with one, and -c and -m differ once text is not ASCII.
            var text = reader.ReadToEnd();

            var lines = text.Count(c => c == '\n');
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var chars = text.Length;
            var bytes = Encoding.UTF8.GetByteCount(text);

            totalLines += lines;
            totalWords += words;
            totalBytes += bytes;
            totalChars += chars;
            fileCount++;

            context.Io.Out.WriteLine(FormatCounts(lines, words, bytes, chars, options, showAll, name == "-" ? string.Empty : name));
        }

        if (fileCount > 1)
            context.Io.Out.WriteLine(FormatCounts(totalLines, totalWords, totalBytes, totalChars, options, showAll, "total"));

        context.Io.Out.Flush();
        return 0;
    }

    private static string FormatCounts(long lines, long words, long bytes, long chars, CommandLine options, bool showAll, string name)
    {
        var parts = new List<string>();
        if (showAll || options.Has('l')) parts.Add(lines.ToString().PadLeft(7));
        if (showAll || options.Has('w')) parts.Add(words.ToString().PadLeft(7));
        if (showAll || options.Has('c')) parts.Add(bytes.ToString().PadLeft(7));
        if (options.Has('m') && !options.Has('c')) parts.Add(chars.ToString().PadLeft(7));

        var text = string.Join(string.Empty, parts);
        return name.Length > 0 ? text + " " + name : text;
    }

    // ---------------------------------------------------------------- grep

    private static int Grep(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "eABC", stopAtFirstOperand: false);
        var operands = options.Operands.ToList();

        var patternText = options.Value('e');

        if (patternText is null)
        {
            if (operands.Count == 0) throw new ShellException("usage: grep [options] pattern [file ...]", 2);
            patternText = operands[0];
            operands.RemoveAt(0);
        }

        var fixedString = options.Has('F') || args[0] == "fgrep";
        var pattern = fixedString ? Regex.Escape(patternText) : patternText;
        if (options.Has('w')) pattern = @"\b(?:" + pattern + @")\b";
        if (options.Has('x')) pattern = "^(?:" + pattern + ")$";

        Regex regex;
        try
        {
            regex = new Regex(pattern, options.Has('i') ? RegexOptions.IgnoreCase : RegexOptions.None);
        }
        catch (ArgumentException ex)
        {
            Interpreter.WriteError(context, $"grep: {patternText}: {ex.Message}");
            return 2;
        }

        var recursive = options.Has('r') || options.Has('R');
        var inputs = recursive ? ExpandRecursive(context, operands) : operands;
        var showName = options.Has('H') || (inputs.Count > 1 && !options.Has('h'));
        var invert = options.Has('v');
        var matchedAny = false;
        var perFileCount = 0;

        foreach (var (name, reader, _) in OpenInputs(context, inputs, "grep"))
        {
            var lineNumber = 0;
            perFileCount = 0;

            foreach (var line in ReadLines(reader))
            {
                lineNumber++;
                var matched = regex.IsMatch(line) ^ invert;
                if (!matched) continue;

                matchedAny = true;
                perFileCount++;

                if (options.Has('q')) return 0;
                if (options.Has('c')) continue;

                if (options.Has('l'))
                {
                    context.Io.Out.WriteLine(name);
                    break;
                }

                if (options.Has('o'))
                {
                    foreach (Match match in regex.Matches(line))
                        context.Io.Out.WriteLine(Prefix(name, lineNumber) + match.Value);

                    continue;
                }

                context.Io.Out.WriteLine(Prefix(name, lineNumber) + Highlight(line, regex, options));
            }

            if (options.Has('c')) context.Io.Out.WriteLine(showName ? $"{name}:{perFileCount}" : perFileCount.ToString());
        }

        context.Io.Out.Flush();
        return matchedAny ? 0 : 1;

        string Prefix(string name, int lineNumber)
        {
            var sb = new StringBuilder();
            if (showName) sb.Append(name).Append(':');
            if (options.Has('n')) sb.Append(lineNumber).Append(':');
            return sb.ToString();
        }
    }

    private static string Highlight(string line, Regex regex, CommandLine options)
    {
        if (options.HasLong("color") is false && options.HasLong("colour") is false) return line;
        return regex.Replace(line, m => "\x1b[1;31m" + m.Value + "\x1b[0m");
    }

    private static List<string> ExpandRecursive(ShellContext context, List<string> operands)
    {
        var roots = operands.Count > 0 ? operands : new List<string> { "." };
        var files = new List<string>();

        foreach (var root in roots)
        {
            var path = context.ResolvePath(root);

            if (File.Exists(path)) { files.Add(root); continue; }
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                files.Add(Path.GetRelativePath(context.WorkingDirectory, file).Replace('\\', '/'));
        }

        return files;
    }

    // ----------------------------------------------------------------- sed

    private static int Sed(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "e", stopAtFirstOperand: false);
        var operands = options.Operands.ToList();
        var scripts = new List<string>();

        if (options.Value('e') is { } fromFlag) scripts.Add(fromFlag);
        else if (operands.Count > 0) { scripts.Add(operands[0]); operands.RemoveAt(0); }
        else throw new ShellException("no script specified", 2);

        var commands = scripts.SelectMany(SplitSedScript).Select(SedCommand.Parse).ToList();

        var quiet = options.Has('n');
        var lineNumber = 0;

        foreach (var (_, reader, _) in OpenInputs(context, operands, "sed"))
        {
            var lines = ReadLines(reader).ToList();

            for (var i = 0; i < lines.Count; i++)
            {
                lineNumber = i + 1;
                var line = lines[i];
                var deleted = false;
                var printed = false;

                foreach (var command in commands)
                {
                    if (!command.Applies(line, lineNumber, lines.Count)) continue;

                    switch (command.Action)
                    {
                        case 's':
                            line = command.Substitute(line);
                            break;
                        case 'd':
                            deleted = true;
                            break;
                        case 'p':
                            context.Io.Out.WriteLine(line);
                            printed = true;
                            break;
                        case 'y':
                            line = command.Transliterate(line);
                            break;
                        case 'q':
                            if (!quiet) context.Io.Out.WriteLine(line);
                            context.Io.Out.Flush();
                            return 0;
                    }

                    if (deleted) break;
                }

                if (deleted) continue;
                if (!quiet) context.Io.Out.WriteLine(line);
                else if (printed) { /* already written by the p command */ }
            }
        }

        context.Io.Out.Flush();
        return 0;
    }

    /// <summary>
    /// Splits a sed script on <c>;</c>, but not on semicolons that sit inside the delimited
    /// halves of an <c>s</c> or <c>y</c> command or inside a <c>/pattern/</c> address.
    /// </summary>
    private static IEnumerable<string> SplitSedScript(string script)
    {
        var current = new StringBuilder();
        var i = 0;

        while (i < script.Length)
        {
            var c = script[i];

            if (c == '\\' && i + 1 < script.Length)
            {
                current.Append(c).Append(script[i + 1]);
                i += 2;
                continue;
            }

            if (c == '/' && current.ToString().Trim().Length == 0)
            {
                // A leading /pattern/ address.
                var end = SkipDelimited(script, i, '/', 1);
                current.Append(script[i..end]);
                i = end;
                continue;
            }

            if (c is 's' or 'y' && i + 1 < script.Length && !char.IsLetterOrDigit(script[i + 1]))
            {
                var delimiter = script[i + 1];
                var end = SkipDelimited(script, i + 1, delimiter, 2);
                current.Append(script[i..end]);
                i = end;
                continue;
            }

            if (c == ';')
            {
                if (current.ToString().Trim().Length > 0) yield return current.ToString();
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (current.ToString().Trim().Length > 0) yield return current.ToString();
    }

    /// <summary>Returns the index just past the <paramref name="count"/>-th unescaped delimiter after <paramref name="start"/>.</summary>
    private static int SkipDelimited(string text, int start, char delimiter, int count)
    {
        var i = start + 1;
        var seen = 0;

        while (i < text.Length && seen < count)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] == delimiter) seen++;
            i++;
        }

        return i;
    }

    /// <summary>One sed command: an optional address followed by s, y, d, p or q.</summary>
    private sealed class SedCommand
    {
        public char Action { get; private init; }

        private Regex? _addressPattern;
        private int _addressLine = -1;
        private bool _addressLast;
        private Regex? _search;
        private string _replacement = string.Empty;
        private bool _global;
        private string _from = string.Empty;
        private string _to = string.Empty;

        public static SedCommand Parse(string text)
        {
            text = text.Trim();
            var index = 0;
            Regex? addressPattern = null;
            var addressLine = -1;
            var addressLast = false;

            if (index < text.Length && text[index] == '/')
            {
                var close = FindDelimiter(text, index + 1, '/');
                if (close < 0) throw new ShellException($"unterminated address regex: {text}", 2);
                addressPattern = new Regex(text[(index + 1)..close]);
                index = close + 1;
            }
            else if (index < text.Length && text[index] == '$')
            {
                addressLast = true;
                index++;
            }
            else
            {
                var digits = index;
                while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
                if (digits > index)
                {
                    addressLine = int.Parse(text[index..digits]);
                    index = digits;
                }
            }

            if (index >= text.Length) throw new ShellException($"missing command: {text}", 2);

            var action = text[index];
            var command = new SedCommand
            {
                Action = action,
                _addressPattern = addressPattern,
                _addressLine = addressLine,
                _addressLast = addressLast,
            };

            switch (action)
            {
                case 's':
                case 'y':
                {
                    if (index + 1 >= text.Length) throw new ShellException($"unterminated `{action}' command", 2);
                    var delimiter = text[index + 1];
                    var first = FindDelimiter(text, index + 2, delimiter);
                    if (first < 0) throw new ShellException($"unterminated `{action}' command", 2);
                    var second = FindDelimiter(text, first + 1, delimiter);
                    if (second < 0) throw new ShellException($"unterminated `{action}' command", 2);

                    var left = text[(index + 2)..first];
                    var right = text[(first + 1)..second];
                    var flags = text[(second + 1)..];

                    if (action == 's')
                    {
                        command._search = new Regex(left, flags.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None);
                        command._replacement = ConvertReplacement(right);
                        command._global = flags.Contains('g');
                    }
                    else
                    {
                        command._from = left;
                        command._to = right;
                    }

                    break;
                }

                case 'd' or 'p' or 'q':
                    break;

                default:
                    throw new ShellException($"unknown command: `{action}'", 2);
            }

            return command;
        }

        private static int FindDelimiter(string text, int start, char delimiter)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == delimiter) return i;
            }

            return -1;
        }

        /// <summary>sed writes back-references as <c>\1</c>; .NET wants <c>$1</c>.</summary>
        private static string ConvertReplacement(string replacement)
        {
            var sb = new StringBuilder();

            for (var i = 0; i < replacement.Length; i++)
            {
                if (replacement[i] == '$') { sb.Append("$$"); continue; }

                if (replacement[i] == '\\' && i + 1 < replacement.Length)
                {
                    var next = replacement[++i];
                    if (char.IsAsciiDigit(next)) sb.Append('$').Append(next);
                    else if (next == 'n') sb.Append('\n');
                    else if (next == 't') sb.Append('\t');
                    else sb.Append(next);
                    continue;
                }

                if (replacement[i] == '&') { sb.Append("$0"); continue; }
                sb.Append(replacement[i]);
            }

            return sb.ToString();
        }

        public bool Applies(string line, int lineNumber, int total)
        {
            if (_addressPattern is not null) return _addressPattern.IsMatch(line);
            if (_addressLine >= 0) return lineNumber == _addressLine;
            if (_addressLast) return lineNumber == total;
            return true;
        }

        public string Substitute(string line) =>
            _search is null ? line : _search.Replace(line, _replacement, _global ? -1 : 1);

        public string Transliterate(string line)
        {
            var sb = new StringBuilder(line.Length);

            foreach (var c in line)
            {
                var index = _from.IndexOf(c);
                sb.Append(index >= 0 && index < _to.Length ? _to[index] : c);
            }

            return sb.ToString();
        }
    }

    // -------------------------------------------------------- sort / uniq

    private static int Sort(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "kt");
        var lines = new List<string>();

        foreach (var (_, reader, _) in OpenInputs(context, options.Operands, "sort"))
            lines.AddRange(ReadLines(reader));

        var separator = options.Value('t') is { Length: > 0 } t ? t[0] : (char?)null;
        var field = int.TryParse(options.Value('k')?.Split(',')[0], out var parsed) ? parsed : 0;

        string Key(string line)
        {
            if (field <= 0) return line;

            var parts = separator is null
                ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : line.Split(separator.Value);

            return field - 1 < parts.Length ? parts[field - 1] : string.Empty;
        }

        Comparison<string> comparison;

        if (options.Has('n'))
        {
            comparison = (a, b) => ToNumber(Key(a)).CompareTo(ToNumber(Key(b)));
        }
        else if (options.Has('f'))
        {
            comparison = (a, b) => string.Compare(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            comparison = (a, b) => string.CompareOrdinal(Key(a), Key(b));
        }

        lines.Sort(comparison);
        if (options.Has('r')) lines.Reverse();

        IEnumerable<string> output = lines;
        if (options.Has('u')) output = Distinct(lines, options.Has('f'));

        foreach (var line in output) context.Io.Out.WriteLine(line);
        context.Io.Out.Flush();
        return 0;

        static double ToNumber(string text)
        {
            var trimmed = text.Trim();
            var end = 0;
            if (end < trimmed.Length && (trimmed[end] == '-' || trimmed[end] == '+')) end++;
            while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || trimmed[end] == '.')) end++;
            return double.TryParse(trimmed[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
    }

    private static IEnumerable<string> Distinct(List<string> lines, bool ignoreCase)
    {
        string? previous = null;

        foreach (var line in lines)
        {
            if (previous is not null && string.Equals(previous, line, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;

            previous = line;
            yield return line;
        }
    }

    private static int Uniq(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var comparison = options.Has('i') ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var lines = new List<string>();

        foreach (var (_, reader, _) in OpenInputs(context, options.Operands, "uniq"))
            lines.AddRange(ReadLines(reader));

        var index = 0;

        while (index < lines.Count)
        {
            var run = 1;
            while (index + run < lines.Count && string.Equals(lines[index], lines[index + run], comparison)) run++;

            var emit = true;
            if (options.Has('d') && run < 2) emit = false;
            if (options.Has('u') && run > 1) emit = false;

            if (emit) context.Io.Out.WriteLine(options.Has('c') ? $"{run,7} {lines[index]}" : lines[index]);
            index += run;
        }

        context.Io.Out.Flush();
        return 0;
    }

    // ----------------------------------------------------------- cut / tr

    private static int Cut(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "dfc");
        var separator = options.Value('d') is { Length: > 0 } d ? d[0] : '\t';
        var fieldList = options.Value('f');
        var characterList = options.Value('c');

        if (fieldList is null && characterList is null) throw new ShellException("you must specify a list of fields or characters", 2);

        var ranges = ParseRanges(fieldList ?? characterList!);

        foreach (var (_, reader, _) in OpenInputs(context, options.Operands, "cut"))
        {
            foreach (var line in ReadLines(reader))
            {
                if (characterList is not null)
                {
                    var sb = new StringBuilder();
                    for (var i = 1; i <= line.Length; i++)
                        if (ranges.Any(r => i >= r.From && i <= r.To)) sb.Append(line[i - 1]);

                    context.Io.Out.WriteLine(sb.ToString());
                    continue;
                }

                if (!line.Contains(separator)) { context.Io.Out.WriteLine(line); continue; }

                var parts = line.Split(separator);
                var selected = new List<string>();

                for (var i = 1; i <= parts.Length; i++)
                    if (ranges.Any(r => i >= r.From && i <= r.To)) selected.Add(parts[i - 1]);

                context.Io.Out.WriteLine(string.Join(separator, selected));
            }
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static List<(int From, int To)> ParseRanges(string specification)
    {
        var ranges = new List<(int, int)>();

        foreach (var part in specification.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');

            if (dash < 0)
            {
                if (int.TryParse(part, out var single)) ranges.Add((single, single));
                continue;
            }

            var fromText = part[..dash];
            var toText = part[(dash + 1)..];
            var from = fromText.Length == 0 ? 1 : int.Parse(fromText);
            var to = toText.Length == 0 ? int.MaxValue : int.Parse(toText);
            ranges.Add((from, to));
        }

        return ranges;
    }

    private static int Tr(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        if (options.Operands.Count == 0) throw new ShellException("missing operand", 2);

        var from = ExpandSet(options.Operands[0]);
        var to = options.Operands.Count > 1 ? ExpandSet(options.Operands[1]) : string.Empty;

        var text = context.Io.In.ReadToEnd();
        var sb = new StringBuilder(text.Length);
        var previous = '\0';

        foreach (var c in text)
        {
            var index = from.IndexOf(c);

            if (options.Has('d'))
            {
                if (index >= 0) continue;
                sb.Append(c);
                continue;
            }

            var mapped = index >= 0 ? (to.Length == 0 ? c : to[Math.Min(index, to.Length - 1)]) : c;

            if (options.Has('s') && mapped == previous && index >= 0) continue;

            sb.Append(mapped);
            previous = mapped;
        }

        context.Io.Out.Write(sb.ToString());
        context.Io.Out.Flush();
        return 0;
    }

    /// <summary>Expands <c>a-z</c> ranges and POSIX classes into an explicit character set.</summary>
    private static string ExpandSet(string specification)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < specification.Length; i++)
        {
            if (specification[i] == '[' && i + 1 < specification.Length && specification[i + 1] == ':')
            {
                var close = specification.IndexOf(":]", i, StringComparison.Ordinal);
                if (close > 0)
                {
                    var name = specification[(i + 2)..close];
                    for (var c = (char)0; c < 128; c++)
                        if (MatchesClass(c, name)) sb.Append(c);

                    i = close + 1;
                    continue;
                }
            }

            if (specification[i] == '\\' && i + 1 < specification.Length)
            {
                sb.Append(ShellBuiltins.Unescape(specification.Substring(i, 2), out _));
                i++;
                continue;
            }

            if (i + 2 < specification.Length && specification[i + 1] == '-')
            {
                for (var c = specification[i]; c <= specification[i + 2]; c++) sb.Append(c);
                i += 2;
                continue;
            }

            sb.Append(specification[i]);
        }

        return sb.ToString();
    }

    private static bool MatchesClass(char c, string name) => name switch
    {
        "alpha" => char.IsLetter(c),
        "digit" => char.IsDigit(c),
        "alnum" => char.IsLetterOrDigit(c),
        "space" => char.IsWhiteSpace(c),
        "upper" => char.IsUpper(c),
        "lower" => char.IsLower(c),
        "punct" => char.IsPunctuation(c) || char.IsSymbol(c),
        _ => false,
    };

    // ------------------------------------------------------------ plumbing

    private static int Tee(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var writers = new List<StreamWriter>();

        try
        {
            foreach (var operand in options.Operands)
            {
                var path = context.ResolvePath(operand);

                try
                {
                    writers.Add(new StreamWriter(path, append: options.Has('a')));
                }
                catch (Exception ex)
                {
                    Interpreter.WriteError(context, $"tee: {operand}: {ex.Message}");
                }
            }

            while (context.Io.In.ReadLine() is { } line)
            {
                context.Io.Out.WriteLine(line);
                context.Io.Out.Flush();
                foreach (var writer in writers) writer.WriteLine(line);
            }
        }
        finally
        {
            foreach (var writer in writers) { writer.Flush(); writer.Dispose(); }
        }

        return 0;
    }

    private static int Rev(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        foreach (var (_, reader, _) in OpenInputs(context, options.Operands, "rev"))
        {
            foreach (var line in ReadLines(reader))
            {
                var chars = line.ToCharArray();
                Array.Reverse(chars);
                context.Io.Out.WriteLine(new string(chars));
            }
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static int Nl(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var number = 0;

        foreach (var (_, reader, _) in OpenInputs(context, options.Operands, "nl"))
        {
            foreach (var line in ReadLines(reader))
                context.Io.Out.WriteLine(line.Length == 0 ? string.Empty : $"{++number,6}\t{line}");
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static int Xargs(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "nI", stopAtFirstOperand: true);
        var command = options.Operands.Count > 0 ? options.Operands : new List<string> { "echo" };
        var batchSize = int.TryParse(options.Value('n'), out var parsed) ? parsed : 0;
        var marker = options.Value('I');

        var items = new List<string>();
        var text = context.Io.In.ReadToEnd();
        items.AddRange(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (items.Count == 0 && marker is null && batchSize == 0)
            return interpreter.Run(string.Join(' ', command.Select(Interpreter.SingleQuote)), context);

        var status = 0;

        if (marker is not null)
        {
            foreach (var item in items)
            {
                var script = string.Join(' ', command.Select(a => a.Contains(marker)
                    ? Interpreter.SingleQuote(a.Replace(marker, item))
                    : Interpreter.SingleQuote(a)));

                status = interpreter.Run(script, context);
            }

            return status;
        }

        var size = batchSize > 0 ? batchSize : items.Count;

        for (var i = 0; i < items.Count; i += size)
        {
            var batch = items.Skip(i).Take(size);
            var script = string.Join(' ', command.Concat(batch).Select(Interpreter.SingleQuote));
            status = interpreter.Run(script, context);
        }

        return status;
    }

    // ---------------------------------------------------------------- diff

    private static int Diff(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        if (options.Operands.Count < 2) throw new ShellException("missing operand", 2);

        var leftPath = context.ResolvePath(options.Operands[0]);
        var rightPath = context.ResolvePath(options.Operands[1]);

        if (!File.Exists(leftPath) || !File.Exists(rightPath))
        {
            Interpreter.WriteError(context, "diff: No such file or directory");
            return 2;
        }

        var left = File.ReadAllLines(leftPath);
        var right = File.ReadAllLines(rightPath);
        var lcs = LongestCommonSubsequence(left, right);

        var i = 0;
        var j = 0;
        var different = false;

        foreach (var (leftIndex, rightIndex) in lcs.Append((left.Length, right.Length)))
        {
            var removed = new List<string>();
            var added = new List<string>();

            while (i < leftIndex) removed.Add(left[i++]);
            while (j < rightIndex) added.Add(right[j++]);

            if (removed.Count > 0 || added.Count > 0)
            {
                different = true;
                context.Io.Out.WriteLine(Range(i - removed.Count, removed.Count) +
                    (removed.Count > 0 && added.Count > 0 ? "c" : removed.Count > 0 ? "d" : "a") +
                    Range(j - added.Count, added.Count));

                foreach (var line in removed) context.Io.Out.WriteLine("< " + line);
                if (removed.Count > 0 && added.Count > 0) context.Io.Out.WriteLine("---");
                foreach (var line in added) context.Io.Out.WriteLine("> " + line);
            }

            i = leftIndex + 1;
            j = rightIndex + 1;
            if (leftIndex >= left.Length || rightIndex >= right.Length) break;
        }

        context.Io.Out.Flush();
        return different ? 1 : 0;

        static string Range(int start, int count) =>
            count <= 1 ? (start + 1).ToString() : $"{start + 1},{start + count}";
    }

    private static List<(int Left, int Right)> LongestCommonSubsequence(string[] left, string[] right)
    {
        var lengths = new int[left.Length + 1, right.Length + 1];

        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = left[i] == right[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        var x = 0;
        var y = 0;

        while (x < left.Length && y < right.Length)
        {
            if (left[x] == right[y]) { pairs.Add((x, y)); x++; y++; }
            else if (lengths[x + 1, y] >= lengths[x, y + 1]) x++;
            else y++;
        }

        return pairs;
    }

    // ----------------------------------------------------------------- awk

    /// <summary>
    /// A deliberately small awk: field splitting, <c>NR</c>/<c>NF</c>, regex and expression
    /// patterns, <c>BEGIN</c>/<c>END</c>, and <c>print</c>. Enough for the one-liners that show
    /// up in pipelines, not a full implementation.
    /// </summary>
    private static int Awk(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "F", stopAtFirstOperand: false);
        var operands = options.Operands.ToList();
        if (operands.Count == 0) throw new ShellException("usage: awk [-F sep] 'program' [file ...]", 2);

        var program = operands[0];
        operands.RemoveAt(0);
        var separator = options.Value('F');

        var rules = AwkRule.ParseProgram(program);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        long recordNumber = 0;

        foreach (var rule in rules.Where(r => r.Kind == AwkRuleKind.Begin))
            rule.Execute(context, Array.Empty<string>(), string.Empty, recordNumber, variables);

        foreach (var (_, reader, _) in OpenInputs(context, operands, "awk"))
        {
            foreach (var line in ReadLines(reader))
            {
                recordNumber++;
                var fields = separator is null
                    ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    : line.Split(separator);

                foreach (var rule in rules.Where(r => r.Kind == AwkRuleKind.Normal))
                    if (rule.Applies(line, fields, recordNumber, variables))
                        rule.Execute(context, fields, line, recordNumber, variables);
            }
        }

        foreach (var rule in rules.Where(r => r.Kind == AwkRuleKind.End))
            rule.Execute(context, Array.Empty<string>(), string.Empty, recordNumber, variables);

        context.Io.Out.Flush();
        return 0;
    }

    private enum AwkRuleKind
    {
        Normal,
        Begin,
        End,
    }

    private sealed class AwkRule
    {
        public AwkRuleKind Kind { get; private init; }

        private Regex? _pattern;
        private string? _condition;
        private List<string> _statements = new();

        public static List<AwkRule> ParseProgram(string program)
        {
            var rules = new List<AwkRule>();
            var i = 0;

            while (i < program.Length)
            {
                while (i < program.Length && (char.IsWhiteSpace(program[i]) || program[i] == ';')) i++;
                if (i >= program.Length) break;

                var patternStart = i;
                while (i < program.Length && program[i] != '{') i++;
                var patternText = program[patternStart..i].Trim();

                var body = string.Empty;
                if (i < program.Length && program[i] == '{')
                {
                    var depth = 0;
                    var bodyStart = i + 1;

                    for (; i < program.Length; i++)
                    {
                        if (program[i] == '{') depth++;
                        else if (program[i] == '}' && --depth == 0) break;
                    }

                    body = program[bodyStart..Math.Min(i, program.Length)];
                    i++;
                }

                var rule = new AwkRule
                {
                    Kind = patternText == "BEGIN" ? AwkRuleKind.Begin
                        : patternText == "END" ? AwkRuleKind.End
                        : AwkRuleKind.Normal,
                };

                if (rule.Kind == AwkRuleKind.Normal && patternText.Length > 0)
                {
                    if (patternText.StartsWith('/') && patternText.EndsWith('/') && patternText.Length > 1)
                        rule._pattern = new Regex(patternText[1..^1]);
                    else
                        rule._condition = patternText;
                }

                rule._statements = SplitStatements(body.Length > 0 ? body : "print");
                rules.Add(rule);
            }

            return rules;
        }

        private static List<string> SplitStatements(string body)
        {
            var statements = new List<string>();
            var current = new StringBuilder();
            var inString = false;

            foreach (var c in body)
            {
                if (c == '"') inString = !inString;

                if ((c == ';' || c == '\n') && !inString)
                {
                    if (current.ToString().Trim().Length > 0) statements.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            if (current.ToString().Trim().Length > 0) statements.Add(current.ToString().Trim());
            return statements;
        }

        public bool Applies(string line, string[] fields, long recordNumber, Dictionary<string, string> variables)
        {
            if (_pattern is not null) return _pattern.IsMatch(line);
            if (_condition is null) return true;

            var evaluated = EvaluateCondition(_condition, fields, line, recordNumber, variables);
            return evaluated;
        }

        public void Execute(ShellContext context, string[] fields, string line, long recordNumber, Dictionary<string, string> variables)
        {
            foreach (var statement in _statements)
            {
                if (statement.StartsWith("print", StringComparison.Ordinal))
                {
                    var argument = statement[5..].Trim();

                    if (argument.Length == 0)
                    {
                        context.Io.Out.WriteLine(line);
                        continue;
                    }

                    var pieces = SplitTopLevel(argument, ',')
                        .Select(p => Resolve(p.Trim(), fields, line, recordNumber, variables));

                    context.Io.Out.WriteLine(string.Join(' ', pieces));
                    continue;
                }

                var equals = statement.IndexOf('=');
                if (equals > 0 && statement[equals - 1] is not ('!' or '<' or '>' or '='))
                {
                    var name = statement[..equals].Trim();
                    var value = Resolve(statement[(equals + 1)..].Trim(), fields, line, recordNumber, variables);
                    variables[name] = value;
                }
            }
        }

        private static List<string> SplitTopLevel(string text, char separator)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var inString = false;
            var depth = 0;

            foreach (var c in text)
            {
                if (c == '"') inString = !inString;
                else if (!inString && c == '(') depth++;
                else if (!inString && c == ')') depth--;

                if (c == separator && !inString && depth == 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            parts.Add(current.ToString());
            return parts;
        }

        private static string Resolve(string expression, string[] fields, string line, long recordNumber, Dictionary<string, string> variables)
        {
            expression = expression.Trim();

            if (expression.Length >= 2 && expression[0] == '"' && expression[^1] == '"') return expression[1..^1];
            if (expression == "$0") return line;
            if (expression == "NR") return recordNumber.ToString();
            if (expression == "NF") return fields.Length.ToString();

            if (expression.StartsWith('$') && int.TryParse(expression[1..], out var index))
                return index >= 1 && index <= fields.Length ? fields[index - 1] : string.Empty;

            if (variables.TryGetValue(expression, out var value)) return value;

            // Arithmetic over the pieces we can resolve, e.g. `sum + $2`.
            if (expression.Any(c => c is '+' or '-' or '*' or '/'))
            {
                var evaluated = EvaluateArithmetic(expression, fields, line, recordNumber, variables);
                if (evaluated is not null) return evaluated;
            }

            // An identifier that was never assigned is empty, which is how awk reads it. This is
            // what lets `sum = sum + $1` start from nothing and accumulate.
            if (IsIdentifier(expression)) return string.Empty;

            return expression;
        }

        private static bool IsIdentifier(string text) =>
            text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') &&
            text.All(c => char.IsLetterOrDigit(c) || c == '_');

        private static string? EvaluateArithmetic(string expression, string[] fields, string line, long recordNumber, Dictionary<string, string> variables)
        {
            var tokens = Regex.Split(expression, @"(?<=[-+*/])|(?=[-+*/])").Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (tokens.Count < 3) return null;

            if (!TryResolveNumber(tokens[0], fields, line, recordNumber, variables, out var accumulator)) return null;

            for (var i = 1; i + 1 < tokens.Count; i += 2)
            {
                if (!TryResolveNumber(tokens[i + 1], fields, line, recordNumber, variables, out var operand)) return null;

                accumulator = tokens[i] switch
                {
                    "+" => accumulator + operand,
                    "-" => accumulator - operand,
                    "*" => accumulator * operand,
                    "/" => operand == 0 ? accumulator : accumulator / operand,
                    _ => accumulator,
                };
            }

            return accumulator.ToString("G15", CultureInfo.InvariantCulture);
        }

        /// <summary>An operand is a number, or an unset variable — which awk counts as zero.</summary>
        private static bool TryResolveNumber(string token, string[] fields, string line, long recordNumber,
            Dictionary<string, string> variables, out double value)
        {
            var resolved = Resolve(token, fields, line, recordNumber, variables);

            if (resolved.Length == 0) { value = 0; return true; }
            return double.TryParse(resolved, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool EvaluateCondition(string condition, string[] fields, string line, long recordNumber, Dictionary<string, string> variables)
        {
            foreach (var op in new[] { "==", "!=", ">=", "<=", ">", "<", "~" })
            {
                var index = condition.IndexOf(op, StringComparison.Ordinal);
                if (index <= 0) continue;

                var left = Resolve(condition[..index].Trim(), fields, line, recordNumber, variables);
                var right = Resolve(condition[(index + op.Length)..].Trim(), fields, line, recordNumber, variables);

                var leftIsNumber = double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a);
                var rightIsNumber = double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b);
                var numeric = leftIsNumber && rightIsNumber;

                return op switch
                {
                    "==" => numeric ? a == b : left == right,
                    "!=" => numeric ? a != b : left != right,
                    ">=" => numeric ? a >= b : string.CompareOrdinal(left, right) >= 0,
                    "<=" => numeric ? a <= b : string.CompareOrdinal(left, right) <= 0,
                    ">" => numeric ? a > b : string.CompareOrdinal(left, right) > 0,
                    "<" => numeric ? a < b : string.CompareOrdinal(left, right) < 0,
                    "~" => Regex.IsMatch(left, right.Trim('/')),
                    _ => false,
                };
            }

            var resolved = Resolve(condition, fields, line, recordNumber, variables);
            return resolved.Length > 0 && resolved != "0";
        }
    }
}
