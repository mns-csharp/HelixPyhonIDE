using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HelixPythonNamespace.CudaVirtualCompiler;

namespace HelixPythonNamespace.Workspace.Cpp
{
    /// <summary>One breakpoint/step stop captured from a real gdb or lldb session over a C/C++ binary.</summary>
    public sealed class CppDebugStop
    {
        public string Kind;                    // "breakpoint" or "step"
        public string Method;                  // frame func
        public string File;
        public int Line;
        public readonly SortedDictionary<string, string> Locals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public override string ToString()
        {
            var sb = new StringBuilder(Kind + " " + Method + " " + File + ":" + Line);
            foreach (var kv in Locals) sb.Append("  " + kv.Key + "=" + kv.Value);
            return sb.ToString();
        }
    }

    /// <summary>Result of a real gdb/lldb debug session over a compiled C/C++ binary.</summary>
    public sealed class CppDebugResult
    {
        public bool Available;                 // false when neither gdb nor lldb could be located, or the build failed
        public string Reason;
        public string DebuggerVersion;
        public string RawOutput;
        public readonly List<CppDebugStop> Stops = new List<CppDebugStop>();
        public bool Succeeded { get { return Available && Stops.Count > 0; } }
    }

    /// <summary>
    /// A plain (non-CUDA) C/C++ debugger — breakpoints / stepping / variable inspection over a running native
    /// binary, driven by real <b>gdb</b> in its <c>--interpreter=mi2</c> mode (GDB/MI, the same protocol family
    /// netcoredbg and lldb also speak), falling back to
    /// <c>lldb</c> (the usual compiled-language fallback) when gdb is not installed. Per NFR-02 it shells
    /// to the real tool; when neither is present it reports
    /// so honestly — it never fabricates a stop. Building: a <c>CMakeLists.txt</c> found by walking up from the
    /// source file is configured/built (GNU toolchain, so gdb gets DWARF) via
    /// <see cref="CMakeBuildRunner"/>; otherwise the single file is compiled directly with gcc/g++.
    /// </summary>
    public static class CppDebugRunner
    {
        public static bool IsAvailable { get { return FindGdb() != null || FindLldb() != null; } }

        /// <summary>
        /// Test seam: when true, gdb and lldb are reported as not found, so the honest-absence
        /// branch runs on a machine that has them. Never set by product code.
        /// </summary>
        internal static bool SimulateNoDebuggerForTests;

        /// <summary>
        /// Resolve (building if needed) the debuggable binary for <paramref name="sourceFile"/>, then drive
        /// gdb (or lldb if gdb is unavailable) to break at <paramref name="breakLine"/>, capture locals, and
        /// single-step <paramref name="steps"/> times.
        /// </summary>
        public static CppDebugResult Debug(string projectOrFileDir, string sourceFile, int breakLine, int steps = 2, int timeoutMs = 180000)
        {
            var r = new CppDebugResult();
            string gdb = FindGdb();
            string lldb = gdb == null ? FindLldb() : null;
            if (gdb == null && lldb == null)
            {
                r.Reason = "Neither gdb nor lldb was found on PATH or standard MinGW/MSYS2/LLVM installs. " +
                           "Install MinGW-w64 (provides gdb) or LLVM (provides lldb) to debug C/C++.";
                return r;
            }

            string exe, buildReason;
            if (!TryGetExecutable(sourceFile, out exe, out buildReason))
            {
                r.Reason = buildReason;
                return r;
            }

            r.DebuggerVersion = QueryVersion(gdb ?? lldb, gdb != null);
            string srcName = Path.GetFileName(sourceFile);
            int stepCount = steps < 1 ? 1 : steps;
            string output = gdb != null
                ? DriveGdbMi(gdb, exe, srcName, breakLine, stepCount, timeoutMs)
                : DriveLldbBatch(lldb, exe, srcName, breakLine, stepCount, timeoutMs);
            r.RawOutput = output.Length > 8000 ? output.Substring(output.Length - 8000) : output;
            if (gdb != null) ParseStops(output, r.Stops); else ParseLldbStops(output, r.Stops);
            r.Available = true;
            return r;
        }

        // Build (or locate a prior build of) the binary for sourceFile: a CMakeLists.txt found by walking up
        // wins (configured/built with a GNU compiler so gdb/lldb get DWARF debug info); otherwise the single
        // file is compiled directly with gcc (.c) or g++ (.cpp/.cc/.cxx).
        private static bool TryGetExecutable(string sourceFile, out string exe, out string reason)
        {
            exe = null; reason = null;
            string cmakeDir = FindCMakeListsDir(sourceFile);
            if (cmakeDir != null)
            {
                string gccCpp = CMakeBuildRunner.FindGccCppCompiler();
                string gccC = CMakeBuildRunner.FindGccCCompiler(gccCpp);
                if (gccCpp == null || gccC == null)
                {
                    reason = "CMakeLists.txt found at " + cmakeDir + " but no GNU gcc/g++ compiler was found " +
                             "(install MinGW-w64/WinLibs or set HELIX_GXX) — a GCC toolchain build is required " +
                             "for gdb/lldb debug info.";
                    return false;
                }
                string buildDir = Path.Combine(cmakeDir, "cmake-build-helix-debug");
                var extra = new List<string>
                {
                    "-DCMAKE_BUILD_TYPE=Debug",
                    "-DCMAKE_C_COMPILER=\"" + gccC.Replace('\\', '/') + "\"",
                    "-DCMAKE_CXX_COMPILER=\"" + gccCpp.Replace('\\', '/') + "\"",
                };
                if (!File.Exists(Path.Combine(buildDir, "CMakeCache.txt")))
                {
                    CMakeRunResult cfg = CMakeBuildRunner.Configure(
                        cmakeDir, buildDir, "Ninja", extra, 120000, CMakeHostToolchain.Gcc);
                    if (!cfg.Available) { reason = "cmake unavailable: " + cfg.Reason; return false; }
                    if (cfg.ExitCode != 0) { reason = "cmake configure failed (exit " + cfg.ExitCode + "): " + cfg.RawOutput; return false; }
                }
                CMakeRunResult bld = CMakeBuildRunner.Build(buildDir, null, 180000, CMakeHostToolchain.Gcc);
                if (!bld.Available) { reason = "cmake unavailable: " + bld.Reason; return false; }
                if (bld.ExitCode != 0) { reason = "cmake build failed (exit " + bld.ExitCode + "): " + bld.RawOutput; return false; }
                string found = FindBuiltExe(buildDir);
                if (found == null) { reason = "built executable not found under " + buildDir; return false; }
                exe = found;
                return true;
            }

            // No CMakeLists.txt — compile the single file directly.
            string ext = (Path.GetExtension(sourceFile) ?? string.Empty).ToLowerInvariant();
            bool isC = ext == ".c";
            string compiler = isC
                ? CMakeBuildRunner.FindGccCCompiler(CMakeBuildRunner.FindGccCppCompiler())
                : CMakeBuildRunner.FindGccCppCompiler();
            if (compiler == null)
            {
                reason = (isC ? "gcc.exe" : "g++.exe") + " was not found on PATH or standard MinGW/WinLibs installs.";
                return false;
            }
            string tempDir = Path.Combine(Path.GetTempPath(), "helix_cpp_dbg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string exeName = Path.GetFileNameWithoutExtension(sourceFile) + ".exe";
            string exePath = Path.Combine(tempDir, exeName);
            string srcDir = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
            string srcBase = Path.GetFileName(sourceFile);
            string compileOut; int compileExit;
            if (!RunProcess(compiler, "-g -O0 -o " + Quote(exePath) + " " + Quote(srcBase), srcDir, 60000, out compileOut, out compileExit))
            { reason = (isC ? "gcc" : "g++") + " timed out compiling " + sourceFile; return false; }
            if (compileExit != 0 || !File.Exists(exePath))
            { reason = (isC ? "gcc" : "g++") + " compile failed (exit " + compileExit + "): " + compileOut; return false; }
            exe = exePath;
            return true;
        }

        // Walk up from the source file to the nearest directory containing a CMakeLists.txt.
        private static string FindCMakeListsDir(string sourceFile)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFile)));
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "CMakeLists.txt"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        // The newest non-CMakeFiles .exe under a build directory.
        private static string FindBuiltExe(string buildDir)
        {
            if (!Directory.Exists(buildDir)) return null;
            string best = null; DateTime newest = DateTime.MinValue;
            try
            {
                foreach (string f in Directory.GetFiles(buildDir, "*.exe", SearchOption.AllDirectories))
                {
                    if (f.IndexOf("CMakeFiles", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > newest) { newest = t; best = f; }
                }
            }
            catch { }
            return best;
        }

        // gdb --interpreter=mi2: unlike netcoredbg attaching to a dotnet host, gdb has the binary's symbols
        // available the moment it's launched, so the breakpoint binds BEFORE the first run — no entry-point
        // dance needed (contrast CSharpDebugRunner.DriveMi).
        private static string DriveGdbMi(string gdb, string exe, string srcName, int line, int steps, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = gdb, Arguments = "--interpreter=mi2 " + Quote(exe),
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe),
            };
            var sb = new StringBuilder();
            using (var p = Process.Start(psi))
            {
                var reader = new System.Threading.Thread(() => { try { int c; while ((c = p.StandardOutput.Read()) >= 0) lock (sb) sb.Append((char)c); } catch { } });
                reader.IsBackground = true; reader.Start();

                void Send(string cmd) { try { p.StandardInput.WriteLine(cmd); p.StandardInput.Flush(); } catch { } }

                Send("-break-insert " + srcName + ":" + line);
                System.Threading.Thread.Sleep(300);
                int mark = Marker(sb);
                Send("-exec-run");
                if (WaitFor(sb, "breakpoint-hit", 20000, mark))
                {
                    Send("-stack-list-variables 1");
                    System.Threading.Thread.Sleep(400);
                    for (int i = 0; i < steps; i++)
                    {
                        int m2 = Marker(sb);
                        Send("-exec-next");
                        if (!WaitFor(sb, "*stopped", 10000, m2)) break;
                        Send("-stack-list-variables 1");
                        System.Threading.Thread.Sleep(400);
                    }
                }
                Send("-gdb-exit");
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                reader.Join(500);
            }
            lock (sb) return sb.ToString();
        }

        private static int Marker(StringBuilder sb) { lock (sb) return sb.Length; }

        private static bool WaitFor(StringBuilder sb, string marker, int timeoutMs, int startFrom = 0)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (sb) { if (sb.ToString(startFrom, sb.Length - startFrom).Contains(marker)) return true; }
                System.Threading.Thread.Sleep(50);
            }
            return false;
        }

        // *stopped,reason="breakpoint-hit",...,frame={...,func="compute",...,file="t.cpp",line="5"},...
        // GDB/MI is the same async-record family netcoredbg speaks, so the field regexes match
        // CSharpDebugRunner's byte-for-byte.
        private static readonly Regex StoppedLine = new Regex(
            @"\*stopped,reason=""(?<reason>[^""]+)""", RegexOptions.Compiled);
        private static readonly Regex FrameFunc = new Regex(@"func=""(?<func>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex FrameFile = new Regex(@"[,{]file=""(?<file>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex FrameLine = new Regex(@"line=""(?<line>\d+)""", RegexOptions.Compiled);
        // ^done,variables=[{name="total",value="0"},{name="i",value="1"}]
        private static readonly Regex VarsLine = new Regex(@"variables=\[(?<vars>.*?)\]", RegexOptions.Compiled);
        private static readonly Regex OneVar = new Regex(@"\{name=""(?<name>[^""]+)"",value=""(?<val>[^""]*)""\}", RegexOptions.Compiled);

        private static void ParseStops(string output, List<CppDebugStop> stops)
        {
            if (string.IsNullOrEmpty(output)) return;
            string[] lines = output.Replace("\r", "").Split('\n');
            CppDebugStop current = null;
            foreach (string line in lines)
            {
                Match sm = StoppedLine.Match(line);
                if (sm.Success)
                {
                    string reason = sm.Groups["reason"].Value;
                    // Skip the process-exit stop — not a user breakpoint/step.
                    if (reason == "exited" || reason.StartsWith("exited", StringComparison.Ordinal))
                    { current = null; continue; }
                    string file = FrameFile.Match(line).Groups["file"].Value;
                    if (string.IsNullOrEmpty(file)) { current = null; continue; } // no source frame -> ignore
                    current = new CppDebugStop
                    {
                        Kind = reason.StartsWith("breakpoint", StringComparison.Ordinal) ? "breakpoint" : "step",
                        Method = FrameFunc.Match(line).Groups["func"].Value,
                        File = file,
                    };
                    int ln; int.TryParse(FrameLine.Match(line).Groups["line"].Value, out ln); current.Line = ln;
                    stops.Add(current);
                    continue;
                }
                Match vm = VarsLine.Match(line);
                if (vm.Success && current != null)
                    foreach (Match one in OneVar.Matches(vm.Groups["vars"].Value))
                        current.Locals[one.Groups["name"].Value] = one.Groups["val"].Value;
            }
        }

        /// <summary>Parse a captured gdb --interpreter=mi2 transcript into stops. Exposed for deterministic testing.</summary>
        public static List<CppDebugStop> ParseMiOutput(string output)
        {
            var stops = new List<CppDebugStop>();
            ParseStops(output, stops);
            return stops;
        }

        // lldb fallback: the same "--batch" pattern used for MSVC/PDB binaries — the
        // -o commands run synchronously, so a breakpoint pauses the batch and the following "frame variable"
        // executes at the stop. No timing race.
        private static string DriveLldbBatch(string lldb, string exe, string srcName, int line, int steps, int timeoutMs)
        {
            var args = new StringBuilder("--batch");
            args.Append(" -o ").Append(Quote("breakpoint set --file " + srcName + " --line " + line));
            args.Append(" -o ").Append(Quote("run"));
            args.Append(" -o ").Append(Quote("frame variable"));
            for (int i = 0; i < steps; i++)
            {
                args.Append(" -o ").Append(Quote("thread step-over"));
                args.Append(" -o ").Append(Quote("frame variable"));
            }
            args.Append(" -o ").Append(Quote("continue"));
            args.Append(' ').Append(Quote(exe));

            string outp; int exitCode;
            RunProcess(lldb, args.ToString(), Path.GetDirectoryName(exe), timeoutMs, out outp, out exitCode);
            return outp;
        }

        // "frame #0: 0x... t.exe`compute(n=5) at t.cpp:2"
        private static readonly Regex LldbFrameLine = new Regex(
            @"frame #\d+:.*?`(?<method>[\w:]+)\([^)]*\)\s+at\s+(?<file>[^:\s]+):(?<line>\d+)", RegexOptions.Compiled);
        // "stop reason = breakpoint 1.1" / "stop reason = step over"
        private static readonly Regex LldbStopReason = new Regex(@"stop reason = (?<reason>[a-z ]+)", RegexOptions.Compiled);
        // "(int) total = 0"   ("(int) i = <variable not available>" is filtered)
        private static readonly Regex LldbVarLine = new Regex(
            @"^\s*\((?<type>[^)]+)\)\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<val>.+?)\s*$", RegexOptions.Compiled);

        private static void ParseLldbStops(string output, List<CppDebugStop> stops)
        {
            if (string.IsNullOrEmpty(output)) return;
            string[] lines = output.Replace("\r", "").Split('\n');
            string pendingReason = "breakpoint";
            CppDebugStop current = null;
            foreach (string raw in lines)
            {
                Match rm = LldbStopReason.Match(raw);
                if (rm.Success) { pendingReason = rm.Groups["reason"].Value.Trim(); continue; }

                Match fm = LldbFrameLine.Match(raw);
                if (fm.Success)
                {
                    current = new CppDebugStop
                    {
                        Kind = pendingReason.StartsWith("breakpoint", StringComparison.Ordinal) ? "breakpoint" : "step",
                        Method = fm.Groups["method"].Value.Trim(),
                        File = fm.Groups["file"].Value.Trim(),
                    };
                    int ln; int.TryParse(fm.Groups["line"].Value, out ln); current.Line = ln;
                    stops.Add(current);
                    continue;
                }
                if (current == null) continue;
                Match vm = LldbVarLine.Match(raw);
                if (vm.Success)
                {
                    string val = vm.Groups["val"].Value.Trim();
                    if (val.StartsWith("<", StringComparison.Ordinal)) continue; // "<variable not available>"
                    current.Locals[vm.Groups["name"].Value] = val;
                }
            }
        }

        /// <summary>Parse a captured lldb --batch transcript into stops. Exposed for deterministic testing.</summary>
        public static List<CppDebugStop> ParseLldbOutput(string output)
        {
            var stops = new List<CppDebugStop>();
            ParseLldbStops(output, stops);
            return stops;
        }

        private static bool RunProcess(string exe, string args, string workingDir, int timeoutMs, out string output, out int exitCode)
        {
            output = string.Empty; exitCode = -1;
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = args, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Path.GetTempPath(),
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }
                exitCode = p.ExitCode;
                output = o + "\n" + e;
                return true;
            }
        }

        private static string QueryVersion(string tool, bool isGdb)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = tool, Arguments = "--version", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(8000);
                    Match m = Regex.Match(o, isGdb ? @"GDB\)?\s+([\d.]+)" : @"lldb version ([\d.]+)");
                    if (!m.Success) m = Regex.Match(o, @"([\d.]+)");
                    return m.Success ? m.Groups[m.Groups.Count - 1].Value : "unknown";
                }
            }
            catch { return "unknown"; }
        }

        // Probes PATH plus the same GNU-toolchain install roots CMakeBuildRunner.FindGccCppCompiler already
        // searches (so "gdb next to g++" resolves consistently with whatever compiler HelixStudio builds C/C++
        // projects with), plus the standard MSYS2/MinGW roots.
        private static string FindGdb()
        {
            if (SimulateNoDebuggerForTests) return null;
            string onPath = FindOnPath("gdb.exe") ?? FindOnPath("gdb");
            if (onPath != null) return onPath;
            string env = Environment.GetEnvironmentVariable("HELIX_GDB");
            if (!string.IsNullOrWhiteSpace(env)) { try { if (File.Exists(env)) return env; } catch { } }

            var roots = new List<string>();
            string gxx = CMakeBuildRunner.FindGccCppCompiler();
            if (!string.IsNullOrWhiteSpace(gxx))
            {
                try { roots.Add(Path.GetDirectoryName(gxx)); } catch { }
            }
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            roots.Add(@"C:\mingw64\bin");
            roots.Add(@"C:\WinLibs\mingw64\bin");
            roots.Add(Path.Combine(localApp, "Programs", "WinLibs", "mingw64", "bin"));
            roots.Add(@"C:\msys64\mingw64\bin");
            roots.Add(@"C:\msys64\usr\bin");
            roots.Add(@"C:\MinGW\bin");

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string cand = Path.Combine(root, "gdb.exe");
                if (File.Exists(cand)) return cand;
            }
            return null;
        }

        // Same lldb probe RustDebugRunner uses (PATH + standard LLVM installs).
        private static string FindLldb()
        {
            if (SimulateNoDebuggerForTests) return null;
            string onPath = FindOnPath("lldb.exe") ?? FindOnPath("lldb");
            if (onPath != null) return onPath;
            foreach (string root in new[] { @"C:\Program Files\LLVM\bin", @"C:\Program Files (x86)\LLVM\bin" })
            {
                string cand = Path.Combine(root, "lldb.exe");
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

        private static string Quote(string s) { return "\"" + s + "\""; }
    }
}
