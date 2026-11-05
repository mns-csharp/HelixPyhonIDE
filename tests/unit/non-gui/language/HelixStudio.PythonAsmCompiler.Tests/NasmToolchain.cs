using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// Locates the real assembler, linker and CPython on this machine.
    ///
    /// Nothing here is hardcoded to one developer's install: paths are discovered, and if a tool
    /// is genuinely absent the execution tests report inconclusive rather than inventing a pass.
    /// The point of the oracle is that the assembly is really assembled, really linked and really
    /// executed — a mocked toolchain would prove only that the emitter produces text.
    /// </summary>
    internal static class NasmToolchain
    {
        private static readonly Lazy<string> _nasm = new Lazy<string>(FindNasm);
        private static readonly Lazy<string> _link = new Lazy<string>(FindLink);
        private static readonly Lazy<string> _kernel32 = new Lazy<string>(FindKernel32);
        private static readonly Lazy<string> _python = new Lazy<string>(FindPython);

        public static string Nasm { get { return _nasm.Value; } }
        public static string Link { get { return _link.Value; } }
        public static string Kernel32Lib { get { return _kernel32.Value; } }
        public static string Python { get { return _python.Value; } }

        /// <summary>Null when everything needed to build and run an executable is present.</summary>
        public static string MissingTool()
        {
            if (Nasm == null) return "nasm.exe (set NASM_EXE or put it on PATH)";
            if (Link == null) return "MSVC link.exe";
            if (Kernel32Lib == null) return "kernel32.lib from the Windows SDK";
            if (Python == null) return "python.exe (needed as the semantic oracle)";
            return null;
        }

        private static string FindNasm()
        {
            string env = Environment.GetEnvironmentVariable("NASM_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            var candidates = new List<string>();
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"bin\NASM\nasm.exe"));
            candidates.Add(@"C:\Program Files\NASM\nasm.exe");
            candidates.Add(@"C:\Program Files (x86)\NASM\nasm.exe");
            candidates.AddRange(OnPath("nasm.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string FindLink()
        {
            // The `link` on PATH is frequently GNU coreutils' hard-link tool, which will happily
            // accept the arguments and produce nothing useful, so the MSVC linker is located by
            // its install layout instead of by name.
            foreach (string install in VisualStudioInstalls())
            {
                string tools = Path.Combine(install, @"VC\Tools\MSVC");
                if (!Directory.Exists(tools)) continue;
                string found = Directory.GetDirectories(tools)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, @"bin\HostX64\x64\link.exe"))
                    .FirstOrDefault(File.Exists);
                if (found != null) return found;
            }
            foreach (string programFiles in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
            {
                string vs = Path.Combine(programFiles, "Microsoft Visual Studio");
                if (!Directory.Exists(vs)) continue;
                string best = Directory
                    .EnumerateFiles(vs, "link.exe", SearchOption.AllDirectories)
                    .Where(p => p.IndexOf(@"\Tools\MSVC\", StringComparison.OrdinalIgnoreCase) >= 0
                             && p.IndexOf(@"\HostX64\x64\", StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (best != null) return best;
            }
            return null;
        }

        /// <summary>
        /// Every Visual Studio install with the C++ toolset, newest first, as vswhere reports it.
        /// Visual Studio can live on any drive; the Program Files scan in FindLink only sees the
        /// default location, so it remains as the fallback for a machine without vswhere.
        /// </summary>
        private static IEnumerable<string> VisualStudioInstalls()
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

        private static string FindKernel32()
        {
            string root = @"C:\Program Files (x86)\Windows Kits\10\Lib";
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, @"um\x64\kernel32.lib"))
                .FirstOrDefault(File.Exists);
        }

        private static string FindPython()
        {
            string env = Environment.GetEnvironmentVariable("PYTHON_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            return OnPath("python.exe").FirstOrDefault(File.Exists);
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
