using System.Diagnostics;

namespace HelixPythonNamespace.Terminal.Shell.Suggestions;

/// <summary>Produces live values for an argument kind by asking a program, e.g. git's branches.</summary>
public delegate IReadOnlyList<string> Generator(string workingDirectory, CancellationToken token);

/// <summary>
/// Runs generators with a time limit and keeps each answer for a short while per folder and kind.
/// A failure or a timeout is kept too, as an empty answer: a machine without git, or a folder that
/// is not a repository, would otherwise pay for a failed process start on every keystroke.
/// </summary>
public sealed class GeneratorCache
{
    private readonly IReadOnlyDictionary<string, Generator> _generators;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<(string Kind, string Directory), (DateTime At, IReadOnlyList<string> Values)> _answers = new();
    private readonly Dictionary<(string Kind, string Directory), Task<IReadOnlyList<string>>> _running = new();
    private readonly object _lock = new();

    public GeneratorCache(IReadOnlyDictionary<string, Generator> generators, TimeSpan timeout, TimeSpan lifetime, Func<DateTime>? clock = null)
    {
        _generators = generators;
        _timeout = timeout;
        _lifetime = lifetime;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public IReadOnlyList<string>? TryGetCached(string kind, string directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;

        lock (_lock)
        {
            return _answers.TryGetValue((kind, directory.ToLowerInvariant()), out var answer) &&
                   _clock() - answer.At < _lifetime
                ? answer.Values
                : null;
        }
    }

    public IReadOnlyList<string> Run(string kind, string directory, CancellationToken token)
    {
        if (string.IsNullOrEmpty(directory)) return Array.Empty<string>();

        var cached = TryGetCached(kind, directory);
        if (cached is not null) return cached;

        var key = (kind, directory.ToLowerInvariant());
        Task<IReadOnlyList<string>>? work = null;

        lock (_lock)
        {
            if (_running.TryGetValue(key, out var running))
            {
                work = running;
            }
            else if (_generators.TryGetValue(kind, out var generator))
            {
                var limit = new CancellationTokenSource();
                limit.CancelAfter(_timeout);

                work = Task.Run(async () =>
                {
                    IReadOnlyList<string> result;
                    try
                    {
                        result = await Task.Run(() => generator(directory, limit.Token), limit.Token);
                    }
                    catch (Exception)
                    {
                        result = Array.Empty<string>();
                    }
                    finally
                    {
                        limit.Dispose();
                    }

                    lock (_lock)
                    {
                        _answers[key] = (_clock(), result);
                        _running.Remove(key);
                    }

                    return result;
                });

                _running[key] = work;
            }
        }

        if (work is null) return Array.Empty<string>();

        try
        {
            if (work.Wait(_timeout + TimeSpan.FromMilliseconds(50), token))
            {
                return work.Result;
            }
        }
        catch (OperationCanceledException)
        {
            // Caller was cancelled, don't return anything.
        }

        return Array.Empty<string>();
    }
}

/// <summary>Values that only git knows: its branches, its remotes and the files it sees changed.</summary>
public static class GitGenerators
{
    public static IReadOnlyDictionary<string, Generator> All { get; } = new Dictionary<string, Generator>
    {
        [ArgumentKinds.GitBranch] = (directory, token) =>
            Lines(Run(directory, "branch --all --format=%(refname:short)", token))
                .Where(b => !b.EndsWith("/HEAD", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        [ArgumentKinds.GitRemote] = (directory, token) => Lines(Run(directory, "remote", token)).ToList(),
        [ArgumentKinds.GitFile] = (directory, token) => ParseStatus(Run(directory, "status --porcelain", token)),
    };

    /// <summary>
    /// Turns <c>git status --porcelain</c> into paths: two status letters and a space, then the
    /// path, or <c>old -&gt; new</c> for a rename, of which the new name is the one that exists.
    /// </summary>
    public static IReadOnlyList<string> ParseStatus(string porcelain) =>
        Lines(porcelain)
            .Where(line => line.Length > 3)
            .Select(line => line[3..])
            .Select(path => path.Contains(" -> ") ? path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..] : path)
            .Select(path => path.Trim('"'))
            .ToList();

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    private static string Run(string directory, string arguments, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();
        using var registration = token.Register(() => { try { process.Kill(); } catch (Exception) { } });

        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        errors.Wait();
        token.ThrowIfCancellationRequested();

        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments} exited {process.ExitCode}");
        return output;
    }
}
