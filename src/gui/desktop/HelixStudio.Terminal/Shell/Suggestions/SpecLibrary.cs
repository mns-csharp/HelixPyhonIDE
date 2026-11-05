using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Suggestions;

/// <summary>
/// Every command spec the shell knows: the ones built into the assembly, then the user's own
/// from <c>~/.stt/specs</c>, which replace a built-in one of the same name. A file that does not
/// parse is skipped and its reason kept, so one bad file costs that command and nothing else.
/// </summary>
public sealed class SpecLibrary
{
    private const string ResourcePrefix = "Specs/";

    private readonly Dictionary<string, CommandSpec> _specs = new(StringComparer.OrdinalIgnoreCase);

    public static string UserDirectory => Path.Combine(ShellContext.HomeDirectory, ".stt", "specs");

    /// <summary>One line per file that could not be used: <c>name.json: reason</c>.</summary>
    public List<string> LoadErrors { get; } = new();

    public IEnumerable<CommandSpec> All => _specs.Values.Distinct();

    public static SpecLibrary Load(string? userDirectory)
    {
        var library = new SpecLibrary();
        library.LoadEmbedded();
        if (userDirectory is not null) library.LoadDirectory(userDirectory);
        return library;
    }

    public void Add(CommandSpec spec)
    {
        // Remove all keys pointing to the spec being replaced so an override drops old aliases.
        if (_specs.TryGetValue(spec.Name, out var existing))
        {
            var keysToRemove = _specs.Where(kv => ReferenceEquals(kv.Value, existing)).Select(kv => kv.Key).ToList();
            foreach (var key in keysToRemove) _specs.Remove(key);
        }

        _specs[spec.Name] = spec;
        foreach (var alias in spec.Aliases) _specs[alias] = spec;
    }

    /// <summary>Finds a command's spec however it was typed: any case, with a folder, with <c>.exe</c>.</summary>
    public CommandSpec? Find(string command)
    {
        var name = command.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        return _specs.TryGetValue(name, out var spec) ? spec : null;
    }

    public void LoadEmbedded()
    {
        var assembly = typeof(SpecLibrary).Assembly;

        foreach (var resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(".json", StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            LoadText(resource, reader.ReadToEnd());
        }
    }

    public void LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex)
        {
            LoadErrors.Add($"{directory}: {ex.Message}");
            return;
        }

        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            LoadText(Path.GetFileName(file), text);
        }
    }

    private void LoadText(string source, string json)
    {
        try
        {
            Add(CommandSpec.Parse(json));
        }
        catch (Exception ex)
        {
            LoadErrors.Add($"{source}: {ex.Message}");
        }
    }
}
