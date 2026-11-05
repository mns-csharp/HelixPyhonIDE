namespace HelixPythonNamespace.Launcher
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Starting the user's program. Behind an interface so <see cref="SolutionLauncher"/> can be
    /// tested for what it decides to launch — argv, working directory, environment — without a real
    /// Python actually running, which is the only part of the pipeline a unit test cannot pin.
    /// </summary>
    public interface IProgramLauncher
    {
        /// <summary>Run to completion and return the program's exit code.</summary>
        int Launch(string executable, IList<string> arguments, string workingDirectory,
                   IReadOnlyDictionary<string, string> environment);
    }

    /// <summary>
    /// The real launcher: start the interpreter with the console INHERITED — no redirection.
    ///
    /// The IDE redirects both streams because it has to paint them into the Output pane, and pays
    /// for that with buffering, an extra copy of every line, and a program that cannot read stdin.
    /// A console host has none of those reasons: handing the child the terminal it was launched
    /// from is what makes <c>input()</c>, a progress bar, and coloured output behave exactly as they
    /// do under a bare <c>python</c>. That is the whole point of running without the IDE.
    /// </summary>
    public sealed class ProcessProgramLauncher : IProgramLauncher
    {
        public int Launch(string executable, IList<string> arguments, string workingDirectory,
                          IReadOnlyDictionary<string, string> environment)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                // Quote each argument per the Windows CRT rules the child's own argv parser
                // applies. A bare string.Join(" ", …) breaks any argument containing a space,
                // which on Windows means most absolute paths.
                Arguments = HelixPythonNamespace.Core.CommandLineArguments.Join(arguments),
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,     // required to pass environment variables through
                CreateNoWindow = false,
            };
            if (environment != null)
                foreach (var kv in environment) psi.EnvironmentVariables[kv.Key] = kv.Value;

            using (Process process = Process.Start(psi))
            {
                if (process == null) return -1;

                // Ctrl+C reaches the whole console process group, so the child already gets it and
                // can run its own KeyboardInterrupt handling. Staying alive through the first one
                // is what lets this process report the exit code the child chose instead of dying
                // beside it; a second Ctrl+C is left to the default handler.
                ConsoleCancelEventHandler onCancel = null;
                bool interrupted = false;
                onCancel = delegate (object s, ConsoleCancelEventArgs e)
                {
                    if (interrupted) return;      // second press: let it kill us
                    interrupted = true;
                    e.Cancel = true;
                };
                try { Console.CancelKeyPress += onCancel; } catch { onCancel = null; }
                try
                {
                    process.WaitForExit();
                    return process.ExitCode;
                }
                finally
                {
                    if (onCancel != null) try { Console.CancelKeyPress -= onCancel; } catch { }
                }
            }
        }
    }
}
