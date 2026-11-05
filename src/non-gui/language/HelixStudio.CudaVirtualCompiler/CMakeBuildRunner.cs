using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.CudaVirtualCompiler
{
    public enum CMakeHostToolchain
    {
        Auto,
        Msvc,
        Gcc,
        Emulator
    }

    public sealed class NativeCompilerProbeResult
    {
        public bool Available;
        public bool IsGnu;
        public bool Is64Bit;
        public string CompilerPath;
        public string Version;
        public string Target;
        public string Reason;
        public string RawVersion;

        public bool Usable { get { return Available && IsGnu && Is64Bit; } }
    }

    /// <summary>One compiler diagnostic parsed from a real cmake/ninja/make build (gcc, MSVC, or nvcc styles).</summary>
    public sealed class CMakeDiagnostic
    {
        public string FilePath;
        public int Line;
        public int Column;
        public bool IsError;
        public string Message;

        public override string ToString()
        {
            return FilePath + "(" + Line + "): " + (IsError ? "error" : "warning") + ": " + Message;
        }
    }

    /// <summary>Result of a real cmake configure or build invocation.</summary>
    public sealed class CMakeRunResult
    {
        public bool Available;                 // false when cmake couldn't be located or invoked
        public string Reason;                  // why unavailable / how it failed
        public string Phase;                   // "configure" or "build"
        public int ExitCode;
        public string CMakeVersion;            // e.g. "4.2.3"
        public string RawOutput;               // combined stdout+stderr (trimmed)
        public readonly List<CMakeDiagnostic> Diagnostics = new List<CMakeDiagnostic>();

        /// <summary>True when the tool ran and reported success (exit 0).</summary>
        public bool Succeeded { get { return Available && ExitCode == 0; } }

        public int ErrorCount
        {
            get { int n = 0; foreach (var d in Diagnostics) if (d.IsError) n++; return n; }
        }
    }

    /// <summary>
    /// Drives the REAL <c>cmake</c> toolchain to configure and build a native project (FR-PM-04) — the
    /// build-system analogue of <see cref="NvccVerifier"/> (which shells to real <c>nvcc</c> for a single TU).
    /// Where <see cref="HelixPythonNamespace.Workspace.Cuda.CMakeListsReader"/> is a fast static model of a CMakeLists,
    /// this brings GROUND TRUTH: it runs <c>cmake -S -B -G</c> then <c>cmake --build</c>, captures the exit
    /// code and compiler diagnostics, and reports them faithfully so the IDE's build result matches what a
    /// developer sees on the command line 100%.
    /// </summary>
    public static class CMakeBuildRunner
    {
        /// <summary>True when a cmake executable can be located (PATH or a standard install).</summary>
        public static bool IsAvailable { get { return FindCMake() != null; } }

        /// <summary>
        /// Resolve a real GNU C++ compiler. A valid user preference wins, followed by the
        /// HELIX_GXX override, PATH, and common WinLibs/MinGW installation roots.
        /// </summary>
        public static string FindGccCppCompiler(string preferredPath = null)
        {
            foreach (string candidate in new[]
            {
                preferredPath,
                Environment.GetEnvironmentVariable("HELIX_GXX"),
                FindOnPath("g++.exe"),
                FindOnPath("g++"),
                @"C:\mingw64\bin\g++.exe",
                @"C:\WinLibs\mingw64\bin\g++.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "WinLibs", "mingw64", "bin", "g++.exe")
            })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string full = Path.GetFullPath(candidate.Trim().Trim('"'));
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Resolve gcc.exe next to a selected g++.exe.</summary>
        public static string FindGccCCompiler(string cppCompilerPath)
        {
            string cpp = FindGccCppCompiler(cppCompilerPath);
            if (cpp == null) return null;
            string gcc = Path.Combine(Path.GetDirectoryName(cpp), "gcc.exe");
            return File.Exists(gcc) ? gcc : null;
        }

        /// <summary>Resolve the Visual Studio x64 developer environment script used by MSVC/nvcc.</summary>
        public static string FindMsvcEnvironmentScript(string preferredPath = null)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath))
            {
                try
                {
                    string full = Path.GetFullPath(preferredPath.Trim().Trim('"'));
                    if (File.Exists(full) &&
                        string.Equals(Path.GetFileName(full), "vcvars64.bat",
                            StringComparison.OrdinalIgnoreCase))
                        return full;
                }
                catch { }
                return null;
            }
            return FindVcVars64();
        }

        /// <summary>Execute g++ to verify identity, version, and target architecture.</summary>
        public static NativeCompilerProbeResult ProbeGccCppCompiler(string preferredPath = null,
            int timeoutMs = 10000)
        {
            var result = new NativeCompilerProbeResult();
            string compiler = FindGccCppCompiler(preferredPath);
            result.CompilerPath = compiler;
            if (compiler == null)
            {
                result.Reason = "g++.exe was not found";
                return result;
            }
            try
            {
                string versionOutput;
                int versionExit;
                if (!RunProbe(compiler, "--version", timeoutMs, out versionOutput, out versionExit))
                {
                    result.Reason = "g++ --version timed out";
                    return result;
                }
                if (versionExit != 0)
                {
                    result.Reason = "g++ --version exited " + versionExit;
                    result.RawVersion = versionOutput;
                    return result;
                }

                string targetOutput;
                int targetExit;
                if (!RunProbe(compiler, "-dumpmachine", timeoutMs, out targetOutput, out targetExit))
                {
                    result.Reason = "g++ -dumpmachine timed out";
                    return result;
                }
                result.Available = targetExit == 0;
                result.RawVersion = versionOutput;
                result.Target = FirstNonEmptyLine(targetOutput);
                string first = FirstNonEmptyLine(versionOutput);
                Match version = Regex.Match(first ?? string.Empty, @"(?<v>\d+\.\d+(?:\.\d+)?)\s*$");
                result.Version = version.Success ? version.Groups["v"].Value : "unknown";
                string identity = (first ?? string.Empty).ToLowerInvariant();
                result.IsGnu = identity.Contains("g++") || identity.Contains("gcc") ||
                    identity.Contains("gnu") || identity.Contains("mingw");
                string target = (result.Target ?? string.Empty).ToLowerInvariant();
                result.Is64Bit = target.Contains("x86_64") || target.Contains("amd64") ||
                    target.Contains("aarch64");
                if (!result.Available) result.Reason = "g++ -dumpmachine exited " + targetExit;
                else if (!result.IsGnu) result.Reason = "selected executable did not identify as GNU C++";
                else if (!result.Is64Bit) result.Reason = "32-bit compiler target is not supported: " + result.Target;
                return result;
            }
            catch (Exception ex)
            {
                result.Reason = "compiler probe failed: " + ex.Message;
                return result;
            }
        }

        /// <summary>Configure a project: <c>cmake -S sourceDir -B buildDir -G generator [extraArgs]</c>.</summary>
        public static CMakeRunResult Configure(string sourceDir, string buildDir, string generator = "Ninja",
            IEnumerable<string> extraArgs = null, int timeoutMs = 120000)
        {
            return Configure(sourceDir, buildDir, generator, extraArgs, timeoutMs, CMakeHostToolchain.Auto);
        }

        public static string NormalizeCxxStandard(string value, string fallback = "20")
        {
            string candidate = (value ?? string.Empty).Trim();
            switch (candidate)
            {
                case "98": case "03": case "11": case "14":
                case "17": case "20": case "23": case "26":
                    return candidate;
                default:
                    return fallback;
            }
        }

        public static List<string> CreateLanguageStandardArguments(
            string cxxStandard, string cudaStandard, bool usesCuda)
        {
            string cxx = NormalizeCxxStandard(cxxStandard, "20");
            var args = new List<string>
            {
                "-DCMAKE_CXX_STANDARD=" + cxx,
                "-DCMAKE_CXX_STANDARD_REQUIRED=ON",
                "-DCMAKE_CXX_EXTENSIONS=OFF"
            };
            if (usesCuda)
            {
                string cuda = NormalizeCxxStandard(cudaStandard, "20");
                args.Add("-DCMAKE_CUDA_STANDARD=" + cuda);
                args.Add("-DCMAKE_CUDA_STANDARD_REQUIRED=ON");
                args.Add("-DCMAKE_CUDA_EXTENSIONS=OFF");
            }
            return args;
        }

        public static CMakeRunResult Configure(string sourceDir, string buildDir, string generator,
            IEnumerable<string> extraArgs, int timeoutMs, CMakeHostToolchain toolchain)
        {
            return Configure(sourceDir, buildDir, generator, extraArgs, timeoutMs, toolchain, null);
        }

        public static CMakeRunResult Configure(string sourceDir, string buildDir, string generator,
            IEnumerable<string> extraArgs, int timeoutMs, CMakeHostToolchain toolchain,
            string msvcEnvironmentScript, Action<string> onOutputLine = null)
        {
            var result = new CMakeRunResult { Phase = "configure" };
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            { result.Reason = "source dir not found: " + sourceDir; return result; }
            if (!File.Exists(Path.Combine(sourceDir, "CMakeLists.txt")))
            { result.Reason = "no CMakeLists.txt in " + sourceDir; return result; }

            string cmake = FindCMake();
            if (cmake == null) { result.Reason = "cmake not found on PATH or standard install"; return result; }
            result.CMakeVersion = QueryVersion(cmake);

            var args = new StringBuilder();
            args.Append("-S ").Append(Quote(Path.GetFullPath(sourceDir)));
            args.Append(" -B ").Append(Quote(Path.GetFullPath(buildDir)));
            if (!string.IsNullOrEmpty(generator)) args.Append(" -G ").Append(Quote(generator));
            if (extraArgs != null) foreach (string a in extraArgs) args.Append(' ').Append(a);

            Run(cmake, args.ToString(), null, timeoutMs, result,
                toolchain != CMakeHostToolchain.Gcc, null, msvcEnvironmentScript, onOutputLine);
            return result;
        }

        /// <summary>Build a configured project: <c>cmake --build buildDir [--target target]</c>.</summary>
        public static CMakeRunResult Build(string buildDir, string target = null, int timeoutMs = 180000)
        {
            return Build(buildDir, target, timeoutMs, CMakeHostToolchain.Auto);
        }

        public static CMakeRunResult Build(string buildDir, string target, int timeoutMs,
            CMakeHostToolchain toolchain)
        {
            return Build(buildDir, target, timeoutMs, toolchain, null);
        }

        public static CMakeRunResult Build(string buildDir, string target, int timeoutMs,
            CMakeHostToolchain toolchain, string msvcEnvironmentScript,
            Action<string> onOutputLine = null)
        {
            var result = new CMakeRunResult { Phase = "build" };
            if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir))
            { result.Reason = "build dir not found: " + buildDir; return result; }

            string cmake = FindCMake();
            if (cmake == null) { result.Reason = "cmake not found on PATH or standard install"; return result; }
            result.CMakeVersion = QueryVersion(cmake);

            var args = new StringBuilder();
            args.Append("--build ").Append(Quote(Path.GetFullPath(buildDir)));
            if (!string.IsNullOrEmpty(target)) args.Append(" --target ").Append(Quote(target));

            Run(cmake, args.ToString(), null, timeoutMs, result,
                toolchain != CMakeHostToolchain.Gcc, null, msvcEnvironmentScript, onOutputLine);
            return result;
        }

        /// <summary>Run a native executable produced by CMake and capture its real stdout/stderr.</summary>
        public static CMakeRunResult RunExecutable(string executablePath, string workingDir = null,
            string arguments = null, int timeoutMs = 120000)
        {
            return RunExecutable(executablePath, workingDir, arguments, timeoutMs, null);
        }

        public static CMakeRunResult RunExecutable(string executablePath, string workingDir,
            string arguments, int timeoutMs, string prependPath,
            Action<string> onOutputLine = null)
        {
            var result = new CMakeRunResult { Phase = "run" };
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                result.Reason = "executable not found: " + executablePath;
                return result;
            }

            Run(Path.GetFullPath(executablePath), arguments ?? string.Empty,
                string.IsNullOrWhiteSpace(workingDir)
                    ? Path.GetDirectoryName(Path.GetFullPath(executablePath))
                    : workingDir,
                timeoutMs, result, false, prependPath, null, onOutputLine);
            return result;
        }

        // ---- Cancellation ---------------------------------------------------------------------
        // The IDE runs one native build at a time, so a single slot is enough to know what to kill.
        // Tree-kill, not Kill(): when MSVC has to be initialised the child is
        // `cmd /c call vcvars && cmake …`, so killing the direct child alone would orphan
        // cmake/ninja/nvcc and leave the build grinding on invisibly.
        private static readonly object CurrentProcessLock = new object();
        private static Process _currentProcess;

        /// <summary>True while this class has a child process running.</summary>
        public static bool IsRunning
        {
            get { lock (CurrentProcessLock) return _currentProcess != null; }
        }

        /// <summary>
        /// Tree-kill the cmake/ninja process this class is currently running. Returns false when
        /// nothing was running. Used by the IDE's Cancel/Stop command.
        /// </summary>
        public static bool CancelCurrent()
        {
            int pid;
            lock (CurrentProcessLock)
            {
                if (_currentProcess == null) return false;
                try { pid = _currentProcess.Id; } catch { return false; }
            }
            try
            {
                var psi = new ProcessStartInfo("taskkill", "/T /F /PID " + pid)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var k = Process.Start(psi)) { k.WaitForExit(10000); }
                return true;
            }
            catch { return false; }
        }

        private static void Run(string exe, string args, string workingDir, int timeoutMs,
            CMakeRunResult result, bool initializeMsvc, string prependPath = null,
            string msvcEnvironmentScript = null, Action<string> onOutputLine = null)
        {
            try
            {
                string launchExe = exe;
                string launchArgs = args;
                string vcvars = initializeMsvc && IsCMakeExecutable(exe)
                    ? FindMsvcEnvironmentScript(msvcEnvironmentScript) : null;
                if (!string.IsNullOrEmpty(vcvars))
                {
                    // A normal desktop process does not inherit Visual Studio's developer prompt.
                    // Initialize MSVC INCLUDE/LIB/PATH in the child shell so nvcc can invoke cl.exe.
                    launchExe = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                    launchArgs = "/d /c call \"" + vcvars + "\" >nul && \""
                        + exe + "\" " + args;
                }
                else if (!string.IsNullOrWhiteSpace(prependPath))
                {
                    launchExe = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                    launchArgs = "/d /c set \"PATH=" + prependPath + ";%PATH%\" && \""
                        + exe + "\" " + args;
                }
                var psi = new ProcessStartInfo
                {
                    FileName = launchExe,
                    Arguments = launchArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir ?? Path.GetTempPath(),
                };
                using (var p = Process.Start(psi))
                {
                    lock (CurrentProcessLock) _currentProcess = p;
                    try
                    {
                    // Event-based reading: streams each line LIVE to onOutputLine (the Output tab
                    // used to fill only after the whole configure/build finished) while still
                    // buffering stdout/stderr separately for RawOutput/diagnostics parsing.
                    var stdoutBuf = new StringBuilder();
                    var stderrBuf = new StringBuilder();
                    p.OutputDataReceived += (s, a) =>
                    {
                        if (a.Data == null) return;
                        lock (stdoutBuf) stdoutBuf.AppendLine(a.Data);
                        if (onOutputLine != null) { try { onOutputLine(a.Data); } catch { } }
                    };
                    p.ErrorDataReceived += (s, a) =>
                    {
                        if (a.Data == null) return;
                        lock (stderrBuf) stderrBuf.AppendLine(a.Data);
                        if (onOutputLine != null) { try { onOutputLine(a.Data); } catch { } }
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        result.Reason = "cmake timed out after " + timeoutMs + "ms";
                        return;
                    }
                    p.WaitForExit();   // second wait flushes the async output handlers
                    string stdout = stdoutBuf.ToString();
                    string stderr = stderrBuf.ToString();
                    result.ExitCode = p.ExitCode;
                    string combined = (stdout + "\n" + stderr).Trim();
                    result.RawOutput = combined.Length > 8000 ? combined.Substring(combined.Length - 8000) : combined;
                    ParseDiagnostics(combined, result.Diagnostics);
                    result.Available = true;
                    }
                    finally
                    {
                        lock (CurrentProcessLock)
                        {
                            if (ReferenceEquals(_currentProcess, p)) _currentProcess = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Reason = "cmake invocation failed: " + ex.Message;
                result.Available = false;
            }
        }

        private static bool IsCMakeExecutable(string path)
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                && string.Equals(Path.GetFileName(path), "cmake.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindVcVars64()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string vsRoot = Path.Combine(programFiles, "Microsoft Visual Studio");
            if (!Directory.Exists(vsRoot)) return null;
            try
            {
                string[] editions = { "Community", "Professional", "Enterprise", "BuildTools" };
                foreach (string versionDir in Directory.GetDirectories(vsRoot))
                    foreach (string edition in editions)
                    {
                        string candidate = Path.Combine(versionDir, edition,
                            "VC", "Auxiliary", "Build", "vcvars64.bat");
                        if (File.Exists(candidate)) return candidate;
                    }
            }
            catch { }
            return null;
        }

        // gcc/clang: "path:line:col: error: msg"   MSVC/nvcc: "path(line): error C####: msg" / "path(line): error: msg"
        private static readonly Regex GccLine = new Regex(
            @"^(?<file>(?:[A-Za-z]:)?[^:]+):(?<line>\d+):(?<col>\d+):\s*(?<sev>error|warning|fatal error):\s*(?<msg>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MsvcLine = new Regex(
            @"^(?<file>.*?)\((?<line>\d+)\)\s*:\s*(?<sev>error|warning|fatal error)\b[^:]*:\s*(?<msg>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void ParseDiagnostics(string text, List<CMakeDiagnostic> outList)
        {
            if (string.IsNullOrEmpty(text)) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                Match m = GccLine.Match(line);
                bool gcc = m.Success;
                if (!gcc) m = MsvcLine.Match(line);
                if (!m.Success) continue;
                string sev = m.Groups["sev"].Value.ToLowerInvariant();
                int ln; int.TryParse(m.Groups["line"].Value, out ln);
                int col = 0; if (gcc) int.TryParse(m.Groups["col"].Value, out col);
                var d = new CMakeDiagnostic
                {
                    FilePath = m.Groups["file"].Value.Trim(),
                    Line = ln,
                    Column = col,
                    IsError = sev.Contains("error"),
                    Message = m.Groups["msg"].Value.Trim(),
                };
                string key = d.FilePath + "|" + d.Line + "|" + d.Message;
                if (seen.Add(key)) outList.Add(d);
            }
        }

        /// <summary>Probes PATH and known install dirs for a real cmake.exe. Also used by callers
        /// (e.g. MainForm's Auto-toolchain resolution) to decide whether "Auto" should fall back to
        /// the always-available Emulator instead of silently trying an absent real toolchain.</summary>
        public static string FindCMake()
        {
            string onPath = FindOnPath("cmake.exe") ?? FindOnPath("cmake");
            if (onPath != null) return onPath;
            foreach (string root in new[] {
                @"C:\Program Files\CMake\bin",
                @"C:\Program Files (x86)\CMake\bin" })
            {
                string cand = Path.Combine(root, "cmake.exe");
                if (File.Exists(cand)) return cand;
            }
            return null;
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

        private static string QueryVersion(string cmake)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmake, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    Match m = Regex.Match(outp, @"cmake version ([\d.]+)");
                    return m.Success ? m.Groups[1].Value : "unknown";
                }
            }
            catch { return "unknown"; }
        }

        private static bool RunProbe(string executable, string arguments, int timeoutMs,
            out string output, out int exitCode)
        {
            output = string.Empty;
            exitCode = -1;
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(psi))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }
                exitCode = process.ExitCode;
                output = (stdout + "\n" + stderr).Trim();
                return true;
            }
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return null;
        }

        private static string Quote(string s) { return "\"" + s + "\""; }
    }
}
