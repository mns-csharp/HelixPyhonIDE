using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>A CUDA thread coordinate as cuda-gdb reports it. All -1 when the stop was in host code.</summary>
    public sealed class CudaFocus
    {
        public int BlockX = -1, BlockY = -1, BlockZ = -1;
        public int ThreadX = -1, ThreadY = -1, ThreadZ = -1;
        public int Device = -1, Sm = -1, Warp = -1, Lane = -1;

        /// <summary>True when this stop was inside device code, i.e. a real kernel lane is in focus.</summary>
        public bool IsDeviceFocus { get { return ThreadX >= 0 && BlockX >= 0; } }

        public override string ToString()
        {
            if (!IsDeviceFocus) return "host";
            return "block (" + BlockX + "," + BlockY + "," + BlockZ + ") thread ("
                 + ThreadX + "," + ThreadY + "," + ThreadZ + ")";
        }
    }

    /// <summary>One stop captured from a real cuda-gdb session.</summary>
    public sealed class CudaGdbStop
    {
        public string Kind;                    // "breakpoint" or "step"
        public string Method;                  // frame func — the kernel's name in a device frame
        public string File;
        public int Line;
        public readonly CudaFocus Focus = new CudaFocus();
        public readonly SortedDictionary<string, string> Locals =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        public override string ToString()
        {
            var text = new StringBuilder(Kind + " " + Method + " " + File + ":" + Line + " [" + Focus + "]");
            foreach (KeyValuePair<string, string> local in Locals)
                text.Append("  ").Append(local.Key).Append('=').Append(local.Value);
            return text.ToString();
        }
    }

    /// <summary>Result of a real cuda-gdb session over a compiled CUDA binary.</summary>
    public sealed class CudaGdbResult
    {
        public bool Available;                 // false when cuda-gdb was not found, or the build failed
        public string Reason;
        public string DebuggerVersion;
        public string RawOutput;
        public readonly List<CudaGdbStop> Stops = new List<CudaGdbStop>();
        public bool Succeeded { get { return Available && Stops.Count > 0; } }
    }

    /// <summary>
    /// Hard rule 9's other half: debugging a CUDA kernel <b>without</b> the emulator, on the real GPU,
    /// by driving NVIDIA's <c>cuda-gdb</c>.
    ///
    /// <para>
    /// <b>Read this before trusting it.</b> This code has never been run against a real cuda-gdb. It was
    /// written on a Windows machine with no CUDA GPU, and cuda-gdb does not exist on Windows — NVIDIA
    /// removed it after CUDA 9 in favour of Nsight. What IS verified here is the parsing:
    /// <see cref="ParseMiOutput"/> is pure and is pinned by unit tests against transcripts written to the
    /// documented GDB/MI and cuda-gdb output formats. What is NOT verified is that a real cuda-gdb emits
    /// exactly those bytes, and that the command sequence below drives it correctly. Treat a first run on
    /// real hardware as the actual test.
    /// </para>
    ///
    /// <para>
    /// The one thing that is not in doubt: when cuda-gdb is absent this reports so and returns no stops.
    /// It never fabricates one. An untested feature that stays silent is a feature somebody can finish;
    /// an untested feature that invents plausible output is a trap.
    /// </para>
    ///
    /// <para>
    /// cuda-gdb IS gdb, so everything structural is shared with <c>CppDebugRunner</c>: the same
    /// <c>--interpreter=mi2</c> protocol, the same <c>*stopped</c> async records, the same
    /// <c>-stack-list-variables</c>. Two things are CUDA's own — the build needs <c>nvcc -g -G</c> or
    /// device breakpoints will not bind, and a stop in device code carries a <b>focus</b> (which block,
    /// which thread) that has no analogue in a host debugger.
    /// </para>
    /// </summary>
    public static class CudaGdbRunner
    {
        /// <summary>Environment variable pointing at a specific cuda-gdb build.</summary>
        public const string ExecutableVariable = "HELIX_CUDA_GDB";

        public static bool IsAvailable { get { return FindCudaGdb() != null; } }

        /// <summary>The located cuda-gdb, or null. Exposed so the UI can explain WHY it is unavailable.</summary>
        public static string ExecutablePath { get { return FindCudaGdb(); } }

        /// <summary>
        /// Why real-GPU kernel debugging is unavailable here, in words a user can act on. Null when it
        /// IS available.
        /// </summary>
        public static string UnavailableReason()
        {
            if (FindCudaGdb() != null) return null;
            string platform = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? "cuda-gdb is not available on Windows — NVIDIA removed it after CUDA 9, and Nsight "
                  + "Visual Studio Edition replaced it. Debug kernels on the Helix emulator here, or "
                  + "run on Linux for real-GPU debugging. "
                : "cuda-gdb was not found. Install the CUDA Toolkit, or ";
            return platform + "Set " + ExecutableVariable + " to its full path to point at a specific build.";
        }

        /// <summary>
        /// Build <paramref name="cuFile"/> with device debug info and drive cuda-gdb to break at
        /// <paramref name="breakLine"/>, optionally switching focus to a chosen lane, then single-step
        /// <paramref name="steps"/> times capturing locals at each stop.
        /// </summary>
        /// <param name="focusBlock">Block to switch focus to, or null to report whichever lane hit first.</param>
        /// <param name="focusThread">Thread within that block, or null.</param>
        public static CudaGdbResult Debug(string cuFile, int breakLine,
            int[] focusBlock = null, int[] focusThread = null, int steps = 2, int timeoutMs = 300000)
        {
            var result = new CudaGdbResult();
            if (string.IsNullOrEmpty(cuFile) || !File.Exists(cuFile))
            {
                result.Reason = "CUDA source not found: " + cuFile;
                return result;
            }

            string cudaGdb = FindCudaGdb();
            if (cudaGdb == null) { result.Reason = UnavailableReason(); return result; }

            string executable, buildReason;
            if (!TryBuildWithDeviceDebugInfo(cuFile, out executable, out buildReason))
            {
                result.Reason = buildReason;
                return result;
            }

            result.DebuggerVersion = QueryVersion(cudaGdb);
            string output = Drive(cudaGdb, executable, Path.GetFileName(cuFile), breakLine,
                focusBlock, focusThread, steps < 1 ? 1 : steps, timeoutMs);
            result.RawOutput = output.Length > 16000 ? output.Substring(output.Length - 16000) : output;
            ParseStops(output, result.Stops);
            result.Available = true;
            return result;
        }

        // ---- build ------------------------------------------------------------

        /// <summary>
        /// Compile with <c>nvcc -g -G</c>. Both flags matter and for different reasons: <c>-g</c> gives the
        /// HOST code debug info, <c>-G</c> gives the DEVICE code debug info. Without <c>-G</c> a breakpoint
        /// inside a kernel simply never binds, and the session looks like a debugger that ignores
        /// breakpoints rather than a build that was missing a flag.
        /// </summary>
        private static bool TryBuildWithDeviceDebugInfo(string cuFile, out string executable, out string reason)
        {
            executable = null;
            reason = null;

            string nvcc = FindNvcc();
            if (nvcc == null)
            {
                reason = "nvcc was not found on PATH or in a standard CUDA Toolkit install. "
                       + "Real-GPU kernel debugging needs the toolkit to build with -G (device debug info).";
                return false;
            }

            string absolute = Path.GetFullPath(cuFile);
            string directory = Path.Combine(Path.GetTempPath(), "helix_cudagdb_" + Guid.NewGuid().ToString("N"));
            string exeName = Path.GetFileNameWithoutExtension(absolute)
                + (Environment.OSVersion.Platform == PlatformID.Win32NT ? ".exe" : string.Empty);
            string exePath = Path.Combine(directory, exeName);
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) { reason = "could not create a build directory: " + ex.Message; return false; }

            var arguments = new StringBuilder();
            arguments.Append("-g -G -O0 ").Append(Quote(absolute)).Append(" -o ").Append(Quote(exePath));

            string output;
            int exitCode;
            if (!RunProcess(nvcc, arguments.ToString(), Path.GetDirectoryName(absolute), 300000,
                    out output, out exitCode))
            {
                reason = "nvcc timed out building " + Path.GetFileName(cuFile);
                return false;
            }
            if (exitCode != 0 || !File.Exists(exePath))
            {
                reason = "nvcc -g -G failed (exit " + exitCode + "):" + Environment.NewLine + output;
                return false;
            }
            executable = exePath;
            return true;
        }

        // ---- drive ------------------------------------------------------------

        /// <summary>
        /// The command sequence. Written to be readable rather than clever, because nobody here can run it:
        /// whoever first tries this on a GPU will be reading this method to work out what went wrong.
        /// </summary>
        private static string Drive(string cudaGdb, string executable, string sourceName, int line,
            int[] focusBlock, int[] focusThread, int steps, int timeoutMs)
        {
            var info = new ProcessStartInfo
            {
                FileName = cudaGdb,
                Arguments = "--interpreter=mi2 " + Quote(executable),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable),
            };

            var transcript = new StringBuilder();
            using (Process process = Process.Start(info))
            {
                var reader = new System.Threading.Thread(delegate ()
                {
                    try
                    {
                        int c;
                        while ((c = process.StandardOutput.Read()) >= 0)
                            lock (transcript) transcript.Append((char)c);
                    }
                    catch { }
                });
                reader.IsBackground = true;
                reader.Start();

                Action<string> send = delegate (string command)
                {
                    try { process.StandardInput.WriteLine(command); process.StandardInput.Flush(); }
                    catch { }
                };

                // MEASURED, not guessed: without this, cuda-gdb's "Are you sure you want to
                // continue? (y or [n])" prompts are auto-answered NO when stdin is a pipe, and device
                // debugging is abandoned with only a console-stream line to say so. The first real run
                // on a GPU died exactly there.
                send("-gdb-set confirm off");
                // Stop the process from being torn down between the kernel finishing and us reading it.
                send("-gdb-set cuda break_on_launch none");
                send("-break-insert " + sourceName + ":" + line);
                System.Threading.Thread.Sleep(300);

                int mark = Mark(transcript);
                send("-exec-run");
                if (WaitFor(transcript, "breakpoint-hit", 60000, mark))
                {
                    // Whichever lane arrived first is in focus. When the caller asked for a specific one,
                    // switch to it -- the same choice the emulator debugger offers, and for the same
                    // reason: the interesting thread is rarely thread 0.
                    if (IsCoordinate(focusBlock) && IsCoordinate(focusThread))
                    {
                        send("-interpreter-exec console " + Quote(
                            "cuda block (" + Join(focusBlock) + ") thread (" + Join(focusThread) + ")"));
                        System.Threading.Thread.Sleep(400);
                    }
                    // Ask where we are, so the focus is in the transcript even if the stop record did not
                    // carry a "[Switching focus ...]" line (it only appears when the focus CHANGES).
                    send("-interpreter-exec console " + Quote("cuda kernel block thread"));
                    System.Threading.Thread.Sleep(300);
                    send("-stack-list-variables 1");
                    System.Threading.Thread.Sleep(400);

                    for (int i = 0; i < steps; i++)
                    {
                        int stepMark = Mark(transcript);
                        send("-exec-next");
                        if (!WaitFor(transcript, "*stopped", 30000, stepMark)) break;
                        send("-interpreter-exec console " + Quote("cuda kernel block thread"));
                        System.Threading.Thread.Sleep(300);
                        send("-stack-list-variables 1");
                        System.Threading.Thread.Sleep(400);
                    }
                }
                send("-gdb-exit");
                if (!process.WaitForExit(timeoutMs)) { try { process.Kill(); } catch { } }
                reader.Join(500);
            }
            lock (transcript) return transcript.ToString();
        }

        private static bool IsCoordinate(int[] value)
        {
            return value != null && value.Length == 3 && value[0] >= 0 && value[1] >= 0 && value[2] >= 0;
        }

        private static string Join(int[] value)
        {
            return value[0].ToString(CultureInfo.InvariantCulture) + ","
                 + value[1].ToString(CultureInfo.InvariantCulture) + ","
                 + value[2].ToString(CultureInfo.InvariantCulture);
        }

        private static int Mark(StringBuilder text) { lock (text) return text.Length; }

        private static bool WaitFor(StringBuilder text, string marker, int timeoutMs, int startFrom)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                lock (text)
                {
                    if (text.Length > startFrom
                        && text.ToString(startFrom, text.Length - startFrom).Contains(marker))
                        return true;
                }
                System.Threading.Thread.Sleep(50);
            }
            return false;
        }

        // ---- parse ------------------------------------------------------------

        // GDB/MI async records. Mostly identical to CppDebugRunner's, because cuda-gdb IS gdb --
        // with one difference that is fatal if you assume otherwise. A CUDA stop record puts its own
        // field FIRST:
        //     *stopped,CudaFocus={...},reason="breakpoint-hit",...
        // so "reason" is not adjacent to "*stopped" the way it is in plain gdb. Anchoring the two
        // together (as the C/C++ runner reasonably does) drops every device breakpoint hit on the
        // floor, silently. Measured against real cuda-gdb 13.4; see the .mi.txt fixtures.
        private static readonly Regex StoppedLine = new Regex(
            @"\*stopped,.*?\breason=""(?<reason>[^""]+)""", RegexOptions.Compiled);
        private static readonly Regex FrameFunc = new Regex(@"func=""(?<func>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex FrameFile = new Regex(@"[,{]file=""(?<file>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex FrameLine = new Regex(@"line=""(?<line>\d+)""", RegexOptions.Compiled);
        private static readonly Regex VarsLine = new Regex(@"variables=\[(?<vars>.*?)\]", RegexOptions.Compiled);
        // {name="value",value="7"} for a local, but {name="data",arg="1",value="0x70a600000"} for a
        // PARAMETER -- cuda-gdb marks arguments with an extra field. Requiring name and value to be
        // adjacent silently drops every kernel parameter, which are the variables a user most wants
        // to see. The [^}]*? tolerates that field and any other cuda-gdb adds later.
        private static readonly Regex OneVar = new Regex(
            @"\{name=""(?<name>[^""]+)""[^}]*?,value=""(?<val>[^""]*)""[^}]*\}", RegexOptions.Compiled);

        // CUDA's own. Matched WHEREVER they appear rather than against one exact sentence: the focus
        // reaches the transcript two different ways -- the "[Switching focus to CUDA kernel ...]" banner
        // cuda-gdb prints when the focus changes, and the reply to an explicit "cuda block thread" -- and
        // pinning either phrasing exactly would make the parser brittle against a tool nobody here can run.
        private static readonly Regex BlockCoordinate = new Regex(
            @"block\s*\((?<x>\d+)\s*,\s*(?<y>\d+)\s*,\s*(?<z>\d+)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ThreadCoordinate = new Regex(
            @"thread\s*\((?<x>\d+)\s*,\s*(?<y>\d+)\s*,\s*(?<z>\d+)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HardwareCoordinate = new Regex(
            @"device\s+(?<device>\d+)\s*,\s*sm\s+(?<sm>\d+)\s*,\s*warp\s+(?<warp>\d+)\s*,\s*lane\s+(?<lane>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The focus as cuda-gdb actually reports it on a stop:
        //   CudaFocus={device="0",sm="0",warp="0",lane="0",kernel="0",grid="1",
        //              blockIdx="(0,0,0)",threadIdx="(0,0,0)"}
        // Note "blockIdx=" and "threadIdx=", NOT the "block (x,y,z)" spelling of the console banner --
        // the coordinate regexes above match the banner and do NOT match this, which is how a real
        // kernel stop ended up looking like host code.
        private static readonly Regex FocusBlockIdx = new Regex(
            @"blockIdx=""\((?<x>\d+),(?<y>\d+),(?<z>\d+)\)""", RegexOptions.Compiled);
        private static readonly Regex FocusThreadIdx = new Regex(
            @"threadIdx=""\((?<x>\d+),(?<y>\d+),(?<z>\d+)\)""", RegexOptions.Compiled);
        private static readonly Regex FocusHardware = new Regex(
            @"device=""(?<device>\d+)"",sm=""(?<sm>\d+)"",warp=""(?<warp>\d+)"",lane=""(?<lane>\d+)""",
            RegexOptions.Compiled);

        private static void ParseStops(string output, List<CudaGdbStop> stops)
        {
            if (string.IsNullOrEmpty(output)) return;

            // A focus line can arrive BEFORE its stop record (cuda-gdb announces the switch, then reports
            // the stop) or after it (when we asked explicitly). So a focus seen while no stop is open is
            // held and applied to the next one.
            var pending = new CudaFocus();
            bool pendingHasFocus = false;
            CudaGdbStop current = null;
            // The last lane we knew about, and the frame it belonged to. cuda-gdb prints the focus
            // banner only when the focus CHANGES, so stepping within one thread produces stop records
            // with no coordinate at all -- and without carrying it forward, the second line of a kernel
            // would report as host code and the Variables panel would lose threadIdx halfway through.
            var lastFocus = new CudaFocus();
            string lastMethod = null;

            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string line = Unescape(raw);

                Match stopped = StoppedLine.Match(line);
                if (stopped.Success)
                {
                    string reason = stopped.Groups["reason"].Value;
                    if (reason.StartsWith("exited", StringComparison.Ordinal)) { current = null; continue; }
                    string file = FrameFile.Match(line).Groups["file"].Value;
                    if (string.IsNullOrEmpty(file)) { current = null; continue; }

                    current = new CudaGdbStop
                    {
                        Kind = reason.StartsWith("breakpoint", StringComparison.Ordinal) ? "breakpoint" : "step",
                        Method = FrameFunc.Match(line).Groups["func"].Value,
                        File = file,
                    };
                    int number;
                    int.TryParse(FrameLine.Match(line).Groups["line"].Value, out number);
                    current.Line = number;
                    if (pendingHasFocus) { Copy(pending, current.Focus); pendingHasFocus = false; }
                    // Scan the stop record itself too. cuda-gdb announces the focus on its own console
                    // line, but a version that folded it into the record would otherwise be parsed as a
                    // host-code stop -- silently, and only on hardware nobody here can try.
                    ScanFocus(line, current.Focus);

                    // Still nothing, and we are in the same frame we were in last time? Then the focus
                    // did not change, which is exactly why no banner was printed. Same frame is the
                    // condition that matters: once the kernel returns to host code the function differs,
                    // and a carried-forward thread index next to host variables would be a lie.
                    if (!current.Focus.IsDeviceFocus && lastFocus.IsDeviceFocus
                        && string.Equals(current.Method, lastMethod, StringComparison.Ordinal))
                        Copy(lastFocus, current.Focus);

                    if (current.Focus.IsDeviceFocus) { Copy(current.Focus, lastFocus); }
                    lastMethod = current.Method;
                    stops.Add(current);
                    continue;
                }

                CudaFocus target = current != null ? current.Focus : pending;
                if (ScanFocus(line, target))
                {
                    if (current == null) pendingHasFocus = true;
                    else if (current.Focus.IsDeviceFocus) Copy(current.Focus, lastFocus);
                }

                Match variables = VarsLine.Match(line);
                if (variables.Success && current != null)
                    foreach (Match one in OneVar.Matches(variables.Groups["vars"].Value))
                        current.Locals[one.Groups["name"].Value] = one.Groups["val"].Value;
            }
        }

        /// <summary>
        /// Pull whatever coordinates <paramref name="line"/> carries into <paramref name="target"/>.
        /// Returns true when it carried any. Each part is independent, because the three appear
        /// together in a focus banner and separately in the replies to individual <c>cuda</c> commands.
        /// </summary>
        private static bool ScanFocus(string line, CudaFocus target)
        {
            bool found = false;

            // The structured CudaFocus={...} field on a stop record.
            Match blockIdx = FocusBlockIdx.Match(line);
            if (blockIdx.Success)
            {
                target.BlockX = Number(blockIdx, "x");
                target.BlockY = Number(blockIdx, "y");
                target.BlockZ = Number(blockIdx, "z");
                found = true;
            }
            Match threadIdx = FocusThreadIdx.Match(line);
            if (threadIdx.Success)
            {
                target.ThreadX = Number(threadIdx, "x");
                target.ThreadY = Number(threadIdx, "y");
                target.ThreadZ = Number(threadIdx, "z");
                found = true;
            }
            Match focusHardware = FocusHardware.Match(line);
            if (focusHardware.Success)
            {
                target.Device = Number(focusHardware, "device");
                target.Sm = Number(focusHardware, "sm");
                target.Warp = Number(focusHardware, "warp");
                target.Lane = Number(focusHardware, "lane");
                found = true;
            }

            // The console banner, printed when the focus CHANGES -- e.g. after an explicit
            // "cuda block (..) thread (..)". Both spellings occur in one session.
            Match block = BlockCoordinate.Match(line);
            if (block.Success)
            {
                target.BlockX = Number(block, "x");
                target.BlockY = Number(block, "y");
                target.BlockZ = Number(block, "z");
                found = true;
            }
            Match thread = ThreadCoordinate.Match(line);
            if (thread.Success)
            {
                target.ThreadX = Number(thread, "x");
                target.ThreadY = Number(thread, "y");
                target.ThreadZ = Number(thread, "z");
                found = true;
            }
            Match hardware = HardwareCoordinate.Match(line);
            if (hardware.Success)
            {
                target.Device = Number(hardware, "device");
                target.Sm = Number(hardware, "sm");
                target.Warp = Number(hardware, "warp");
                target.Lane = Number(hardware, "lane");
                found = true;
            }
            return found;
        }

        private static int Number(Match match, string group)
        {
            int value;
            return int.TryParse(match.Groups[group].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) ? value : -1;
        }

        private static void Copy(CudaFocus from, CudaFocus to)
        {
            to.BlockX = from.BlockX; to.BlockY = from.BlockY; to.BlockZ = from.BlockZ;
            to.ThreadX = from.ThreadX; to.ThreadY = from.ThreadY; to.ThreadZ = from.ThreadZ;
            to.Device = from.Device; to.Sm = from.Sm; to.Warp = from.Warp; to.Lane = from.Lane;
        }

        /// <summary>
        /// Undo GDB/MI's console-stream quoting. Focus banners arrive as <c>~"[Switching focus ...]\n"</c>,
        /// so the parens and commas the coordinate regexes look for are inside an escaped C string.
        /// </summary>
        private static string Unescape(string line)
        {
            if (line.IndexOf('\\') < 0) return line;
            return line.Replace("\\n", " ").Replace("\\t", " ").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        /// <summary>
        /// Parse a captured cuda-gdb <c>--interpreter=mi2</c> transcript into stops. Pure, and the only
        /// part of this class that is actually covered by tests — see the class remarks.
        /// </summary>
        public static List<CudaGdbStop> ParseMiOutput(string output)
        {
            var stops = new List<CudaGdbStop>();
            ParseStops(output, stops);
            return stops;
        }

        // ---- discovery --------------------------------------------------------

        private static string FindCudaGdb()
        {
            string overridden = Environment.GetEnvironmentVariable(ExecutableVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                try { if (File.Exists(overridden)) return overridden; } catch { }
            }

            string onPath = FindOnPath("cuda-gdb") ?? FindOnPath("cuda-gdb.exe");
            if (onPath != null) return onPath;

            foreach (string root in ToolkitRoots())
            {
                foreach (string name in new[] { "cuda-gdb", "cuda-gdb.exe" })
                {
                    string candidate;
                    try { candidate = Path.Combine(root, "bin", name); } catch { continue; }
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        private static IEnumerable<string> ToolkitRoots()
        {
            foreach (string variable in new[] { "CUDA_PATH", "CUDA_HOME" })
            {
                string value = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
            // Linux/WSL, where cuda-gdb actually exists. Newest first.
            foreach (string root in SortedVersionDirectories("/usr/local", "cuda")) yield return root;
            yield return "/usr/local/cuda";
            yield return "/opt/cuda";
            // Windows toolkit layout, kept for completeness — cuda-gdb is not shipped there.
            foreach (string root in new[] {
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA",
                @"C:\Program Files (x86)\NVIDIA GPU Computing Toolkit\CUDA" })
                foreach (string version in SortedVersionDirectories(root, null))
                    yield return version;
        }

        private static List<string> SortedVersionDirectories(string root, string prefix)
        {
            var results = new List<string>();
            try
            {
                if (!Directory.Exists(root)) return results;
                foreach (string directory in Directory.GetDirectories(root))
                {
                    if (prefix != null && !Path.GetFileName(directory)
                            .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    results.Add(directory);
                }
                results.Sort(StringComparer.OrdinalIgnoreCase);
                results.Reverse();
            }
            catch { }
            return results;
        }

        private static string FindNvcc()
        {
            string onPath = FindOnPath("nvcc.exe") ?? FindOnPath("nvcc");
            if (onPath != null) return onPath;
            foreach (string root in ToolkitRoots())
            {
                foreach (string name in new[] { "nvcc", "nvcc.exe" })
                {
                    string candidate;
                    try { candidate = Path.Combine(root, "bin", name); } catch { continue; }
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        private static string FindOnPath(string executable)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                string candidate;
                try { candidate = Path.Combine(directory.Trim(), executable); } catch { continue; }
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string QueryVersion(string tool)
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(8000);
                    Match match = Regex.Match(output, @"GDB\)?\s+([\d.]+)");
                    if (!match.Success) match = Regex.Match(output, @"([\d.]+)");
                    return match.Success ? match.Groups[match.Groups.Count - 1].Value : "unknown";
                }
            }
            catch { return "unknown"; }
        }

        private static bool RunProcess(string executable, string arguments, string workingDirectory,
            int timeoutMs, out string output, out int exitCode)
        {
            output = string.Empty;
            exitCode = -1;
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            };
            using (Process process = Process.Start(info))
            {
                string standardOutput = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs)) { try { process.Kill(); } catch { } return false; }
                exitCode = process.ExitCode;
                output = standardOutput + Environment.NewLine + standardError;
                return true;
            }
        }

        private static string Quote(string text) { return "\"" + text + "\""; }
    }
}
