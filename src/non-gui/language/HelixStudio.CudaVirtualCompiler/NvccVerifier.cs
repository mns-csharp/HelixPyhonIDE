using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.CudaVirtualCompiler
{
    /// <summary>
    /// One diagnostic reported by the REAL <c>nvcc</c> toolchain (device front-end <c>cicc</c> and/or
    /// the MSVC host compiler), parsed from nvcc's stderr.
    /// </summary>
    public sealed class NvccDiagnostic
    {
        public string FilePath;
        public int Line;
        public int Column;
        public bool IsError;   // true = error/fatal, false = warning
        public string Message;

        public override string ToString()
        {
            return FilePath + "(" + Line + "): " + (IsError ? "error" : "warning") + ": " + Message;
        }
    }

    /// <summary>Result of an nvcc verification pass over a single translation unit.</summary>
    public sealed class NvccVerifyResult
    {
        public bool Available;                 // false when nvcc / a host compiler couldn't be located or invoked
        public string Reason;                  // why it's unavailable (diagnostic aid)
        public int ExitCode;                   // nvcc's process exit code (0 = compiled clean)
        public string NvccVersion;             // e.g. "release 13.3, V13.3.33"
        public readonly List<NvccDiagnostic> Diagnostics = new List<NvccDiagnostic>();

        public int ErrorCount
        {
            get { int n = 0; foreach (var d in Diagnostics) if (d.IsError) n++; return n; }
        }
        public int WarningCount
        {
            get { int n = 0; foreach (var d in Diagnostics) if (!d.IsError) n++; return n; }
        }
    }

    /// <summary>
    /// Shells out to the real CUDA compiler (<c>nvcc</c>) to obtain AUTHORITATIVE diagnostics for a
    /// <c>.cu</c> translation unit — the CUDA analogue of the Python side running real CPython / pytest /
    /// debugpy. The virtual compiler (<see cref="CudaVirtualCompiler"/>) is a fast static checker that can
    /// miss hard errors (a missing <c>;</c>, an undeclared identifier); this brings ground truth into the
    /// pipeline whenever an nvcc install is present.
    ///
    /// It performs a compile-to-object syntax/semantic check (<c>nvcc -c file.cu -o &lt;tmp&gt;</c>) with the
    /// MSVC host resolved via <c>-ccbin</c>, captures stderr, and parses both cicc-style
    /// <c>file(line): error: msg</c> and MSVC <c>file(line): error C####: msg</c> lines.
    /// </summary>
    public static class NvccVerifier
    {
        /// <summary>True when an nvcc executable can be located (PATH or a standard CUDA install).</summary>
        public static bool IsAvailable { get { return FindNvcc() != null; } }

        public static NvccVerifyResult Verify(string cuFilePath, int timeoutMs = 60000)
        {
            var result = new NvccVerifyResult();
            if (string.IsNullOrEmpty(cuFilePath) || !File.Exists(cuFilePath))
            { result.Reason = "file not found: " + cuFilePath; return result; }

            // Absolute path: nvcc runs with a temp working directory, so a relative source path wouldn't resolve.
            string absCu = Path.GetFullPath(cuFilePath);

            string nvcc = FindNvcc();
            if (nvcc == null) { result.Reason = "nvcc not found on PATH or standard CUDA install"; return result; }
            result.NvccVersion = QueryVersion(nvcc);

            string ccbin = FindHostCompilerDir();   // may be null; nvcc falls back to its own detection

            string tmpDir = Path.Combine(Path.GetTempPath(), "helix_nvcc_" + Guid.NewGuid().ToString("N"));
            string outObj = Path.Combine(tmpDir, "out.obj");
            try
            {
                Directory.CreateDirectory(tmpDir);

                var args = new StringBuilder();
                args.Append("-c ");
                args.Append(Quote(absCu));
                args.Append(" -o ").Append(Quote(outObj));
                if (ccbin != null) args.Append(" -ccbin ").Append(Quote(ccbin));

                var psi = new ProcessStartInfo
                {
                    FileName = nvcc,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = tmpDir,
                };

                using (var p = Process.Start(psi))
                {
                    string stderr = p.StandardError.ReadToEnd();
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        result.Reason = "nvcc timed out after " + timeoutMs + "ms";
                        return result;
                    }
                    result.ExitCode = p.ExitCode;
                    ParseDiagnostics(stderr + "\n" + stdout, absCu, result.Diagnostics);
                    result.Available = true;
                }
            }
            catch (Exception ex)
            {
                result.Reason = "nvcc invocation failed: " + ex.Message;
                result.Available = false;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
            return result;
        }

        // nvcc/cicc: "file.cu(12): error: identifier "x" is undefined" / "...: warning #177-D: ..."
        // MSVC host: "file.cu(12): error C2143: ..." / "file.cu(12): warning C4101: ..."
        private static readonly Regex DiagLine = new Regex(
            @"^(?<file>.*?)\((?<line>\d+)\)\s*:\s*(?<sev>error|warning|catastrophic error|fatal error)\b[^:]*:\s*(?<msg>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void ParseDiagnostics(string text, string cuFile, List<NvccDiagnostic> outList)
        {
            if (string.IsNullOrEmpty(text)) return;
            string cuName = Path.GetFileName(cuFile);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                Match m = DiagLine.Match(line);
                if (!m.Success) continue;
                // Only surface diagnostics anchored to the file under test (skip system-header noise).
                string file = m.Groups["file"].Value.Trim();
                if (!file.EndsWith(cuName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(file), cuName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string sev = m.Groups["sev"].Value.ToLowerInvariant();
                int ln; int.TryParse(m.Groups["line"].Value, out ln);
                outList.Add(new NvccDiagnostic
                {
                    FilePath = file,
                    Line = ln,
                    Column = 0,
                    IsError = sev.Contains("error"),
                    Message = m.Groups["msg"].Value.Trim(),
                });
            }
        }

        private static string FindNvcc()
        {
            // 1) PATH
            string onPath = FindOnPath("nvcc.exe") ?? FindOnPath("nvcc");
            if (onPath != null) return onPath;
            // 2) Standard CUDA install roots (newest first by directory name).
            foreach (string root in new[] {
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA",
                @"C:\Program Files (x86)\NVIDIA GPU Computing Toolkit\CUDA" })
            {
                if (!Directory.Exists(root)) continue;
                var versions = new List<string>(Directory.GetDirectories(root));
                versions.Sort(StringComparer.OrdinalIgnoreCase);
                versions.Reverse();
                foreach (string v in versions)
                {
                    string cand = Path.Combine(v, "bin", "nvcc.exe");
                    if (File.Exists(cand)) return cand;
                }
            }
            return null;
        }

        // Directory containing cl.exe (MSVC host), for nvcc -ccbin. Newest MSVC toolset under any VS.
        private static string FindHostCompilerDir()
        {
            var vsRoots = new List<string>();
            foreach (string pf in new[] { @"C:\Program Files\Microsoft Visual Studio", @"C:\Program Files (x86)\Microsoft Visual Studio" })
                if (Directory.Exists(pf)) vsRoots.Add(pf);

            string best = null; string bestVer = null;
            foreach (string vsRoot in vsRoots)
            {
                foreach (string edition in SafeDirs(vsRoot))                     // 18, 2022, ...
                foreach (string ed2 in SafeDirs(edition))                        // Community, Professional...
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
            }
            return best;
        }

        private static IEnumerable<string> SafeDirs(string p)
        {
            try { return Directory.GetDirectories(p); } catch { return new string[0]; }
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

        private static string QueryVersion(string nvcc)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = nvcc, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    Match m = Regex.Match(outp, @"release [\d.]+, V[\d.]+");
                    return m.Success ? m.Value : "unknown";
                }
            }
            catch { return "unknown"; }
        }

        private static string Quote(string s) { return "\"" + s + "\""; }
    }
}
