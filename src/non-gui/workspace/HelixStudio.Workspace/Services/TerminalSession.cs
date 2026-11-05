using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// FR-X-01 (engine, NFR-03): a headless interactive-terminal session. Hosts a real shell
    /// process (cmd.exe by default) with redirected stdin/stdout/stderr, streams combined output
    /// through <see cref="OutputReceived"/>, and accepts commands via <see cref="SendInput"/>.
    /// The WinForms <c>TerminalPanel</c> is a thin view over this; the CLI/tests drive it directly,
    /// so the interactive terminal is CI-testable without a UI (mirrors the debugger/runner pattern).
    /// </summary>
    public sealed class TerminalSession : IDisposable
    {
        private readonly string shellPath_;
        private readonly string shellArgs_;
        private readonly string workingDir_;
        private Process proc_;
        private readonly object gate_ = new object();

        /// <summary>Raised (off the reader thread) for each chunk of combined stdout/stderr.</summary>
        public event Action<string> OutputReceived;

        /// <summary>Raised once when the hosted shell exits, with its exit code.</summary>
        public event Action<int> Exited;

        public TerminalSession(string shellPath = null, string workingDir = null, string shellArgs = null)
        {
            // Default to the platform command interpreter. ComSpec is cmd.exe on Windows.
            shellPath_ = string.IsNullOrEmpty(shellPath)
                ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                : shellPath;
            // For cmd.exe, "/Q" disables command echo (the panel echoes commands itself); "/K" keeps
            // the shell alive for subsequent commands. "prompt $P$G" is cmd's own default format
            // (current drive+path, then ">") — spelled out explicitly because /K's "prompt $G$S"
            // history in this codebase collapsed it to a bare "> " with no path, which read as a
            // missing/broken prompt. Callers may override for other shells.
            shellArgs_ = shellArgs ?? (IsCmd(shellPath_) ? "/Q /K prompt $P$G" : null);
            workingDir_ = string.IsNullOrEmpty(workingDir) ? Environment.CurrentDirectory : workingDir;
        }

        public bool IsRunning
        {
            get { lock (gate_) { return proc_ != null && !SafeHasExited(proc_); } }
        }

        public string ShellPath { get { return shellPath_; } }
        public string WorkingDirectory { get { return workingDir_; } }

        /// <summary>Spawn the shell and begin streaming its output. Idempotent while running.</summary>
        public void Start()
        {
            lock (gate_)
            {
                if (proc_ != null && !SafeHasExited(proc_)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = shellPath_,
                    Arguments = shellArgs_ ?? string.Empty,
                    WorkingDirectory = workingDir_,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                proc_ = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc_.Exited += (s, e) =>
                {
                    Action<int> h = Exited;
                    if (h != null)
                    {
                        int code = 0;
                        try { code = proc_.ExitCode; } catch { }
                        h(code);
                    }
                };

                proc_.Start();
                // Deliberately NOT BeginOutputReadLine()/OutputDataReceived: that mechanism only
                // delivers complete, newline-terminated lines. An interactive shell's PROMPT is
                // printed WITHOUT a trailing newline by design (so the user's typed input lands on
                // the same line) — under line-buffered reading it sits invisibly buffered until some
                // LATER output happens to complete a line, so the prompt never (promptly) appears.
                // Read raw chunks instead, delivering whatever bytes are available immediately.
                StartRawReaderThread(proc_.StandardOutput);
                StartRawReaderThread(proc_.StandardError);
            }
        }

        // Blocks on Read() (returns as soon as ANY data is available, unlike ReadLine()) and emits
        // each chunk as it arrives. One background thread per stream; both exit naturally when the
        // underlying pipe closes (Read returns 0) as the process exits.
        private void StartRawReaderThread(StreamReader reader)
        {
            var thread = new System.Threading.Thread(() =>
            {
                char[] buffer = new char[4096];
                try
                {
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        Emit(new string(buffer, 0, read));
                }
                catch { /* pipe torn down on process exit */ }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>Write a command line to the shell's stdin (a trailing newline is added).</summary>
        public void SendInput(string command)
        {
            Process p;
            lock (gate_) { p = proc_; }
            if (p == null || SafeHasExited(p)) return;
            try { p.StandardInput.WriteLine(command ?? string.Empty); p.StandardInput.Flush(); }
            catch { /* pipe closed — shell exited between the guard and the write */ }
        }

        /// <summary>Politely ask the shell to exit (writes <c>exit</c>); falls back to Kill on timeout.</summary>
        public void Close(int waitMs = 1500)
        {
            Process p;
            lock (gate_) { p = proc_; }
            if (p == null) return;
            try
            {
                if (!SafeHasExited(p)) { p.StandardInput.WriteLine("exit"); p.StandardInput.Flush(); }
                if (!p.WaitForExit(waitMs)) Kill();
            }
            catch { Kill(); }
        }

        public void Kill()
        {
            Process p;
            lock (gate_) { p = proc_; }
            if (p == null) return;
            try { if (!SafeHasExited(p)) p.Kill(); } catch { }
        }

        /// <summary>
        /// Kill the shell AND everything it spawned (a long-running command typed into it, e.g. a
        /// Python script). .NET Framework 4.7.2 lacks <c>Process.Kill(entireProcessTree)</c> — a
        /// plain <see cref="Kill"/> only kills the shell itself and orphans its children, which is
        /// exactly wrong for "stop the long-running thing I just started in here". Uses <c>taskkill
        /// /T /F</c> (same pattern as the Run/Debug/Profiler stop paths elsewhere in this app),
        /// falling back to a plain Kill if taskkill itself is unavailable/blocked.
        /// </summary>
        public void KillTree()
        {
            Process p;
            lock (gate_) { p = proc_; }
            if (p == null) return;
            int pid;
            try { pid = p.Id; } catch { return; }

            bool treeKilled = false;
            try
            {
                var psi = new ProcessStartInfo("taskkill", "/T /F /PID " + pid)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (Process tk = Process.Start(psi))
                {
                    if (tk != null)
                    {
                        tk.WaitForExit(3000);
                        treeKilled = tk.HasExited && tk.ExitCode == 0;
                    }
                }
            }
            catch { /* taskkill missing or blocked — fall back below */ }

            if (!treeKilled)
            {
                try { if (!SafeHasExited(p)) p.Kill(); } catch { }
            }
        }

        public void Dispose()
        {
            try { Kill(); } catch { }
            Process p;
            lock (gate_) { p = proc_; proc_ = null; }
            if (p != null) { try { p.Dispose(); } catch { } }
        }

        private void Emit(string text)
        {
            Action<string> h = OutputReceived;
            if (h != null) h(text);
        }

        private static bool IsCmd(string path)
        {
            string name = string.Empty;
            try { name = Path.GetFileNameWithoutExtension(path) ?? string.Empty; } catch { }
            return name.Equals("cmd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }
    }
}
