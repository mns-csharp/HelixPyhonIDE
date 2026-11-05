using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HelixPythonNamespace.Workspace.Profiling
{
    /// <summary>
    /// A user-mode statistical sampling profiler for any native Windows executable — no admin and no
    /// ETW required. It launches the exe, then repeatedly suspends each of the child process's
    /// threads, reads the instruction pointer (<c>GetThreadContext</c>) and resolves it to a function
    /// through DbgHelp (<c>SymFromAddr</c>), aggregating leaf-function hit counts into the shared
    /// <see cref="WorkspaceProfile"/>.
    ///
    /// Language-agnostic on purpose: it needs a built executable and a PDB beside it, nothing else.
    /// <c>RustProfiler</c> (cargo build) and <c>CppProfiler</c> (cmake build) both drive it, so the
    /// P/Invoke machinery lives here once instead of being copied per language.
    /// </summary>
    public static class NativeSampler
    {
        /// <summary>Rank raw per-function hit counts into a profile. Pure — unit-tested.</summary>
        public static WorkspaceProfile BuildProfile(IDictionary<string, int> counts, double wallSeconds)
        {
            var r = new WorkspaceProfile { Tool = "sampler", Available = true, WallSeconds = wallSeconds };
            int total = 0;
            foreach (var kv in counts) total += kv.Value;
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in list)
                r.Samples.Add(new ProfileSample
                {
                    Function = kv.Key,
                    Samples = kv.Value,
                    Percent = total > 0 ? 100.0 * kv.Value / total : 0.0,
                });
            return r;
        }

        /// <summary>
        /// Launch <paramref name="exe"/> and sample it for <paramref name="sampleMs"/>, filling
        /// <paramref name="r"/>. <paramref name="isRuntimeFrame"/> decides which resolved frames are
        /// runtime scaffolding rather than the user's code; pass null for the shared default.
        /// </summary>
        public static void Sample(string exe, int sampleMs, WorkspaceProfile r,
                                  Func<string, bool> isRuntimeFrame = null,
                                  string arguments = null)
        {
            if (isRuntimeFrame == null) isRuntimeFrame = IsCommonRuntimeFrame;
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = arguments ?? string.Empty,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            var counts = new Dictionary<string, int>();
            var sw = Stopwatch.StartNew();
            using (var p = Process.Start(psi))
            {
                int pid = p.Id;
                IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
                if (hProc == IntPtr.Zero) hProc = p.Handle;

                SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_LOAD_LINES);
                System.Threading.Thread.Sleep(120);           // let the loader map modules before we invade
                bool symOk = SymInitialize(hProc, Path.GetDirectoryName(exe), true);

                // 16-byte-aligned CONTEXT buffer (GetThreadContext requires alignment on x64).
                IntPtr raw = Marshal.AllocHGlobal(CONTEXT_SIZE + 16);
                long aligned = (raw.ToInt64() + 15) & ~15L;
                IntPtr ctx = new IntPtr(aligned);
                // Cache open thread handles so the hot sampling loop avoids a Toolhelp snapshot per
                // sample (that snapshot dominated the cost and throttled the rate to ~50 Hz).
                var handles = new Dictionary<int, IntPtr>();
                long lastRefresh = -100000;
                timeBeginPeriod(1);   // so Sleep(1) really is ~1ms (else ~15ms → few samples)
                try
                {
                    while (sw.ElapsedMilliseconds < sampleMs && !p.HasExited)
                    {
                        if (sw.ElapsedMilliseconds - lastRefresh > 400)
                        {
                            lastRefresh = sw.ElapsedMilliseconds;
                            var live = new HashSet<int>(EnumThreads(pid));
                            foreach (int tid in live)
                                if (!handles.ContainsKey(tid))
                                {
                                    IntPtr h = OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME, false, (uint)tid);
                                    if (h != IntPtr.Zero) handles[tid] = h;
                                }
                            var dead = new List<int>();
                            foreach (var kv in handles) if (!live.Contains(kv.Key)) dead.Add(kv.Key);
                            foreach (int tid in dead) { CloseHandle(handles[tid]); handles.Remove(tid); }
                        }
                        foreach (var kv in handles)
                        {
                            IntPtr hThread = kv.Value;
                            if (SuspendThread(hThread) == unchecked((uint)-1)) continue;
                            ZeroMemory(ctx, CONTEXT_SIZE);
                            Marshal.WriteInt32(ctx, CTX_FLAGS_OFFSET, CONTEXT_CONTROL);
                            ulong rip = 0;
                            if (GetThreadContext(hThread, ctx)) rip = (ulong)Marshal.ReadInt64(ctx, CTX_RIP_OFFSET);
                            ResumeThread(hThread);
                            if (rip != 0)
                            {
                                string name = symOk ? Resolve(hProc, rip) : null;
                                if (name == null) name = "[external]";
                                int c; counts[name] = counts.TryGetValue(name, out c) ? c + 1 : 1;
                            }
                        }
                        System.Threading.Thread.Sleep(1);      // ~1 kHz sampling
                    }
                }
                finally
                {
                    timeEndPeriod(1);
                    foreach (var kv in handles) CloseHandle(kv.Value);
                    Marshal.FreeHGlobal(raw);
                    if (symOk) SymCleanup(hProc);
                    try { if (!p.HasExited) p.Kill(); } catch { }
                }
            }
            sw.Stop();

            // Drop runtime scaffolding; keep the user's functions as the hot set. If everything was
            // scaffolding, report what we have rather than an empty profile.
            var user = new Dictionary<string, int>();
            foreach (var kv in counts) if (!isRuntimeFrame(kv.Key)) user[kv.Key] = kv.Value;
            var chosen = user.Count > 0 ? user : counts;

            var built = BuildProfile(chosen, sw.Elapsed.TotalSeconds);
            r.Samples.AddRange(built.Samples);
            r.WallSeconds = sw.Elapsed.TotalSeconds;
            r.Available = true;
            if (r.Samples.Count == 0) r.Reason = "no samples captured (workload too short?)";
        }

        /// <summary>CRT/OS scaffolding common to every native language.</summary>
        public static bool IsCommonRuntimeFrame(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name == "[external]") return true;
            string[] pre = { "__", "RtlUser", "BaseThreadInit", "mainCRTStartup", "invoke_main",
                             "__scrt", "NtWait", "Sleep", "WaitFor" };
            foreach (string p in pre) if (name.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string Resolve(IntPtr hProc, ulong addr)
        {
            // SYMBOL_INFO followed by a name buffer. MaxNameLen chars inline after the fixed header.
            const int maxName = 256;
            IntPtr buf = Marshal.AllocHGlobal(SYMBOL_INFO_SIZE + maxName);
            try
            {
                ZeroMemory(buf, SYMBOL_INFO_SIZE + maxName);
                Marshal.WriteInt32(buf, 0, SYMBOL_INFO_SIZE);          // SizeOfStruct
                Marshal.WriteInt32(buf, SI_MAXNAME_OFFSET, maxName);   // MaxNameLen
                ulong disp;
                if (SymFromAddr(hProc, addr, out disp, buf))
                {
                    IntPtr namePtr = new IntPtr(buf.ToInt64() + SI_NAME_OFFSET);
                    string name = Marshal.PtrToStringAnsi(namePtr);
                    return string.IsNullOrEmpty(name) ? null : name;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static IEnumerable<int> EnumThreads(int pid)
        {
            var ids = new List<int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snap == INVALID_HANDLE) return ids;
            try
            {
                var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32)) };
                if (Thread32First(snap, ref te))
                    do { if (te.th32OwnerProcessID == (uint)pid) ids.Add((int)te.th32ThreadID); }
                    while (Thread32Next(snap, ref te));
            }
            finally { CloseHandle(snap); }
            return ids;
        }

        /// <summary>Newest *.exe in a directory — the just-built binary.</summary>
        public static string NewestExecutable(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string best = null; DateTime bestT = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(dir, "*.exe"))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t > bestT) { bestT = t; best = f; }
            }
            return best;
        }

        // ---- P/Invoke ----

        private const int CONTEXT_SIZE = 1232;          // sizeof(CONTEXT) on x64
        private const int CTX_FLAGS_OFFSET = 0x30;      // CONTEXT.ContextFlags
        private const int CTX_RIP_OFFSET = 0xF8;        // CONTEXT.Rip
        private const int CONTEXT_CONTROL = 0x00100001; // AMD64 CONTEXT_CONTROL
        private const int SYMBOL_INFO_SIZE = 88;        // sizeof(SYMBOL_INFO) incl. Name[1] + tail padding (x64)
        private const int SI_MAXNAME_OFFSET = 80;       // SYMBOL_INFO.MaxNameLen (ULONG at offset 80)
        private const int SI_NAME_OFFSET = 84;          // SYMBOL_INFO.Name[] (CHAR[1] at offset 84)

        private const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010;
        private const uint THREAD_GET_CONTEXT = 0x0008, THREAD_SUSPEND_RESUME = 0x0002;
        private const uint TH32CS_SNAPTHREAD = 0x00000004;
        private const int SYMOPT_UNDNAME = 0x2, SYMOPT_DEFERRED_LOADS = 0x4, SYMOPT_LOAD_LINES = 0x10;
        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct THREADENTRY32
        {
            public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID;
            public int tpBasePri, tpDeltaPri; public uint dwFlags;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr hThread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr hThread, IntPtr ctx);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 te);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 te);
        [DllImport("kernel32.dll")] private static extern void RtlZeroMemory(IntPtr dst, IntPtr len);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
        private static void ZeroMemory(IntPtr dst, int len) { RtlZeroMemory(dst, new IntPtr(len)); }

        [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool SymInitialize(IntPtr hProc, string searchPath, bool invade);
        [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool SymCleanup(IntPtr hProc);
        [DllImport("dbghelp.dll")] private static extern int SymSetOptions(int options);
        [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool SymFromAddr(IntPtr hProc, ulong addr, out ulong disp, IntPtr symbol);
    }
}
