using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>
    /// Runs the project's own interpreter to ask what it is and what it has installed.
    ///
    /// Deliberately the SAME interpreter the run will use, not one found on PATH. A capture that
    /// described a different Python than the one that produced the result would be worse than no
    /// capture — it would look like provenance and be fiction.
    ///
    /// Bounded and windowless, like every other probe here: this runs on the path to launching the
    /// user's program, so a hung <c>pip freeze</c> must cost a few seconds and then be reported as
    /// a timeout, never hold up the run.
    /// </summary>
    public sealed class ProcessInterpreterProbe : IInterpreterProbe
    {
        private readonly int _timeoutMilliseconds;

        public ProcessInterpreterProbe(int timeoutMilliseconds = 15000)
        {
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        public ProbeRun Run(string executable, string arguments, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(executable)) return ProbeRun.Failed("no interpreter path");

            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // pip prints its "a new release is available" notice on a whim; the capture wants the
            // package list, not advice.
            info.EnvironmentVariables["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            try
            {
                using (var process = new Process { StartInfo = info })
                {
                    var output = new StringBuilder();
                    object gate = new object();

                    process.OutputDataReceived += (s, e) =>
                    { if (e.Data != null) lock (gate) output.Append(e.Data).Append('\n'); };
                    process.ErrorDataReceived += (s, e) =>
                    { if (e.Data != null) lock (gate) output.Append(e.Data).Append('\n'); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!WaitFor(process, cancellationToken))
                    {
                        TryKill(process);
                        return ProbeRun.TimedOut();
                    }

                    string text;
                    lock (gate) text = output.ToString();

                    // A non-zero exit means the answer is not usable, whatever it printed — a
                    // "No module named pip" banner is not a package list.
                    return process.ExitCode == 0 ? ProbeRun.Ran(text) : ProbeRun.Failed(text.Trim());
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return ProbeRun.Failed(ex.Message); }
        }

        private bool WaitFor(Process process, CancellationToken cancellationToken)
        {
            int waited = 0;
            const int slice = 100;
            while (waited < _timeoutMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.WaitForExit(slice))
                {
                    process.WaitForExit();   // let the async readers drain
                    return true;
                }
                waited += slice;
            }
            return false;
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception) { }
        }
    }
}
