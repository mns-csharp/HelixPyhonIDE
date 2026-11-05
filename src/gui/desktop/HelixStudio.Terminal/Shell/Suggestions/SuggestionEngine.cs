using System.Text;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Suggestions;

public enum SuggestionKind
{
    Command,
    Subcommand,
    Option,
    Folder,
    File,
    Variable,
    Job,
    Branch,
    Remote,
    History,
    Value,
}

/// <summary>
/// One thing that could be typed. <see cref="InsertText"/> replaces the word being typed;
/// <see cref="DisplayText"/> is what the list shows, and <see cref="MatchedPositions"/> index into
/// it. <see cref="FullPath"/> is set for files and folders, so a side pane can describe them only
/// when they are looked at rather than for every item on every keystroke.
/// </summary>
public sealed record Suggestion(
    string InsertText,
    string DisplayText,
    SuggestionKind Kind,
    string Description,
    string Detail,
    string? FullPath,
    int[] MatchedPositions,
    MatchClass Class,
    double Score);

public sealed class SuggestionSet
{
    public int ReplaceStart { get; init; }

    public int ReplaceLength { get; init; }

    public IReadOnlyList<Suggestion> Items { get; init; } = Array.Empty<Suggestion>();

    /// <summary>What a first Tab inserts: the longest text every prefix match shares.</summary>
    public string CommonPrefix { get; init; } = string.Empty;

    /// <summary>True when a generator was skipped; asking again with generators will add to this.</summary>
    public bool Pending { get; init; }

    /// <summary>The line and caret this answer was computed for.</summary>
    public string Line { get; init; } = string.Empty;

    public int Caret { get; init; }

    /// <summary>
    /// False once the line has moved on. The replace span only makes sense against the text it was
    /// measured on; applied to any other it cuts the wrong characters.
    /// </summary>
    public bool IsFor(string line, int caret) => caret == Caret && line == Line;

    public static SuggestionSet Empty(int caret) => new() { ReplaceStart = caret };
}

/// <summary>
/// Everything that could be typed at the caret, best first. It reads what the caret is on, asks the
/// providers that fit, ranks what they offer by how the letters line up and then by habit, and says
/// what span accepting would replace. It is shared by the popup and by Tab so the two agree.
/// </summary>
public sealed class SuggestionEngine
{
    private const int MaximumItems = 50;

    private readonly GeneratorCache _generators;
    private readonly object _pathLock = new();
    private string? _pathValue;
    private List<(string Name, string FullPath)> _programs = new();

    public SuggestionEngine(SpecLibrary specs, UsageStats usage, GeneratorCache generators)
    {
        Specs = specs;
        Usage = usage;
        _generators = generators;
    }

    public SpecLibrary Specs { get; }

    public UsageStats Usage { get; }

    public SuggestionSet Suggest(string line, int caret, ShellContext context, bool includeGenerators = false, CancellationToken token = default)
    {
        try
        {
            caret = Math.Clamp(caret, 0, line.Length);
            var at = LineAnalyzer.Analyze(line, caret, Specs);
            var candidates = new List<Suggestion>();
            var pending = false;

            switch (at.Position)
            {
                case LinePosition.Command:
                    AddCommandPosition(candidates, at.Word, context);
                    break;

                case LinePosition.Option:
                    AddOptions(candidates, at);
                    break;

                case LinePosition.Variable:
                    AddVariables(candidates, at.Word, context);
                    break;

                case LinePosition.Redirect:
                    AddPaths(candidates, at.Word, ArgumentKinds.Path, context);
                    break;

                case LinePosition.Argument:
                case LinePosition.OptionValue:
                    if (at.OffersSubcommands) AddSubcommands(candidates, at);
                    pending = AddByKind(candidates, at, context, includeGenerators, token);
                    break;
            }

            var items = candidates
                .OrderByDescending(s => s.Class)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.DisplayText, StringComparer.OrdinalIgnoreCase)
                .DistinctBy(s => s.InsertText, StringComparer.Ordinal)
                .Take(MaximumItems)
                .ToList();

            return new SuggestionSet
            {
                ReplaceStart = at.WordStart,
                ReplaceLength = at.WordLength,
                Items = items,
                CommonPrefix = CommonPrefix(items),
                Pending = pending,
                Line = line,
                Caret = caret,
            };
        }
        catch (Exception)
        {
            caret = Math.Clamp(caret, 0, line.Length);
            return new SuggestionSet { ReplaceStart = caret, Line = line, Caret = caret };
        }
    }

    /// <summary>For the side pane: what a folder holds, or a file's size and date.</summary>
    public static string DescribePath(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                var count = Directory.EnumerateFileSystemEntries(fullPath).Count();
                return $"Folder · {count} item{(count == 1 ? "" : "s")}\n{fullPath.Replace('\\', '/')}";
            }

            var file = new FileInfo(fullPath);
            if (file.Exists)
                return $"{FormatSize(file.Length)} · modified {file.LastWriteTime:yyyy-MM-dd HH:mm}\n{fullPath.Replace('\\', '/')}";
        }
        catch (Exception)
        {
            // Unreadable: the pane shows the path alone.
        }

        return fullPath.Replace('\\', '/');
    }

    // ------------------------------------------------------------------ providers

    private void AddCommandPosition(List<Suggestion> into, string word, ShellContext context)
    {
        if (word.Contains('/') || word.Contains('\\') || word.StartsWith('.'))
        {
            AddPaths(into, word, ArgumentKinds.Path, context);
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string description, string detail, SuggestionKind kind)
        {
            if (!seen.Add(name)) return;
            var match = FuzzyMatcher.Match(name, word);
            if (match is null) return;
            into.Add(new Suggestion(Escape(name), name, kind, description, detail, null, match.Positions,
                match.Class, match.Score + Usage.Boost(name)));
        }

        var builtins = context.Interpreter?.Builtins;
        if (builtins is not null)
        {
            foreach (var name in builtins.Names)
            {
                var spec = Specs.Find(name);
                Add(name, spec?.Description is { Length: > 0 } d ? d : "builtin", builtins.HelpFor(name), SuggestionKind.Command);
            }
        }

        foreach (var name in context.Functions.Keys) Add(name, "shell function", name + " ()", SuggestionKind.Command);
        foreach (var pair in context.Aliases) Add(pair.Key, $"alias for '{pair.Value}'", pair.Value, SuggestionKind.Command);
        foreach (var (name, fullPath) in Programs(context)) Add(name, Specs.Find(name)?.Description is { Length: > 0 } d ? d : "program", fullPath.Replace('\\', '/'), SuggestionKind.Command);

        foreach (var line in context.History)
        {
            var first = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null && !first.Contains('=')) Add(first, "from history", "from history", SuggestionKind.History);
        }
    }

    private void AddOptions(List<Suggestion> into, LineContext at)
    {
        if (at.Spec is null) return;

        foreach (var option in at.Spec.Options)
        {
            var display = string.Join(", ", option.Names);
            FuzzyMatch? best = null;
            var bestName = option.Names.FirstOrDefault() ?? string.Empty;
            var offset = 0;
            var bestOffset = 0;

            foreach (var name in option.Names)
            {
                var match = FuzzyMatcher.Match(name, at.Word);
                if (match is not null && (best is null || match.Class > best.Class || (match.Class == best.Class && match.Score > best.Score)))
                {
                    best = match;
                    bestName = name;
                    bestOffset = offset;
                }

                offset += name.Length + 2;
            }

            if (best is null) continue;

            var positions = best.Positions.Select(p => p + bestOffset).ToArray();
            var detail = option.Value is null ? option.Description : $"{display} <{option.Value}>\n{option.Description}";
            into.Add(new Suggestion(bestName, display, SuggestionKind.Option, option.Description, detail, null, positions, best.Class, best.Score));
        }
    }

    private void AddSubcommands(List<Suggestion> into, LineContext at)
    {
        if (at.Spec is null) return;

        var prefix = string.Join(' ', at.CommandPath);

        foreach (var sub in at.Spec.Subcommands)
        {
            var match = FuzzyMatcher.Match(sub.Name, at.Word);
            if (match is null) continue;

            var detail = sub.Synopsis.Length > 0 ? $"{sub.Synopsis}\n{sub.Description}" : $"{prefix} {sub.Name}\n{sub.Description}";
            into.Add(new Suggestion(sub.Name, sub.Name, SuggestionKind.Subcommand, sub.Description, detail, null,
                match.Positions, match.Class, match.Score + Usage.Boost(prefix + " " + sub.Name)));
        }
    }

    /// <summary>Adds what an argument of the given kind can be; true when a generator was skipped.</summary>
    private bool AddByKind(List<Suggestion> into, LineContext at, ShellContext context, bool includeGenerators, CancellationToken token)
    {
        var kind = at.ArgumentKind;
        if (kind is null) return false;

        switch (kind)
        {
            case ArgumentKinds.Folder:
            case ArgumentKinds.File:
            case ArgumentKinds.Path:
                AddPaths(into, at.Word, kind, context);
                return false;

            case ArgumentKinds.Variable:
                AddVariables(into, at.Word, context, bare: true);
                return false;

            case ArgumentKinds.Job:
                foreach (var job in context.Jobs.All())
                    AddValue(into, "%" + job.Id, at.Word, job.Command, SuggestionKind.Job);
                return false;

            case ArgumentKinds.Command:
                AddCommandPosition(into, at.Word, context);
                return false;

            case ArgumentKinds.Choice:
                foreach (var choice in at.Choices) AddValue(into, choice.Name, at.Word, choice.Description, SuggestionKind.Value);
                return false;
        }

        if (!ArgumentKinds.IsGenerator(kind)) return false;

        // Git may be missing, or the folder may not be a repository; either way a local path is
        // still worth offering for a branch or a file, and offering it never waits on the
        // generator or its failure. A remote name is never a path, so it gets no fallback.
        if (kind is ArgumentKinds.GitBranch or ArgumentKinds.GitFile)
            AddPaths(into, at.Word, ArgumentKinds.Path, context);

        var values = includeGenerators
            ? _generators.Run(kind, context.WorkingDirectory, token)
            : _generators.TryGetCached(kind, context.WorkingDirectory);

        if (values is null) return true;

        var suggestionKind = kind switch
        {
            ArgumentKinds.GitBranch => SuggestionKind.Branch,
            ArgumentKinds.GitRemote => SuggestionKind.Remote,
            _ => SuggestionKind.File,
        };

        foreach (var value in values) AddValue(into, value, at.Word, kind.Replace("git-", "git "), suggestionKind);
        return false;
    }

    private static void AddValue(List<Suggestion> into, string value, string word, string description, SuggestionKind kind)
    {
        var match = FuzzyMatcher.Match(value, word);
        if (match is null) return;
        into.Add(new Suggestion(Escape(value), value, kind, description, description, null, match.Positions, match.Class, match.Score));
    }

    private static void AddVariables(List<Suggestion> into, string word, ShellContext context, bool bare = false)
    {
        var braced = word.StartsWith("${", StringComparison.Ordinal);
        var typed = bare ? word : word.TrimStart('$').TrimStart('{');

        foreach (var variable in context.AllVariables())
        {
            var match = FuzzyMatcher.Match(variable.Name, typed);
            if (match is null) continue;

            var value = variable.Value ?? string.Empty;
            var shown = value.Length > 60 ? value[..57] + "..." : value;
            var insert = bare ? variable.Name : braced ? "${" + variable.Name + "}" : "$" + variable.Name;

            into.Add(new Suggestion(insert, variable.Name, SuggestionKind.Variable, shown, value, null, match.Positions, match.Class, match.Score));
        }
    }

    private static void AddPaths(List<Suggestion> into, string word, string kind, ShellContext context)
    {
        var separator = word.LastIndexOfAny(new[] { '/', '\\' });
        var folderPart = separator < 0 ? string.Empty : word[..(separator + 1)];
        var namePart = separator < 0 ? word : word[(separator + 1)..];

        string folder;
        try
        {
            folder = folderPart.Length == 0 ? context.WorkingDirectory : context.ResolvePath(folderPart);
        }
        catch (Exception)
        {
            return;
        }

        if (!Directory.Exists(folder)) return;

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception)
        {
            return;
        }

        var showHidden = namePart.StartsWith('.');

        foreach (var entry in entries)
        {
            var isFolder = entry is DirectoryInfo;
            // A file argument still lists folders, so a file inside one can be reached.
            if (kind == ArgumentKinds.Folder && !isFolder) continue;
            if (!showHidden && (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

            var match = FuzzyMatcher.Match(entry.Name, namePart);
            if (match is null) continue;

            var display = entry.Name + (isFolder ? "/" : string.Empty);
            // Forward slashes: the shell reads a backslash as an escape, so C:\Users would run as C:Users.
            var insert = Escape(folderPart.Replace('\\', '/') + entry.Name) + (isFolder ? "/" : string.Empty);

            into.Add(new Suggestion(insert, display, isFolder ? SuggestionKind.Folder : SuggestionKind.File,
                isFolder ? "folder" : FormatSize(entry is FileInfo f ? f.Length : 0), string.Empty, entry.FullName,
                match.Positions, match.Class, match.Score));
        }
    }

    /// <summary>Programs on PATH, scanned again only when PATH itself changes.</summary>
    private List<(string Name, string FullPath)> Programs(ShellContext context)
    {
        var pathValue = context.GetValue("PATH") ?? string.Empty;

        lock (_pathLock)
        {
            if (pathValue == _pathValue) return _programs;

            var programs = new List<(string, string)>();
            var separators = pathValue.Contains(';') ? ';' : ':';

            foreach (var directory in pathValue.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string resolved;
                try { resolved = context.ResolvePath(directory.Trim()); }
                catch (Exception) { continue; }

                if (!Directory.Exists(resolved)) continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(resolved))
                    {
                        var extension = Path.GetExtension(file);
                        if (extension.Length > 0 && extension.ToLowerInvariant() is not (".exe" or ".cmd" or ".bat" or ".com")) continue;
                        programs.Add((Path.GetFileNameWithoutExtension(file), file));
                    }
                }
                catch (Exception)
                {
                    // An unreadable PATH entry contributes nothing.
                }
            }

            _pathValue = pathValue;
            _programs = programs;
            return programs;
        }
    }

    private static string CommonPrefix(List<Suggestion> items)
    {
        var prefixes = items.Where(s => s.Class >= MatchClass.Prefix).Select(s => s.InsertText).ToList();
        if (prefixes.Count == 0) return string.Empty;

        var prefix = prefixes[0];
        foreach (var text in prefixes.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < text.Length &&
                   char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(text[length]))
                length++;
            prefix = prefix[..length];
        }

        return prefix;
    }

    /// <summary>
    /// Backslashes every character the shell would otherwise read as syntax, so an accepted name
    /// runs as one word. The lexer drops a backslash before any character, so over-escaping is safe.
    /// A tilde is left alone so ~/ still means the home folder.
    /// </summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (" \t'\"\\|&;()<>$`*?[]{}#!".Contains(ch)) escaped.Append('\\');
            escaped.Append(ch);
        }

        return escaped.ToString();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}
