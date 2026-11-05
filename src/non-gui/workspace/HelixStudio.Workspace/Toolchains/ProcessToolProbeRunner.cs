using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>
    /// The real runner: resolves executables the way a shell does (PATH × PATHEXT), plus the
    /// well-known install directories a shell would NOT look in, and launches them windowless with
    /// a hard timeout.
    ///
    /// NFR-02 is not violated here — nothing is being ANALYSED by an external tool. Asking a
    /// toolchain what version it is is the only way to know what is installed, and the answer is
    /// the tool's own, which is exactly the "external tools as ground-truth oracle" case.
    /// </summary>
    public sealed class ProcessToolProbeRunner : IToolProbeRunner
    {
        private readonly int _timeoutMilliseconds;

        /// <summary>
        /// Directory → the file names in it, built once per directory and reused for every probe.
        /// A scan asks ~100 questions of the same few dozen directories; without this each one
        /// costs PATH × PATHEXT stat calls, and one unreachable network share on PATH is paid for
        /// again on every single probe. A runner lives for exactly one scan, so the cache cannot
        /// go stale within a session — Rescan builds a new one.
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, string>> _directoryFiles =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public ProcessToolProbeRunner(int timeoutMilliseconds = 6000)
        {
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        public string Resolve(ToolProbe probe)
        {
            if (probe == null || string.IsNullOrEmpty(probe.Executable)) return null;

            HashSet<string> excluded = Excluded(probe);
            foreach (string dir in SearchDirectories(probe))
            {
                if (excluded.Count > 0 && excluded.Contains(Key(dir))) continue;
                foreach (string candidate in Candidates(dir, probe.Executable))
                {
                    string onDisk = ExistingName(dir, candidate);
                    // The file's OWN name, not the candidate we built: PATHEXT is conventionally
                    // upper-case, so "kotlinc" + ".BAT" would put a path in the details pane and on
                    // the clipboard that does not match what the user sees in Explorer.
                    if (onDisk != null) return SafeCombine(dir, onDisk) ?? candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// The directories this probe refuses, whatever PATH says — see
        /// <see cref="ToolProbe.ExcludedDirectories"/>.
        /// </summary>
        private static HashSet<string> Excluded(ToolProbe probe)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string template in probe.ExcludedDirectories)
                foreach (string expanded in ExpandDirectory(template))
                    excluded.Add(Key(expanded));
            return excluded;
        }

        /// <summary>One comparable form for a directory: absolute, no trailing separator.</summary>
        private static string Key(string directory)
        {
            string trimmed = (directory ?? string.Empty).Trim().Trim('"');
            try { trimmed = Path.GetFullPath(trimmed); }
            catch (Exception) { }   // a malformed PATH entry compares as written
            return trimmed.TrimEnd('\\', '/');
        }

        public ProbeRun Run(ToolProbe probe, string executablePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(executablePath)) return ProbeRun.Failed("no executable");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = probe == null ? string.Empty : (probe.Arguments ?? string.Empty),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,   // a tool that decides to prompt gets EOF, not a hang
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = SafeWorkingDirectory(executablePath),
                };

                var captured = new StringBuilder();
                using (var process = new Process())
                using (var stdoutDone = new ManualResetEventSlim(false))
                using (var stderrDone = new ManualResetEventSlim(false))
                {
                    process.StartInfo = psi;
                    // Both streams are read on their own threads. Reading them one after the other
                    // deadlocks the moment a tool fills the pipe it is not being read from — and
                    // several of these print their banner to stderr.
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) stdoutDone.Set(); else lock (captured) captured.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) stderrDone.Set(); else lock (captured) captured.AppendLine(e.Data);
                    };

                    if (!process.Start()) return ProbeRun.Failed("could not start " + executablePath);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    WriteStandardInput(process, probe);

                    if (!WaitForExit(process, cancellationToken))
                    {
                        TryKill(process);
                        return ProbeRun.TimedOut();
                    }

                    // Let the pipe readers drain what the process wrote just before it exited.
                    stdoutDone.Wait(500);
                    stderrDone.Wait(500);
                    lock (captured) return ProbeRun.Ran(captured.ToString());
                }
            }
            catch (Exception ex)
            {
                // A probe is a question, never a failure of the IDE: report and carry on.
                return ProbeRun.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Hand an interpreter its one-line question, then close the pipe. Closing it is what makes
        /// the tool exit — a REPL that is still holding an open stdin sits there until the timeout.
        /// </summary>
        private static void WriteStandardInput(Process process, ToolProbe probe)
        {
            try
            {
                string script = probe == null ? null : probe.StandardInput;
                if (!string.IsNullOrEmpty(script)) process.StandardInput.Write(script);
            }
            catch (IOException) { }              // the tool closed its end first — nothing to say
            catch (ObjectDisposedException) { }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Wait in short slices so a cancelled scan does not sit on a hung tool.</summary>
        private bool WaitForExit(Process process, CancellationToken cancellationToken)
        {
            const int slice = 100;
            int waited = 0;
            while (waited < _timeoutMilliseconds)
            {
                if (process.WaitForExit(slice)) return true;
                if (cancellationToken.IsCancellationRequested) return false;
                waited += slice;
            }
            return false;
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
        }

        /// <summary>
        /// Run the tool from its own directory. The IDE's working directory may be a solution the
        /// tool would then try to interpret (npm reads a package.json, cargo a Cargo.toml), which
        /// turns a version query into real work against the user's project.
        /// </summary>
        private static string SafeWorkingDirectory(string executablePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(executablePath);
                return Directory.Exists(dir) ? dir : Environment.SystemDirectory;
            }
            catch (ArgumentException) { return Environment.SystemDirectory; }
            catch (PathTooLongException) { return Environment.SystemDirectory; }
        }

        /// <summary>
        /// PATH first — a tool the user's own shell would find is the one they mean — then the
        /// probe's install-root environment variables, then its well-known directories.
        /// </summary>
        private static IEnumerable<string> SearchDirectories(ToolProbe probe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = dir.Trim().Trim('"');
                if (trimmed.Length > 0) yield return trimmed;
            }

            foreach (string variable in probe.ExtraDirectoryEnvironmentVariables)
            {
                string root = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(root)) continue;
                yield return root;
                string bin = null;
                try { bin = Path.Combine(root, "bin"); }
                catch (ArgumentException) { }
                if (bin != null) yield return bin;
            }

            foreach (string template in probe.ExtraDirectories)
                foreach (string expanded in ExpandDirectory(template))
                    yield return expanded;
        }

        /// <summary>
        /// One <see cref="ToolProbe.ExtraDirectories"/> entry → the directories it actually names on
        /// this machine: environment variables substituted, and any <c>*</c> segment matched against
        /// the real filesystem (`%ProgramFiles%\Java\*\bin` → one path per installed JDK).
        ///
        /// An entry naming a variable this machine does not set expands to nothing rather than to a
        /// path with a literal <c>%VAR%</c> in it, which would be searched, always miss, and quietly
        /// cost a stat call per probe.
        /// </summary>
        internal static IEnumerable<string> ExpandDirectory(string template)
        {
            if (string.IsNullOrEmpty(template)) yield break;

            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(template); }
            catch (ArgumentException) { yield break; }
            if (expanded.IndexOf('%') >= 0) yield break;      // an unset variable — not a directory

            if (expanded.IndexOf('*') < 0)
            {
                yield return expanded;
                yield break;
            }

            foreach (string match in ExpandWildcards(expanded)) yield return match;
        }

        /// <summary>
        /// Resolve <c>*</c> segments left to right against the filesystem. Bounded on purpose: a
        /// pattern is a hint about where a toolchain installs itself, not a licence to walk a disk.
        /// </summary>
        private static IEnumerable<string> ExpandWildcards(string pattern)
        {
            const int limit = 64;
            string[] segments = pattern.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) yield break;

            // Rebuild the root from the original string so "C:\" and "/usr" both survive the split.
            string root = pattern.StartsWith("/", StringComparison.Ordinal)
                ? "/" + segments[0]
                : segments[0] + (segments[0].EndsWith(":", StringComparison.Ordinal) ? "\\" : string.Empty);
            var current = new List<string> { root };

            for (int i = 1; i < segments.Length && current.Count > 0; i++)
            {
                string segment = segments[i];
                var next = new List<string>();
                foreach (string dir in current)
                {
                    if (next.Count >= limit) break;
                    if (segment.IndexOf('*') < 0)
                    {
                        string combined = SafeCombine(dir, segment);
                        if (combined != null && SafeDirectoryExists(combined)) next.Add(combined);
                        continue;
                    }
                    foreach (string child in SafeChildDirectories(dir, segment))
                    {
                        next.Add(child);
                        if (next.Count >= limit) break;
                    }
                }
                current = next;
            }

            foreach (string dir in current) yield return dir;
        }

        private static bool SafeDirectoryExists(string path)
        {
            try { return Directory.Exists(path); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static IEnumerable<string> SafeChildDirectories(string parent, string pattern)
        {
            try { return Directory.GetDirectories(parent, pattern); }
            catch (IOException) { return new string[0]; }               // unreachable share, bad media
            catch (UnauthorizedAccessException) { return new string[0]; }
            catch (ArgumentException) { return new string[0]; }
        }

        /// <summary>
        /// The file's real name if this candidate exists in that directory, else null. Answered
        /// from one listing per directory rather than a stat per candidate — see
        /// <see cref="_directoryFiles"/>. A directory that cannot be listed is cached as empty, so
        /// an unreachable PATH entry costs one timeout for the whole scan rather than one per probe.
        /// </summary>
        private string ExistingName(string directory, string candidatePath)
        {
            Dictionary<string, string> files;
            if (!_directoryFiles.TryGetValue(directory, out files))
            {
                files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string file in Directory.GetFiles(directory))
                    {
                        string name = Path.GetFileName(file);
                        if (!files.ContainsKey(name)) files[name] = name;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                _directoryFiles[directory] = files;
            }

            try
            {
                string actual;
                return files.TryGetValue(Path.GetFileName(candidatePath), out actual) ? actual : null;
            }
            catch (ArgumentException) { return null; }
        }

        private static IEnumerable<string> Candidates(string directory, string executable)
        {
            // An entry that already carries an extension is tried as-is first.
            if (Path.HasExtension(executable))
            {
                string exact = SafeCombine(directory, executable);
                if (exact != null) yield return exact;
            }

            string pathext = Environment.GetEnvironmentVariable("PATHEXT");
            string[] extensions = string.IsNullOrEmpty(pathext)
                ? new[] { ".EXE", ".CMD", ".BAT", ".COM" }
                : pathext.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string ext in extensions)
            {
                string combined = SafeCombine(directory, executable + ext.Trim());
                if (combined != null) yield return combined;
            }

            // Non-Windows hosts have no PATHEXT convention: the bare name IS the file.
            string bare = SafeCombine(directory, executable);
            if (bare != null) yield return bare;
        }

        private static string SafeCombine(string directory, string file)
        {
            try { return Path.Combine(directory, file); }
            catch (ArgumentException) { return null; }
        }
    }
}
