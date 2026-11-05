namespace HelixPythonNamespace.Catalog
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Fingerprint of a Python installation: interpreter path + version, site-packages
    /// directory and a hash over its top-level listing. Used to detect when a cached
    /// catalog is stale. Port of the C++ <c>helix::catalog</c> fingerprint functions.
    /// </summary>
    public sealed class InstallationFingerprint
    {
        public string InterpreterPath = "";
        public string InterpreterVersion = "";
        public string SitePackagesDir = "";
        public string SitePackagesHash = ""; // hex FNV-1a 64-bit over directory listing
        public bool IsEmpty()
        {
            return string.IsNullOrEmpty(InterpreterPath) && string.IsNullOrEmpty(SitePackagesHash);
        }

        /// <summary>Synchronously runs the interpreter to discover version + site-packages, then hashes the listing.</summary>
        public static InstallationFingerprint Compute(string interpreterPath)
        {
            InstallationFingerprint fp = new InstallationFingerprint();
            if (string.IsNullOrEmpty(interpreterPath))
            {
                return fp;
            }
            fp.InterpreterPath = interpreterPath;
            fp.InterpreterVersion = DiscoverVersion(interpreterPath);
            fp.SitePackagesDir = DiscoverSitePackages(interpreterPath);
            fp.SitePackagesHash = HashSitePackages(fp.SitePackagesDir);
            return fp;
        }

        public static bool DiffersFrom(InstallationFingerprint a, InstallationFingerprint b)
        {
            if (a.InterpreterPath != b.InterpreterPath) return true;
            if (a.InterpreterVersion != b.InterpreterVersion) return true;
            if (a.SitePackagesDir != b.SitePackagesDir) return true;
            if (a.SitePackagesHash != b.SitePackagesHash) return true;
            return false;
        }

        public static bool Save(string path, InstallationFingerprint fp)
        {
            try
            {
                JObject j = new JObject
                {
                    ["interpreterPath"] = fp.InterpreterPath,
                    ["interpreterVersion"] = fp.InterpreterVersion,
                    ["sitePackagesDir"] = fp.SitePackagesDir,
                    ["sitePackagesHash"] = fp.SitePackagesHash,
                };
                File.WriteAllText(path, j.ToString(Newtonsoft.Json.Formatting.None));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Load(string path, out InstallationFingerprint outFp)
        {
            outFp = new InstallationFingerprint();
            JObject j;
            try
            {
                j = JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }
            outFp.InterpreterPath = j.Value<string>("interpreterPath") ?? "";
            outFp.InterpreterVersion = j.Value<string>("interpreterVersion") ?? "";
            outFp.SitePackagesDir = j.Value<string>("sitePackagesDir") ?? "";
            outFp.SitePackagesHash = j.Value<string>("sitePackagesHash") ?? "";
            return true;
        }

        // ---- hashing ----

        public static ulong FnvHash64(string s)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < s.Length; i++)
            {
                byte c = (byte)s[i];
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }

        public static string ToHex(ulong h)
        {
            const string digits = "0123456789abcdef";
            char[] outChars = new char[16];
            for (int i = 15; i >= 0; i--)
            {
                outChars[i] = digits[(int)(h & 0xF)];
                h >>= 4;
            }
            return new string(outChars);
        }

        // ---- subprocess discovery ----

        private static string RunPython(string interpreterPath, string args, int timeoutMs = 5000)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = interpreterPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        return "";
                    }
                    string outText = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "";
                    }
                    return outText;
                }
            }
            catch
            {
                return "";
            }
        }

        private static string DiscoverVersion(string interpreterPath)
        {
            string v = RunPython(interpreterPath, "-c \"import sys;print(sys.version.split('\\n')[0])\"");
            return (v ?? "").TrimEnd('\n', '\r', ' ');
        }

        private static string DiscoverSitePackages(string interpreterPath)
        {
            string outText = RunPython(interpreterPath,
                "-c \"import json,site;print(json.dumps([p for p in site.getsitepackages()+[site.getusersitepackages()] if p]))\"");
            if (string.IsNullOrEmpty(outText))
            {
                return "";
            }
            try
            {
                JArray arr = JArray.Parse(outText.Trim());
                foreach (JToken v in arr)
                {
                    if (v.Type != JTokenType.String) continue;
                    string p = v.Value<string>();
                    if (Directory.Exists(p)) return p;
                }
            }
            catch { }
            return "";
        }

        private static string HashSitePackages(string sitePackagesDir)
        {
            if (string.IsNullOrEmpty(sitePackagesDir) || !Directory.Exists(sitePackagesDir))
            {
                return "";
            }
            List<string> rows = new List<string>();
            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(sitePackagesDir))
                {
                    string name = Path.GetFileName(entry);
                    long rep;
                    try { rep = Directory.GetLastWriteTimeUtc(entry).Ticks; }
                    catch { rep = File.GetLastWriteTimeUtc(entry).Ticks; }
                    string row = name + "|" + rep;
                    if (Directory.Exists(entry) && name.Length > 10 &&
                        name.Substring(name.Length - 10) == ".dist-info")
                    {
                        string rec = Path.Combine(entry, "RECORD");
                        if (File.Exists(rec))
                        {
                            row += "|RECORD=" + new FileInfo(rec).Length;
                        }
                    }
                    rows.Add(row);
                }
            }
            catch
            {
                return "";
            }
            rows.Sort(StringComparer.Ordinal);
            System.Text.StringBuilder all = new System.Text.StringBuilder();
            foreach (string r in rows)
            {
                all.Append(r).Append('\n');
            }
            return ToHex(FnvHash64(all.ToString()));
        }
    }
}
