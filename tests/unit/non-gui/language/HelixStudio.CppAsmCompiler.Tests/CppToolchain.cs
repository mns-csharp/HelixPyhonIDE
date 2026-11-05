using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HelixPythonNamespace.CppAsmCompiler.Tests
{
    /// <summary>
    /// Locates the real assembler, linker and C++ compiler on this machine.
    ///
    /// Nothing here is hardcoded to one developer's install: paths are discovered, and if a tool is
    /// genuinely absent the execution tests report inconclusive rather than inventing a pass. The
    /// point of the oracle is that the assembly is really assembled, really linked and really
    /// executed, against a real C++ compiler's answer — a mocked toolchain would prove only that
    /// the emitter produces text.
    /// </summary>
    internal static class CppToolchain
    {
        private static readonly Lazy<string> _nasm = new Lazy<string>(FindNasm);
        private static readonly Lazy<string> _link = new Lazy<string>(FindLink);
        private static readonly Lazy<string> _kernel32 = new Lazy<string>(FindKernel32);
        private static readonly Lazy<string> _vcvars = new Lazy<string>(FindVcVars);

        public static string Nasm { get { return _nasm.Value; } }
        public static string Link { get { return _link.Value; } }
        public static string Kernel32Lib { get { return _kernel32.Value; } }
        /// <summary>vcvars64.bat — cl.exe needs its INCLUDE/LIB environment to find the CRT.</summary>
        public static string VcVars { get { return _vcvars.Value; } }

        /// <summary>Null when everything needed to build and run both sides is present.</summary>
        public static string MissingTool()
        {
            if (Nasm == null) return "nasm.exe (set NASM_EXE or put it on PATH)";
            if (Link == null) return "MSVC link.exe";
            if (Kernel32Lib == null) return "kernel32.lib from the Windows SDK";
            if (VcVars == null) return "vcvars64.bat (MSVC C++ toolset — needed as the semantic oracle)";
            return null;
        }

        private static string FindNasm()
        {
            string env = Environment.GetEnvironmentVariable("NASM_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"bin\NASM\nasm.exe"),
                @"C:\Program Files\NASM\nasm.exe",
                @"C:\Program Files (x86)\NASM\nasm.exe",
            };
            candidates.AddRange(OnPath("nasm.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string FindLink()
        {
            // The `link` on PATH is frequently GNU coreutils' hard-link tool, which will happily
            // accept the arguments and produce nothing useful, so the MSVC linker is located by its
            // install layout instead of by name.
            foreach (string install in VisualStudioInstalls())
            {
                string tools = Path.Combine(install, @"VC\Tools\MSVC");
                if (!Directory.Exists(tools)) continue;
                string best = Directory.GetDirectories(tools)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, @"bin\HostX64\x64\link.exe"))
                    .FirstOrDefault(File.Exists);
                if (best != null) return best;
            }
            foreach (string programFiles in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
            {
                string vs = Path.Combine(programFiles, "Microsoft Visual Studio");
                if (!Directory.Exists(vs)) continue;
                string best = SafeEnumerate(vs, "link.exe")
                    .Where(p => p.IndexOf(@"\Tools\MSVC\", StringComparison.OrdinalIgnoreCase) >= 0
                             && p.IndexOf(@"\HostX64\x64\", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (best != null) return best;
            }
            return null;
        }

        private static string FindVcVars()
        {
            foreach (string install in VisualStudioInstalls())
            {
                string vcvars = Path.Combine(install, @"VC\Auxiliary\Build\vcvars64.bat");
                if (File.Exists(vcvars)) return vcvars;
            }
            foreach (string programFiles in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
            {
                string vs = Path.Combine(programFiles, "Microsoft Visual Studio");
                if (!Directory.Exists(vs)) continue;
                string best = SafeEnumerate(vs, "vcvars64.bat")
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (best != null) return best;
            }
            return null;
        }

        private static string FindKernel32()
        {
            string root = @"C:\Program Files (x86)\Windows Kits\10\Lib";
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, @"um\x64\kernel32.lib"))
                .FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// Every Visual Studio install with the C++ toolset, newest first, as vswhere reports it.
        /// Visual Studio can live on any drive; the Program Files scan in the callers only sees the
        /// default location, so it remains as the fallback for a machine without vswhere.
        /// </summary>
        internal static IEnumerable<string> VisualStudioInstalls()
        {
            string vswhere = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (!File.Exists(vswhere)) return Enumerable.Empty<string>();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(vswhere,
                    "-all -products * -sort -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 " +
                    "-property installationPath")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(s => s.Trim()).Where(Directory.Exists).ToList();
                }
            }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }

        // A Visual Studio tree contains directories the current user cannot open; the default
        // enumerator throws on the first one and finds nothing.
        private static IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] files;
                try { files = Directory.GetFiles(dir, pattern); }
                catch (Exception) { continue; }
                foreach (string f in files) yield return f;
                string[] subdirectories;
                try { subdirectories = Directory.GetDirectories(dir); }
                catch (Exception) { continue; }
                foreach (string d in subdirectories) pending.Push(d);
            }
        }

        private static IEnumerable<string> OnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), exe); }
                catch (ArgumentException) { continue; }   // PATH entries can contain junk
                yield return candidate;
            }
        }
    }
}
