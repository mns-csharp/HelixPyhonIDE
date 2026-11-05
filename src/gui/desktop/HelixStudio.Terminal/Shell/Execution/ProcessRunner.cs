using System.Diagnostics;
using System.Text;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Execution;

/// <summary>A stream whose blocking reads can be released, so a finished child does not strand a pump thread.</summary>
public interface IUnblockableInput
{
    void UnblockPendingReads();
}

/// <summary>Finds and runs external programs, wiring their standard streams to the shell's.</summary>
public static class ProcessRunner
{
    private static readonly string[] DefaultExtensions = { ".COM", ".EXE", ".BAT", ".CMD" };

    /// <summary>
    /// Resolves a command name to an executable path using PATH and PATHEXT, or null when there
    /// is no such command. A name containing a separator is resolved as a path instead.
    /// </summary>
    public static string? Resolve(string name, ShellContext context)
    {
        if (name.Length == 0) return null;

        if (name.Contains('/') || name.Contains('\\'))
        {
            var direct = context.ResolvePath(name);
            return FindWithExtension(direct);
        }

        var pathValue = context.GetValue("PATH") ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = new List<string> { context.WorkingDirectory };
        directories.AddRange(SplitPath(pathValue, context));

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            string candidate;
            try { candidate = Path.Combine(directory, name); }
            catch (ArgumentException) { continue; }

            var found = FindWithExtension(candidate);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>PATH may hold Windows (<c>;</c>) or Unix (<c>:</c>) separators depending on where it came from.</summary>
    private static IEnumerable<string> SplitPath(string pathValue, ShellContext context)
    {
        var entries = pathValue.Contains(';')
            ? pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries)
            : pathValue.Split(':', StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;

            string resolved;
            try { resolved = context.ResolvePath(trimmed); }
            catch (Exception) { continue; }

            yield return resolved;
        }
    }

    private static string? FindWithExtension(string candidate)
    {
        if (File.Exists(candidate) && Path.GetExtension(candidate).Length > 0) return candidate;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Join(';', DefaultExtensions))
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var extension in extensions)
        {
            var withExtension = candidate + extension.Trim();
            if (File.Exists(withExtension)) return withExtension;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Runs an external program to completion and returns its exit code.</summary>
    public static int Run(
        string executable,
        IReadOnlyList<string> arguments,
        ShellContext context,
        ShellIo io,
        IDictionary<string, string>? extraEnvironment,
        Job? job)
    {
        var (fileName, argumentList) = ResolveInvocation(executable, arguments);
        var environment = BuildEnvironment(context, extraEnvironment);

        if (WantsPseudoConsole(context, io) &&
            ConPty.TryRun(fileName, argumentList, context, io, environment, job, out var status))
            return status;

        return RunWithPipes(fileName, argumentList, executable, context, io, environment, job);
    }

    /// <summary>
    /// Whether the child should get a console of its own rather than three pipes. It needs the
    /// whole screen to itself, which rules out redirection, pipelines and background jobs; a
    /// program that only reads and writes will not notice the difference, but an interactive one
    /// cannot work without it.
    /// </summary>
    private static bool WantsPseudoConsole(ShellContext context, ShellIo io)
    {
        if (context.IsBackgroundJob || !io.IsFullyTerminal || !ConPty.IsSupported) return false;

        // An escape hatch for the case where a console turns out to be the wrong thing.
        return context.GetValue("STT_NO_CONPTY") is null or "" or "0";
    }

    /// <summary>
    /// The commands cmd.exe implements itself rather than shipping as programs on the path.
    ///
    /// Nothing resolves them, so <c>copy</c>, <c>ver</c> and <c>assoc</c> are reported as not
    /// found however plainly they exist on a Windows machine. Names this shell has a builtin of
    /// its own for never reach the lookup that uses this, so <c>dir</c>, <c>type</c> and
    /// <c>start</c> stay the shell's. Left out are the ones that only change the state of the
    /// interpreter running them — <c>set</c>, <c>cd</c>, <c>call</c>, <c>setlocal</c> — because a
    /// child that exits immediately afterwards makes those a quiet no-op that looks like a
    /// working command.
    /// </summary>
    private static readonly HashSet<string> CmdIntrinsics = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "color", "copy", "del", "erase", "ftype", "md", "mklink", "move", "path",
        "pause", "rd", "ren", "rename", "time", "title", "ver", "verify", "vol",
    };

    /// <summary>
    /// Rewrites a command cmd implements itself into a run of cmd that performs it. False for
    /// anything else, which leaves the caller to report it as not found.
    /// </summary>
    public static bool TryUseCmdIntrinsic(
        string name,
        IReadOnlyList<string> operands,
        ShellContext context,
        out string interpreter,
        out List<string> arguments)
    {
        interpreter = string.Empty;
        arguments = new List<string>();

        if (!CmdIntrinsics.Contains(name)) return false;

        interpreter = context.GetValue("ComSpec")
                      ?? Environment.GetEnvironmentVariable("ComSpec")
                      ?? "cmd.exe";

        arguments.Add("/c");
        arguments.Add(name);
        arguments.AddRange(operands);
        return true;
    }

    /// <summary>Points .bat and .cmd at the interpreter that can actually run them.</summary>
    private static (string FileName, IReadOnlyList<string> Arguments) ResolveInvocation(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(executable).ToLowerInvariant();
        if (extension is not (".bat" or ".cmd")) return (executable, arguments);

        var wrapped = new List<string> { "/c", executable };
        wrapped.AddRange(arguments);
        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", wrapped);
    }

    private static Dictionary<string, string> BuildEnvironment(ShellContext context, IDictionary<string, string>? extra)
    {
        var environment = context.BuildEnvironment();
        if (extra is not null)
            foreach (var pair in extra) environment[pair.Key] = pair.Value;

        return environment;
    }

    private static int RunWithPipes(
        string fileName,
        IReadOnlyList<string> arguments,
        string executable,
        ShellContext context,
        ShellIo io,
        Dictionary<string, string> environment,
        Job? job)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start()) throw new ShellException($"{executable}: failed to start");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ShellException($"{Path.GetFileName(executable)}: {ex.Message}", 126);
        }

        job?.Processes.Add(process);

        var outputPump = PumpAsync(process.StandardOutput.BaseStream, io.Output);
        var errorPump = PumpAsync(process.StandardError.BaseStream, io.Error);
        var inputPump = PumpInputAsync(io.Input, process);

        try
        {
            while (!process.WaitForExit(100))
            {
                if (!context.CancellationToken.IsCancellationRequested && job?.Cancellation.IsCancellationRequested != true)
                    continue;

                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already exiting */ }
                break;
            }

            process.WaitForExit();
        }
        finally
        {
            // Releasing the input side first lets the pump thread notice the child is gone.
            (io.Input as IUnblockableInput)?.UnblockPendingReads();
            try { process.StandardInput.Close(); } catch (Exception) { }

            outputPump.Join(2000);
            errorPump.Join(2000);
            inputPump?.Join(200);
            io.Flush();
        }

        return process.ExitCode;
    }

    private static Thread PumpAsync(Stream from, Stream to)
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[8192];

            try
            {
                int read;
                while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    to.Write(buffer, 0, read);
                    to.Flush();
                }
            }
            catch (Exception)
            {
                // The pipe closed underneath us; nothing useful to report.
            }
        })
        { IsBackground = true, Name = "stt-pump-out" };

        thread.Start();
        return thread;
    }

    private static Thread? PumpInputAsync(Stream from, Process process)
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[4096];
            var target = process.StandardInput.BaseStream;

            try
            {
                int read;
                while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (process.HasExited) break;
                    target.Write(buffer, 0, read);
                    target.Flush();
                }
            }
            catch (Exception)
            {
                // Child exited or closed stdin.
            }

            try { target.Close(); } catch (Exception) { }
        })
        { IsBackground = true, Name = "stt-pump-in" };

        thread.Start();
        return thread;
    }
}
