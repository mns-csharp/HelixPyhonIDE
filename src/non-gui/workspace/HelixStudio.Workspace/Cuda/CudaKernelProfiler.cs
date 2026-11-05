using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>Static resource usage + theoretical occupancy for one <c>__global__</c> kernel.</summary>
    public sealed class CudaKernelProfile
    {
        public string Name = "";            // demangled display name (e.g. "vecAdd")
        public string MangledName = "";     // raw ptxas entry name
        public string Arch = "";            // "sm_89"
        public int Registers;               // registers per thread
        public int SharedMemBytes;          // static shared memory per block
        public int StackFrameBytes;
        public int SpillStores;             // bytes of spill stores (register pressure signal)
        public int SpillLoads;
        public int ConstMemBytes;           // cmem[0]

        // Occupancy (computed natively against the arch's hardware limits for the chosen block size).
        public int ThreadsPerBlock;         // block size the occupancy is computed for
        public int ActiveBlocksPerSm;
        public int ActiveWarpsPerSm;
        public int MaxWarpsPerSm;
        public double TheoreticalOccupancy; // 0..1  (ActiveWarpsPerSm / MaxWarpsPerSm)
        public string Limiter = "";         // "registers" | "shared-memory" | "block-limit" | "warps"

        public int OccupancyPercent { get { return (int)Math.Round(TheoreticalOccupancy * 100.0); } }
        public override string ToString()
        {
            return Name + "  regs=" + Registers + " smem=" + SharedMemBytes + "B occ=" + OccupancyPercent + "% (" + Limiter + ")";
        }
    }

    /// <summary>Result of an nvcc <c>-Xptxas -v</c> profiling pass over a <c>.cu</c> translation unit.</summary>
    public sealed class CudaProfileResult
    {
        public bool Available;
        public string Reason = "";
        public string NvccVersion = "";
        public string Arch = "";
        public string RawOutput = "";
        public readonly List<CudaKernelProfile> Kernels = new List<CudaKernelProfile>();
        public bool Succeeded { get { return Available && Kernels.Count > 0; } }
    }

    /// <summary>
    /// FR-PROF-02: real CUDA kernel profiler. Compiles a <c>.cu</c> with the REAL <c>nvcc -Xptxas -v</c>
    /// (NFR-02 — nvcc as the authoritative oracle) to obtain per-kernel <b>register</b>, <b>shared-memory</b>,
    /// <b>spill</b> and <b>constant-memory</b> usage, then computes <b>theoretical occupancy</b> natively from
    /// those figures against the target architecture's hardware limits — the CUDA Occupancy Calculator math,
    /// no external tool required. Fills the "occupancy/registers/throughput" gap the wall-clock
    /// <c>CudaBenchPanel</c> left open. Both <see cref="ParsePtxas"/> and <see cref="ComputeOccupancy"/> are
    /// pure/static for deterministic unit testing (NFR-03).
    /// </summary>
    public static class CudaKernelProfiler
    {
        public static bool IsAvailable { get { return FindNvcc() != null; } }

        public static CudaProfileResult Run(string cuFile, int threadsPerBlock = 256, int timeoutMs = 120000)
        {
            var r = new CudaProfileResult();
            if (string.IsNullOrEmpty(cuFile) || !File.Exists(cuFile)) { r.Reason = "cu file not found: " + cuFile; return r; }
            string nvcc = FindNvcc();
            if (nvcc == null) { r.Reason = "nvcc not found on PATH or standard CUDA install"; return r; }
            r.NvccVersion = QueryVersion(nvcc);

            string absCu = Path.GetFullPath(cuFile);
            string ccbin = FindHostCompilerDir();
            string tmpDir = Path.Combine(Path.GetTempPath(), "helix_cuprof_" + Guid.NewGuid().ToString("N"));
            string outObj = Path.Combine(tmpDir, "out.obj");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var args = new StringBuilder();
                args.Append("-Xptxas -v -c ").Append(Quote(absCu)).Append(" -o ").Append(Quote(outObj));
                if (ccbin != null) args.Append(" -ccbin ").Append(Quote(ccbin));

                var psi = new ProcessStartInfo
                {
                    FileName = nvcc, Arguments = args.ToString(),
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true, WorkingDirectory = tmpDir,
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();   // ptxas info goes to stderr
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } r.Reason = "nvcc timed out after " + timeoutMs + "ms"; return r; }

                    string combined = stderr + "\n" + stdout;
                    r.RawOutput = combined.Length > 8000 ? combined.Substring(combined.Length - 8000) : combined;
                    var parsed = ParsePtxas(combined, threadsPerBlock);
                    r.Kernels.AddRange(parsed.Kernels);
                    r.Arch = parsed.Arch;
                    r.Available = true;
                    if (r.Kernels.Count == 0)
                        r.Reason = p.ExitCode != 0
                            ? "nvcc did not report kernel resources (compile failed): " + FirstError(combined)
                            : "no __global__ kernels found in the translation unit";
                }
            }
            catch (Exception ex) { r.Reason = "nvcc profiling failed: " + ex.Message; r.Available = false; }
            finally { try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { } }
            return r;
        }

        // "ptxas info    : Compiling entry function '_Z6vecAddPKfS0_Pfi' for 'sm_89'"
        private static readonly Regex Entry = new Regex(
            @"Compiling entry function '(?<name>[^']+)' for '(?<arch>sm_\d+)'", RegexOptions.Compiled);
        // "    16 bytes stack frame, 0 bytes spill stores, 0 bytes spill loads"
        private static readonly Regex Props = new Regex(
            @"(?<stack>\d+)\s+bytes stack frame,\s*(?<sstore>\d+)\s+bytes spill stores,\s*(?<sload>\d+)\s+bytes spill loads",
            RegexOptions.Compiled);
        // "ptxas info    : Used 32 registers, 4096 bytes smem, 372 bytes cmem[0]" — but CUDA 13's ptxas inserts
        // extra comma-separated tokens ("used 1 barriers"), and smem may be absent, so scan each field
        // independently rather than with one ordered regex.
        private static readonly Regex UsedRegs = new Regex(@"Used\s+(?<regs>\d+)\s+registers", RegexOptions.Compiled);
        private static readonly Regex SmemRe = new Regex(@"(?<smem>\d+)\s+bytes\s+smem", RegexOptions.Compiled);
        private static readonly Regex CmemRe = new Regex(@"(?<cmem>\d+)\s+bytes\s+cmem", RegexOptions.Compiled);

        /// <summary>
        /// Parse <c>nvcc -Xptxas -v</c> output into per-kernel resource usage + computed occupancy.
        /// Pure/deterministic — exposed for unit testing against verbatim ptxas output.
        /// </summary>
        public static CudaProfileResult ParsePtxas(string text, int threadsPerBlock = 256)
        {
            var r = new CudaProfileResult { Available = true };
            if (string.IsNullOrEmpty(text)) return r;
            CudaKernelProfile cur = null;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                Match em = Entry.Match(raw);
                if (em.Success)
                {
                    cur = new CudaKernelProfile
                    {
                        MangledName = em.Groups["name"].Value,
                        Name = Demangle(em.Groups["name"].Value),
                        Arch = em.Groups["arch"].Value,
                        ThreadsPerBlock = threadsPerBlock,
                    };
                    r.Kernels.Add(cur);
                    if (string.IsNullOrEmpty(r.Arch)) r.Arch = cur.Arch;
                    continue;
                }
                if (cur == null) continue;
                Match pm = Props.Match(raw);
                if (pm.Success)
                {
                    cur.StackFrameBytes = ToInt(pm.Groups["stack"].Value);
                    cur.SpillStores = ToInt(pm.Groups["sstore"].Value);
                    cur.SpillLoads = ToInt(pm.Groups["sload"].Value);
                    continue;
                }
                Match um = UsedRegs.Match(raw);
                if (um.Success)
                {
                    cur.Registers = ToInt(um.Groups["regs"].Value);
                    Match sm = SmemRe.Match(raw); if (sm.Success) cur.SharedMemBytes = ToInt(sm.Groups["smem"].Value);
                    Match cm = CmemRe.Match(raw); if (cm.Success) cur.ConstMemBytes = ToInt(cm.Groups["cmem"].Value);
                    ComputeOccupancy(cur);   // finalize this kernel
                    cur = null;
                }
            }
            return r;
        }

        /// <summary>
        /// Native CUDA occupancy calculator: fills the occupancy fields of <paramref name="k"/> from its
        /// registers/shared-memory usage and <see cref="CudaKernelProfile.ThreadsPerBlock"/>, against the
        /// hardware limits of <see cref="CudaKernelProfile.Arch"/>. Pure — exposed for testing.
        /// </summary>
        public static void ComputeOccupancy(CudaKernelProfile k)
        {
            SmLimits lim = LimitsFor(k.Arch);
            int block = k.ThreadsPerBlock > 0 ? k.ThreadsPerBlock : 256;
            int warpsPerBlock = CeilDiv(block, 32);
            if (warpsPerBlock < 1) warpsPerBlock = 1;

            // Register-limited active blocks.
            int blocksReg;
            if (k.Registers <= 0) blocksReg = lim.MaxBlocksPerSm;
            else
            {
                int regsPerWarp = RoundUp(k.Registers * 32, lim.RegAllocUnit);
                int warpsByReg = regsPerWarp > 0 ? lim.RegsPerSm / regsPerWarp : lim.MaxWarpsPerSm;
                blocksReg = warpsByReg / warpsPerBlock;
            }
            // Shared-memory-limited active blocks.
            int blocksSmem;
            if (k.SharedMemBytes <= 0) blocksSmem = lim.MaxBlocksPerSm;
            else
            {
                int smemAligned = RoundUp(k.SharedMemBytes, lim.SmemAllocUnit);
                blocksSmem = smemAligned > 0 ? lim.SmemPerSm / smemAligned : lim.MaxBlocksPerSm;
            }
            // Warp-count-limited and hardware block-limited.
            int blocksWarp = lim.MaxWarpsPerSm / warpsPerBlock;
            int blocksBlk = lim.MaxBlocksPerSm;

            int active = Math.Min(Math.Min(blocksReg, blocksSmem), Math.Min(blocksWarp, blocksBlk));
            if (active < 0) active = 0;

            // Attribute the binding constraint (registers/shared-memory reported before the softer limits).
            string limiter = "warps";
            if (active == blocksBlk) limiter = "block-limit";
            if (active == blocksWarp) limiter = "warps";
            if (active == blocksSmem) limiter = "shared-memory";
            if (active == blocksReg) limiter = "registers";

            k.MaxWarpsPerSm = lim.MaxWarpsPerSm;
            k.ActiveBlocksPerSm = active;
            k.ActiveWarpsPerSm = active * warpsPerBlock;
            if (k.ActiveWarpsPerSm > lim.MaxWarpsPerSm) k.ActiveWarpsPerSm = lim.MaxWarpsPerSm;
            k.TheoreticalOccupancy = lim.MaxWarpsPerSm > 0 ? (double)k.ActiveWarpsPerSm / lim.MaxWarpsPerSm : 0.0;
            if (k.TheoreticalOccupancy > 1.0) k.TheoreticalOccupancy = 1.0;
            k.Limiter = limiter;
        }

        /// <summary>Per-compute-capability hardware limits used by the occupancy calculator.</summary>
        public struct SmLimits
        {
            public int MaxWarpsPerSm;
            public int MaxBlocksPerSm;
            public int RegsPerSm;        // 32-bit registers per SM
            public int SmemPerSm;        // bytes of shared memory per SM available to blocks
            public int RegAllocUnit;     // register allocation unit (per warp)
            public int SmemAllocUnit;    // shared-memory allocation unit (bytes)
        }

        /// <summary>Limits keyed by <c>sm_XX</c>; falls back to a modern Ampere-class default when unknown.</summary>
        public static SmLimits LimitsFor(string arch)
        {
            switch (arch)
            {
                case "sm_60": return Mk(64, 32, 65536, 65536, 256, 256);
                case "sm_61":
                case "sm_62": return Mk(64, 32, 65536, 98304, 256, 256);
                case "sm_70":
                case "sm_72": return Mk(64, 32, 65536, 98304, 256, 256);
                case "sm_75": return Mk(32, 16, 65536, 65536, 256, 256);
                case "sm_80": return Mk(64, 32, 65536, 167936, 256, 128);
                case "sm_86":
                case "sm_87": return Mk(48, 16, 65536, 102400, 256, 128);
                case "sm_89": return Mk(48, 24, 65536, 102400, 256, 128);
                case "sm_90": return Mk(64, 32, 65536, 233472, 256, 128);
                default:      return Mk(48, 16, 65536, 102400, 256, 128); // Ampere-class default
            }
        }

        private static SmLimits Mk(int warps, int blocks, int regs, int smem, int regUnit, int smemUnit)
        {
            return new SmLimits { MaxWarpsPerSm = warps, MaxBlocksPerSm = blocks, RegsPerSm = regs, SmemPerSm = smem, RegAllocUnit = regUnit, SmemAllocUnit = smemUnit };
        }

        /// <summary>Light Itanium-mangling demangle: <c>_Z6vecAdd...</c> → <c>vecAdd</c>. Best-effort display only.</summary>
        public static string Demangle(string mangled)
        {
            if (string.IsNullOrEmpty(mangled)) return mangled;
            if (!mangled.StartsWith("_Z")) return mangled;
            int i = 2;
            while (i < mangled.Length && mangled[i] == 'N') i++;   // nested-name intro
            int len = 0, start = i;
            while (i < mangled.Length && char.IsDigit(mangled[i])) { len = len * 10 + (mangled[i] - '0'); i++; }
            if (len > 0 && i + len <= mangled.Length) return mangled.Substring(i, len);
            return mangled;
        }

        private static int ToInt(string s) { int v; return int.TryParse(s, out v) ? v : 0; }
        private static int CeilDiv(int a, int b) { return b <= 0 ? 0 : (a + b - 1) / b; }
        private static int RoundUp(int a, int unit) { return unit <= 0 ? a : ((a + unit - 1) / unit) * unit; }

        private static string FirstError(string text)
        {
            foreach (string raw in text.Replace("\r", "").Split('\n'))
                if (raw.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return raw.Trim();
            return "(no diagnostic captured)";
        }

        // ---- nvcc / host-compiler discovery (self-contained; mirrors NvccVerifier so Workspace needn't reference the CUDA compiler) ----

        private static string FindNvcc()
        {
            string onPath = FindOnPath("nvcc.exe") ?? FindOnPath("nvcc");
            if (onPath != null) return onPath;
            foreach (string root in new[] {
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA",
                @"C:\Program Files (x86)\NVIDIA GPU Computing Toolkit\CUDA" })
            {
                if (!Directory.Exists(root)) continue;
                var versions = new List<string>(Directory.GetDirectories(root));
                versions.Sort(StringComparer.OrdinalIgnoreCase); versions.Reverse();
                foreach (string v in versions)
                {
                    string cand = Path.Combine(v, "bin", "nvcc.exe");
                    if (File.Exists(cand)) return cand;
                }
            }
            return null;
        }

        private static string FindHostCompilerDir()
        {
            var vsRoots = new List<string>();
            foreach (string pf in new[] { @"C:\Program Files\Microsoft Visual Studio", @"C:\Program Files (x86)\Microsoft Visual Studio" })
                if (Directory.Exists(pf)) vsRoots.Add(pf);
            string best = null, bestVer = null;
            foreach (string vsRoot in vsRoots)
                foreach (string edition in SafeDirs(vsRoot))
                    foreach (string ed2 in SafeDirs(edition))
                    {
                        string msvc = Path.Combine(ed2, "VC", "Tools", "MSVC");
                        if (!Directory.Exists(msvc)) continue;
                        foreach (string toolset in Directory.GetDirectories(msvc))
                        {
                            string clDir = Path.Combine(toolset, "bin", "Hostx64", "x64");
                            if (!File.Exists(Path.Combine(clDir, "cl.exe"))) continue;
                            string ver = Path.GetFileName(toolset);
                            if (bestVer == null || string.CompareOrdinal(ver, bestVer) > 0) { bestVer = ver; best = clDir; }
                        }
                    }
            return best;
        }

        private static IEnumerable<string> SafeDirs(string p) { try { return Directory.GetDirectories(p); } catch { return new string[0]; } }

        private static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string cand; try { cand = Path.Combine(dir.Trim(), exe); } catch { continue; }
                if (File.Exists(cand)) return cand;
            }
            return null;
        }

        private static string QueryVersion(string nvcc)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = nvcc, Arguments = "--version", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000);
                    Match m = Regex.Match(outp, @"release [\d.]+, V[\d.]+");
                    return m.Success ? m.Value : "unknown";
                }
            }
            catch { return "unknown"; }
        }

        private static string Quote(string s) { return "\"" + s + "\""; }
    }
}
