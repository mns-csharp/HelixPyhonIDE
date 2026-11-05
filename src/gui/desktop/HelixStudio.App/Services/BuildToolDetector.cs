namespace HelixPythonNamespace.App.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;

    // One discovered .NET SDK: its version string and full install path.
    public sealed class DotNetSdkInfo
    {
        public string Version { get; set; }
        public string Path { get; set; }

        public override string ToString()
        {
            return string.Format("dotnet SDK {0}", Version);
        }
    }

    // Discovers the installed .NET SDKs by shelling out to the `dotnet` CLI.
    // Results are cached so the (relatively slow) process launch happens only once.
    public sealed class BuildToolDetector
    {
        private List<DotNetSdkInfo> _cachedSdks;

        public List<DotNetSdkInfo> DetectSdks()
        {
            // Return the memoized result on subsequent calls.
            if (_cachedSdks != null)
            {
                return _cachedSdks;
            }

            _cachedSdks = new List<DotNetSdkInfo>();

            // Launch `dotnet --list-sdks` and parse stdout. Any failure (dotnet not
            // installed, timeout, parse error) leaves the cache as an empty list.
            try
            {
                // Redirect stdout and run windowless so the console SDK listing is
                // captured silently rather than flashing a window.
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-sdks",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return _cachedSdks;
                    }

                    // Read output first, then bound the wait so a hung dotnet can't stall the UI.
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    if (!string.IsNullOrEmpty(output))
                    {
                        // One SDK per line; split on CR/LF and parse each entry.
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            // Format: "8.0.100 [C:\Program Files\dotnet\sdk]"
                            // Split at the '[' into the version (before) and install root (inside brackets).
                            int bracketIndex = line.IndexOf('[');
                            if (bracketIndex > 0)
                            {
                                string version = line.Substring(0, bracketIndex).Trim();
                                string path = line.Substring(bracketIndex + 1).TrimEnd(']').Trim();
                                // Store the full path to the versioned SDK folder (root + version).
                                _cachedSdks.Add(new DotNetSdkInfo
                                {
                                    Version = version,
                                    Path = System.IO.Path.Combine(path, version)
                                });
                            }
                        }
                    }
                }
            }
            catch
            {
                // dotnet not found or error running it
            }

            return _cachedSdks;
        }

        // True when at least one SDK was found (i.e. dotnet is usable for builds).
        public bool HasDotNet()
        {
            return DetectSdks().Count > 0;
        }

        // Drop the cache so the next DetectSdks() re-queries the CLI (e.g. after an install).
        public void Refresh()
        {
            _cachedSdks = null;
        }
    }
}
