using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Ai.Tools
{
    /// <summary>
    /// Run a command in the solution directory — pytest, uv, git, python.
    ///
    /// This is the most dangerous capability in the agent and is treated as such:
    /// <list type="bullet">
    /// <item>Risk is always <see cref="AgentToolRisk.Execute"/>, so the permission policy confirms
    /// every single call. There is no allow-list that makes a command run unattended, because a
    /// name-based allow-list is trivially defeated by arguments (<c>git</c> can rewrite history,
    /// <c>python</c> can do anything at all).</item>
    /// <item>The working directory is pinned to the workspace root. The command still runs with
    /// the IDE's full user privileges — this is NOT a sandbox, which is exactly why approval is
    /// mandatory and <see cref="AgentToolBase.Describe"/> shows the literal command line.</item>
    /// <item>Output is captured and truncated. An unbounded <c>pytest -v</c> would otherwise blow
    /// the model's context in one tool result.</item>
    /// </list>
    /// </summary>
    public sealed class RunCommandTool : AgentToolBase
    {
        /// <summary>Wall-clock ceiling. A hung process must not wedge the agent loop forever.</summary>
        public const int DefaultTimeoutSeconds = 120;

        /// <summary>Per-stream capture cap, in characters.</summary>
        public const int MaxCapturedChars = 20000;

        public RunCommandTool(AgentWorkspace workspace) : base(workspace) { }

        public override string Name { get { return "run_command"; } }

        /// <summary>Always Execute — see the class remarks for why this is never relaxed.</summary>
        public override AgentToolRisk Risk { get { return AgentToolRisk.Execute; } }

        public override string Description
        {
            get
            {
                return "Run a shell command in the solution directory and return its output and "
                     + "exit code. Use it to run the test suite, invoke uv or pip, or inspect git "
                     + "state. Prefer the read/grep/glob tools for inspecting files — they are "
                     + "faster and need no approval. Every call is shown to the user for approval "
                     + "before it runs, so state clearly what you are running and why.";
            }
        }

        public override string InputSchemaJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""command"": { ""type"": ""string"", ""description"": ""The command line to run, e.g. 'python -m pytest -q'."" },
    ""timeout_seconds"": { ""type"": ""integer"", ""description"": ""Optional wall-clock limit (default 120)."" }
  },
  ""required"": [""command""],
  ""additionalProperties"": false
}";
            }
        }

        /// <summary>
        /// The literal command line. The approval prompt is only meaningful if the user can see
        /// exactly what will execute, so this deliberately does not summarize or abbreviate.
        /// </summary>
        public override string Describe(string argumentsJson)
        {
            JObject args = ParseArgs(argumentsJson);
            string command = ReadString(args, "command");
            return "Run: " + (string.IsNullOrWhiteSpace(command) ? "?" : command);
        }

        public override async Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            JObject args = ParseArgs(argumentsJson);
            if (args == null) return AgentToolResult.Error("Arguments were not valid JSON.");

            string command = ReadString(args, "command");
            if (string.IsNullOrWhiteSpace(command))
                return AgentToolResult.Error("No command was given.");

            int timeoutSeconds = Math.Max(1, ReadInt(args, "timeout_seconds", DefaultTimeoutSeconds));

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /d skips AutoRun registry commands, which would otherwise inject whatever the
                // machine has configured into every agent command. /s /c keeps the quoted form intact.
                Arguments = "/d /s /c \"" + command + "\"",
                WorkingDirectory = Workspace.RootDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = startInfo;
                    process.EnableRaisingEvents = true;

                    // Async reads, not ReadToEnd: reading one stream to completion while the other
                    // fills its pipe buffer deadlocks a chatty process.
                    process.OutputDataReceived += (s, e) => Append(stdout, e.Data);
                    process.ErrorDataReceived += (s, e) => Append(stderr, e.Data);

                    if (!process.Start())
                        return AgentToolResult.Error("The process could not be started.");

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    bool exited = await WaitAsync(process, timeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);

                    if (!exited)
                    {
                        TryKill(process);
                        return AgentToolResult.Error(
                            "Timed out after " + timeoutSeconds + "s and was killed.\n"
                            + Combine(stdout, stderr));
                    }

                    string report = "Exit code " + process.ExitCode + "\n" + Combine(stdout, stderr);
                    // A non-zero exit is a normal, informative outcome (a failing test run), not a
                    // tool error — flagging it as one would tell the model the tool malfunctioned.
                    return AgentToolResult.Ok(report);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AgentToolResult.Error("Could not run the command: " + ex.Message);
            }
        }

        private static async Task<bool> WaitAsync(Process process, int timeoutSeconds, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!process.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(50).ConfigureAwait(false);
            }
            // Let the async output handlers drain the last buffered lines before we read them.
            process.WaitForExit(500);
            return true;
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
        }

        private static void Append(StringBuilder buffer, string line)
        {
            if (line == null) return;
            lock (buffer)
            {
                if (buffer.Length >= MaxCapturedChars) return;
                buffer.AppendLine(line);
            }
        }

        private static string Combine(StringBuilder stdout, StringBuilder stderr)
        {
            string outText, errText;
            lock (stdout) { outText = stdout.ToString(); }
            lock (stderr) { errText = stderr.ToString(); }

            var sb = new StringBuilder();
            if (outText.Length > 0) sb.Append(Truncate(outText));
            if (errText.Length > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("--- stderr ---");
                sb.Append(Truncate(errText));
            }
            return sb.Length == 0 ? "(no output)" : sb.ToString();
        }

        private static string Truncate(string s)
        {
            if (s.Length <= MaxCapturedChars) return s;
            return s.Substring(0, MaxCapturedChars)
                 + "\n… output truncated at " + MaxCapturedChars + " characters.";
        }
    }
}
