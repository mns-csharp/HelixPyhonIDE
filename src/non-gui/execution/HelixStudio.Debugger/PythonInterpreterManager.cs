using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace HelixPythonNamespace.Debugger
{
    /// <summary>
    /// Represents a discovered Python interpreter. Matches the public surface
    /// <see cref="PythonDebugEngine"/> expects from the original PythonCodeEditor
    /// implementation.
    /// </summary>
    public sealed class PythonInterpreter
    {
        public string ExecutablePath { get; set; }
        public string Version { get; set; }
        public string Architecture { get; set; }   // "x86_64" / "x86" / ""
        public string Flavor { get; set; }         // "CPython" / "Anaconda" / "Miniconda" / "pyenv" / "Store" / "Other"
        public string Source { get; set; }         // "registry HKLM" / "py -0p" / "PATH" / "well-known" / "browse"
        public bool HasPythonNet { get; set; }     // `import pythonnet` worked
    }

    public sealed class PythonInterpreterManager
    {
        public PythonInterpreter SelectedInterpreter { get; set; }

        /// <summary>
        /// Minimal one-shot discovery: the user may set <see cref="SelectedInterpreter"/>
        /// explicitly; if unset, scan <c>PATH</c> and return the first match.
        /// </summary>
        public PythonInterpreter DiscoverDefault()
        {
            if (SelectedInterpreter != null) return SelectedInterpreter;
            foreach (var i in ScanPath())
            {
                SelectedInterpreter = i;
                return i;
            }
            return null;
        }

        /// <summary>
        /// Enumerate every Python interpreter we can find on this machine — registry,
        /// well-known install paths, the Windows Python launcher (<c>py -0p</c>), and
        /// PATH. Deduplicates by canonical path. Version / architecture are probed by
        /// invoking each python with a 2s timeout.
        /// </summary>
        public List<PythonInterpreter> DiscoverAll()
        {
            var byPath = new Dictionary<string, PythonInterpreter>(StringComparer.OrdinalIgnoreCase);

            void addIfNew(PythonInterpreter candidate)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.ExecutablePath)) return;
                string key;
                try { key = Path.GetFullPath(candidate.ExecutablePath); }
                catch { key = candidate.ExecutablePath; }
                if (!File.Exists(key)) return;
                if (byPath.ContainsKey(key)) return;
                candidate.ExecutablePath = key;
                byPath[key] = candidate;
            }

            foreach (var p in ScanRegistry()) addIfNew(p);
            foreach (var p in ScanWellKnownPaths()) addIfNew(p);
            foreach (var p in ScanPyLauncher()) addIfNew(p);
            foreach (var p in ScanPath()) addIfNew(p);

            // Probe version / arch / pythonnet for each.
            foreach (var i in byPath.Values) ProbeMetadata(i);

            return byPath.Values.OrderBy(i => i.Flavor).ThenByDescending(i => i.Version).ToList();
        }

        // --------------------------------------------------------------- sources

        private static IEnumerable<PythonInterpreter> ScanPath()
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] dirs = pathEnv.Split(Path.PathSeparator);
            string[] candidates = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new[] { "python.exe", "python3.exe" }
                : new[] { "python3", "python" };
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (string exe in candidates)
                {
                    string full;
                    try { full = Path.Combine(dir, exe); } catch { continue; }
                    if (File.Exists(full))
                    {
                        yield return new PythonInterpreter
                        {
                            ExecutablePath = full,
                            Flavor = DeriveFlavor(full),
                            Source = "PATH",
                        };
                    }
                }
            }
        }

        private static IEnumerable<PythonInterpreter> ScanRegistry()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) yield break;
            foreach (var sub in EnumerateRegistrySubkeys(Registry.LocalMachine, @"SOFTWARE\Python\PythonCore"))
                yield return sub;
            foreach (var sub in EnumerateRegistrySubkeys(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Python\PythonCore"))
                yield return sub;
            foreach (var sub in EnumerateRegistrySubkeys(Registry.CurrentUser,  @"SOFTWARE\Python\PythonCore"))
                yield return sub;
            foreach (var sub in EnumerateRegistrySubkeys(Registry.LocalMachine, @"SOFTWARE\Python\ContinuumAnalytics"))
                yield return sub;
            foreach (var sub in EnumerateRegistrySubkeys(Registry.CurrentUser,  @"SOFTWARE\Python\ContinuumAnalytics"))
                yield return sub;
        }

        private static IEnumerable<PythonInterpreter> EnumerateRegistrySubkeys(RegistryKey root, string path)
        {
            RegistryKey parent = null;
            try { parent = root.OpenSubKey(path); }
            catch { yield break; }
            if (parent == null) yield break;
            string[] versions;
            try { versions = parent.GetSubKeyNames(); }
            catch { parent.Close(); yield break; }

            foreach (string v in versions)
            {
                RegistryKey installKey = null;
                string installPath = null;
                try
                {
                    using (var sub = parent.OpenSubKey(v))
                    {
                        installKey = sub?.OpenSubKey("InstallPath");
                        if (installKey != null)
                        {
                            installPath = installKey.GetValue(null) as string
                                      ?? installKey.GetValue("ExecutablePath") as string;
                        }
                    }
                }
                catch { installPath = null; }
                finally { installKey?.Close(); }

                if (string.IsNullOrEmpty(installPath)) continue;

                string exe;
                if (File.Exists(installPath) && installPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    exe = installPath;
                else
                    exe = Path.Combine(installPath, "python.exe");

                if (!File.Exists(exe)) continue;
                yield return new PythonInterpreter
                {
                    ExecutablePath = exe,
                    Version = v,
                    Flavor = DeriveFlavor(exe),
                    Source = root == Registry.LocalMachine ? "registry HKLM" : "registry HKCU",
                };
            }
            parent.Close();
        }

        private static IEnumerable<PythonInterpreter> ScanWellKnownPaths()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) yield break;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            string[] rootGlobs =
            {
                @"C:\Python*",
                @"C:\Program Files\Python*",
                @"C:\Program Files (x86)\Python*",
                Path.Combine(local, @"Programs\Python\Python*"),
            };
            foreach (var pat in rootGlobs)
            {
                foreach (string exe in ExpandExePattern(pat, "python.exe"))
                    yield return new PythonInterpreter
                    {
                        ExecutablePath = exe, Flavor = DeriveFlavor(exe), Source = "well-known"
                    };
            }

            // Store launcher stub (reparse point).
            string storeStub = Path.Combine(local, @"Microsoft\WindowsApps\python.exe");
            if (File.Exists(storeStub))
                yield return new PythonInterpreter
                {
                    ExecutablePath = storeStub, Flavor = "Store", Source = "well-known"
                };

            // Anaconda / Miniconda.
            string[] condaRoots =
            {
                Path.Combine(home, "Anaconda3"),
                Path.Combine(home, "Miniconda3"),
                Path.Combine(progData, "Anaconda3"),
                Path.Combine(progData, "Miniconda3"),
            };
            foreach (string r in condaRoots)
            {
                string exe = Path.Combine(r, "python.exe");
                if (File.Exists(exe))
                    yield return new PythonInterpreter
                    {
                        ExecutablePath = exe, Flavor = "Anaconda", Source = "well-known"
                    };
            }

            // pyenv-win.
            string pyenv = Path.Combine(home, @".pyenv\pyenv-win\versions");
            if (Directory.Exists(pyenv))
            {
                string[] verDirs = null;
                try { verDirs = Directory.GetDirectories(pyenv); } catch { }
                if (verDirs != null)
                {
                    foreach (string v in verDirs)
                    {
                        string exe = Path.Combine(v, "python.exe");
                        if (File.Exists(exe))
                            yield return new PythonInterpreter
                            {
                                ExecutablePath = exe, Flavor = "pyenv", Source = "well-known"
                            };
                    }
                }
            }
        }

        private static IEnumerable<string> ExpandExePattern(string pattern, string leaf)
        {
            string parent = Path.GetDirectoryName(pattern);
            string mask = Path.GetFileName(pattern);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) yield break;
            string[] dirs;
            try { dirs = Directory.GetDirectories(parent, mask); } catch { yield break; }
            foreach (string d in dirs)
            {
                string exe = Path.Combine(d, leaf);
                if (File.Exists(exe)) yield return exe;
            }
        }

        private static IEnumerable<PythonInterpreter> ScanPyLauncher()
        {
            string output = RunCaptured("py", "-0p", timeoutMs: 2000);
            if (string.IsNullOrEmpty(output)) yield break;
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Typical format: " -V:3.12 *        C:\...\python.exe"
                string s = line.Trim();
                int idx = s.LastIndexOf(' ');
                if (idx < 0) continue;
                string path = s.Substring(idx + 1).Trim('"');
                if (!path.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(path)) continue;
                yield return new PythonInterpreter
                {
                    ExecutablePath = path,
                    Flavor = DeriveFlavor(path),
                    Source = "py -0p",
                };
            }
        }

        // --------------------------------------------------------------- probe / flavor

        private static string DeriveFlavor(string exePath)
        {
            string p = (exePath ?? string.Empty).ToLowerInvariant();
            if (p.Contains(@"\anaconda") || p.Contains(@"\miniconda")) return "Anaconda";
            if (p.Contains(@"\pyenv-win\")) return "pyenv";
            if (p.Contains(@"\windowsapps\")) return "Store";
            return "CPython";
        }

        private static void ProbeMetadata(PythonInterpreter i)
        {
            if (i == null || string.IsNullOrEmpty(i.ExecutablePath)) return;
            string version = RunCaptured(i.ExecutablePath,
                "-c \"import sys; print(sys.version_info[0], sys.version_info[1], sys.version_info[2], sys.maxsize)\"",
                timeoutMs: 2000);
            if (!string.IsNullOrEmpty(version))
            {
                string[] parts = version.Trim().Split();
                if (parts.Length >= 4)
                {
                    if (string.IsNullOrEmpty(i.Version))
                        i.Version = parts[0] + "." + parts[1] + "." + parts[2];
                    if (long.TryParse(parts[3], out long maxsize))
                        i.Architecture = maxsize > int.MaxValue ? "x86_64" : "x86";
                }
            }
            // pythonnet probe is non-fatal; a missing module just returns exit != 0.
            string pynet = RunCaptured(i.ExecutablePath,
                "-c \"import pythonnet; print(pythonnet.__version__)\"",
                timeoutMs: 2000);
            i.HasPythonNet = !string.IsNullOrWhiteSpace(pynet);
        }

        private static string RunCaptured(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
                    if (p.ExitCode != 0) return "";
                    return o;
                }
            }
            catch { return ""; }
        }
    }
}
