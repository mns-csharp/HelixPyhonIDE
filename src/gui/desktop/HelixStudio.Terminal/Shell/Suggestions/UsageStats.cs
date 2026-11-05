namespace HelixPythonNamespace.Terminal.Shell.Suggestions;

/// <summary>
/// How often and how recently each command, and each command with its subcommand, was run. The
/// boost it gives is always below one, and the engine adds it inside a match class only, so a
/// habit can reorder equally good matches but never promote a worse match over a better one.
/// </summary>
public sealed class UsageStats
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Count, long Last)> _uses = new(StringComparer.OrdinalIgnoreCase);
    private long _clock;

    public static UsageStats From(IEnumerable<string> history)
    {
        var stats = new UsageStats();
        foreach (var line in history) stats.Record(line);
        return stats;
    }

    public void Record(string line)
    {
        var words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;

        lock (_lock)
        {
            _clock++;
            Bump(words[0]);
            if (words.Length > 1 && !words[1].StartsWith('-')) Bump(words[0] + " " + words[1]);
        }
    }

    public double Boost(string key)
    {
        lock (_lock)
        {
            if (!_uses.TryGetValue(key, out var use)) return 0;

            // Frequency saturates, so a command used ten thousand times is not infinitely ahead,
            // and it leads; recency — how late in history it was last used — breaks near-ties.
            var frequency = 1 - 1 / (1 + Math.Log(1 + use.Count));
            var recency = _clock == 0 ? 0 : (double)use.Last / _clock;
            return Math.Min(0.999, 0.89 * frequency + 0.1 * recency);
        }
    }

    private void Bump(string key)
    {
        _uses.TryGetValue(key, out var use);
        _uses[key] = (use.Count + 1, _clock);
    }
}
