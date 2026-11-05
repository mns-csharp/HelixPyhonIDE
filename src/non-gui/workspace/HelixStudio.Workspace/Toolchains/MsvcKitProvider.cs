using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HelixPythonNamespace.Workspace.PyToCpp;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>
    /// Adds the Visual C++ toolsets to the C / C++ kit.
    ///
    /// MSVC is the one major compiler that a PATH probe cannot see: <c>cl.exe</c> is only on PATH
    /// inside a developer command prompt, so a machine with Visual Studio installed would otherwise
    /// report no Microsoft compiler at all. <see cref="MsvcToolchain.LocateVsInstall"/> already
    /// answers this through vswhere for the Python→C++ build path; this reuses it rather than
    /// adding a fourth MSVC locator to the repository.
    /// </summary>
    public sealed class MsvcKitProvider : IKitProvider
    {
        private readonly Func<string> _locateInstall;

        public MsvcKitProvider(Func<string> locateInstall = null)
        {
            _locateInstall = locateInstall ?? MsvcToolchain.LocateVsInstall;
        }

        public string Language { get { return "C / C++"; } }

        public IList<InstalledKit> Discover(CancellationToken cancellationToken)
        {
            var kits = new List<InstalledKit>();
            if (cancellationToken.IsCancellationRequested) return kits;

            string install;
            try { install = _locateInstall(); }
            catch (Exception) { return kits; }
            if (string.IsNullOrEmpty(install)) return kits;

            string toolsRoot = SafeCombine(install, @"VC\Tools\MSVC");
            if (toolsRoot == null || !SafeExists(toolsRoot)) return kits;

            foreach (string toolset in SortedToolsets(toolsRoot))
            {
                if (cancellationToken.IsCancellationRequested) break;
                string cl = SafeCombine(toolset, @"bin\Hostx64\x64\cl.exe");
                if (cl == null || !File.Exists(cl)) continue;

                // The version is the toolset directory's own name (19.38.33130 style). cl.exe is
                // NOT launched to confirm it: outside a vcvars environment it can fail on a missing
                // runtime DLL, and a compiler that is sitting on disk is installed whether or not
                // this process can start it.
                string version = new DirectoryInfo(toolset).Name;
                kits.Add(new InstalledKit("C / C++", "MSVC toolset " + version, version, cl,
                    KitOutcome.Installed, "found via vswhere in " + install));
            }
            return kits;
        }

        /// <summary>Newest toolset first — that is the one a build would pick.</summary>
        private static IList<string> SortedToolsets(string toolsRoot)
        {
            var directories = new List<string>();
            try { directories.AddRange(Directory.GetDirectories(toolsRoot)); }
            catch (IOException) { return directories; }
            catch (UnauthorizedAccessException) { return directories; }
            directories.Sort(delegate (string a, string b)
            {
                return string.Compare(new DirectoryInfo(b).Name, new DirectoryInfo(a).Name,
                    StringComparison.OrdinalIgnoreCase);
            });
            return directories;
        }

        private static string SafeCombine(string a, string b)
        {
            try { return Path.Combine(a, b); }
            catch (ArgumentException) { return null; }
        }

        private static bool SafeExists(string directory)
        {
            try { return Directory.Exists(directory); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
