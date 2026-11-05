using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>A discovered Python virtual environment: its directory + resolved interpreter path + version.</summary>
    public sealed class VenvInfo
    {
        public string Directory;       // the venv root (contains pyvenv.cfg)
        public string InterpreterPath; // Scripts/python.exe (Windows) or bin/python (POSIX)
        public string Version;         // "3.11.0" (from `python --version`), or "" if not queried
        public bool Exists { get { return InterpreterPath != null && File.Exists(InterpreterPath); } }
        public override string ToString() { return Directory + (string.IsNullOrEmpty(Version) ? "" : " (Python " + Version + ")"); }
    }

    /// <summary>The result of a real <c>python -m venv</c> creation.</summary>
    public sealed class VenvCreateResult
    {
        public bool Success;
        public string Reason;
        public VenvInfo Venv;
        public int ExitCode;
        public string RawOutput;
    }

    /// <summary>
    /// FR-PROJ-03: create / select / bind a Python virtual environment to a project. Where the interpreter
    /// dialog only <i>discovers</i> interpreters, this actually runs the REAL <c>python -m venv</c> (NFR-02),
    /// resolves the venv's interpreter path, discovers existing venvs under a project, and persists the
    /// project→interpreter binding to a sidecar (<c>.helixstudio/interpreter</c>) that survives reopen.
    /// </summary>
    public static class VenvService
    {
        /// <summary>The interpreter path inside a venv directory (platform-correct), whether or not it exists yet.</summary>
        public static string InterpreterPathIn(string venvDir)
        {
            if (string.IsNullOrEmpty(venvDir)) return null;
            string win = Path.Combine(venvDir, "Scripts", "python.exe");
            string posix = Path.Combine(venvDir, "bin", "python");
            if (File.Exists(win)) return win;
            if (File.Exists(posix)) return posix;
            // Not created yet: prefer the platform-native guess.
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? win : posix;
        }

        /// <summary>True when <paramref name="dir"/> is a venv (has a <c>pyvenv.cfg</c>).</summary>
        public static bool IsVenv(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "pyvenv.cfg"));
        }

        /// <summary>Create a venv: <c>&lt;basePython&gt; -m venv &lt;venvDir&gt;</c>. Uses the REAL interpreter.</summary>
        public static VenvCreateResult Create(string basePython, string venvDir, bool withPip = true, int timeoutMs = 120000)
        {
            var r = new VenvCreateResult();
            if (string.IsNullOrEmpty(basePython)) basePython = FindPython();
            if (basePython == null) { r.Reason = "no base python interpreter found"; return r; }
            if (string.IsNullOrEmpty(venvDir)) { r.Reason = "no target directory"; return r; }

            string args = "-m venv " + (withPip ? "" : "--without-pip ") + Quote(Path.GetFullPath(venvDir));
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = basePython, Arguments = args,
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } r.Reason = "venv creation timed out"; return r; }
                    r.ExitCode = p.ExitCode;
                    r.RawOutput = (outp + "\n" + err).Trim();
                    if (p.ExitCode != 0) { r.Reason = "python -m venv exited " + p.ExitCode + ": " + r.RawOutput; return r; }
                }
            }
            catch (Exception ex) { r.Reason = "venv creation failed: " + ex.Message; return r; }

            var info = new VenvInfo { Directory = Path.GetFullPath(venvDir), InterpreterPath = InterpreterPathIn(venvDir) };
            info.Version = QueryVersion(info.InterpreterPath);
            r.Venv = info;
            r.Success = info.Exists;
            if (!r.Success) r.Reason = "venv created but interpreter not found at " + info.InterpreterPath;
            return r;
        }

        /// <summary>Discover venvs under a project directory (common names + any dir with a pyvenv.cfg, depth ≤ 2).</summary>
        public static List<VenvInfo> Discover(string projectDir, bool queryVersions = false)
        {
            var found = new List<VenvInfo>();
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return found;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string cfg in SafeEnumerate(projectDir, "pyvenv.cfg", 2))
            {
                string dir = Path.GetDirectoryName(cfg);
                if (dir == null || !seen.Add(dir)) continue;
                var info = new VenvInfo { Directory = dir, InterpreterPath = InterpreterPathIn(dir) };
                if (queryVersions && info.Exists) info.Version = QueryVersion(info.InterpreterPath);
                found.Add(info);
            }
            return found;
        }

        /// <summary>Bind an interpreter/venv to a project: persist to <c>&lt;projectDir&gt;/.helixstudio/interpreter</c>.</summary>
        public static bool Bind(string projectDir, string interpreterOrVenvPath)
        {
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return false;
            if (string.IsNullOrEmpty(interpreterOrVenvPath)) return false;
            string interp = Directory.Exists(interpreterOrVenvPath) ? InterpreterPathIn(interpreterOrVenvPath) : interpreterOrVenvPath;
            string cfgDir = Path.Combine(projectDir, ".helixstudio");
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(Path.Combine(cfgDir, "interpreter"), Path.GetFullPath(interp));
            return true;
        }

        /// <summary>Read the interpreter previously bound to a project (null if none).</summary>
        public static string BoundInterpreter(string projectDir)
        {
            if (string.IsNullOrEmpty(projectDir)) return null;
            string f = Path.Combine(projectDir, ".helixstudio", "interpreter");
            if (!File.Exists(f)) return null;
            string p = File.ReadAllText(f).Trim();
            return p.Length == 0 ? null : p;
        }

        private static IEnumerable<string> SafeEnumerate(string root, string pattern, int maxDepth)
        {
            var results = new List<string>();
            void Walk(string dir, int depth)
            {
                if (depth > maxDepth) return;
                try { foreach (string f in Directory.GetFiles(dir, pattern)) results.Add(f); } catch { }
                try { foreach (string sub in Directory.GetDirectories(dir)) Walk(sub, depth + 1); } catch { }
            }
            Walk(root, 0);
            return results;
        }

        private static string FindPython()
        {
            string onPath = FindOnPath("python.exe") ?? FindOnPath("python") ?? FindOnPath("python3");
            return onPath;
        }

        private static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string cand;
                try { cand = Path.Combine(dir.Trim(), exe); } catch { continue; }
                if (File.Exists(cand)) return cand;
            }
            return null;
        }

        private static string QueryVersion(string python)
        {
            if (python == null || !File.Exists(python)) return "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string outp = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
                    p.WaitForExit(10000);
                    var m = System.Text.RegularExpressions.Regex.Match(outp, @"Python ([\d.]+)");
                    return m.Success ? m.Groups[1].Value : "";
                }
            }
            catch { return ""; }
        }

        private static string Quote(string s) { return "\"" + s + "\""; }
    }
}
