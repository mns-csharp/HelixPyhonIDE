using System;
using System.IO;

namespace HelixPythonNamespace.VirtualCompiler.Compliance
{
    /// <summary>
    /// Decides whether a file belongs to a third-party dependency rather than the user.
    ///
    /// Both compliance rules exempt third-party code, so this is the boundary that keeps them
    /// usable: a vendored C++ style checker or a library's Keras exporter is not code the user can
    /// restructure, and blocking Run until they rewrite someone else's package would make the
    /// rules unusable rather than protective.
    ///
    /// Detection is by directory convention, because that is what actually identifies vendored
    /// code on disk. It is deliberately generous — a false "third-party" only means a file is not
    /// policed, whereas a false "user-written" blocks the user's Run button over code they do not
    /// own.
    /// </summary>
    public static class ThirdPartyDetector
    {
        private static readonly string[] VendorSegments =
        {
            "site-packages", "dist-packages",
            ".venv", "venv", "env", ".env", "virtualenv",
            "node_modules", ".tox", ".nox",
            "_thirdparty", "thirdparty", "third_party", "3rdparty",
            "vendor", "vendored", "external", "externals",
            ".git", "__pycache__", ".mypy_cache", ".pytest_cache",
            "build", "dist", ".eggs",
        };

        /// <summary>True when any path segment marks the file as vendored or generated.</summary>
        public static bool IsThirdParty(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            // Splits on BOTH separators on every platform. Path.DirectorySeparatorChar and
            // AltDirectorySeparatorChar are the same character on POSIX, so splitting on those two
            // would leave a Windows-style path — one persisted into a .toml or a ledger on Windows
            // and read back on Linux — as one unsplittable segment, and every file under it would
            // be judged user-written. See HelixPythonNamespace.Core.IO.PathComparison.SplitSegments.
            string[] segments;
            try { segments = HelixPythonNamespace.Core.IO.PathComparison.SplitSegments(filePath); }
            catch { return false; }

            foreach (string segment in segments)
            {
                foreach (string vendor in VendorSegments)
                {
                    // Deliberately case-INSENSITIVE on every platform, unlike the path-identity
                    // comparisons in SolutionPolicy. This is a convention question ("does this
                    // folder name mean vendored code?"), not an identity one, and the class
                    // comment's asymmetry applies: a false third-party leaves one file unpoliced,
                    // a false user-written blocks Run over code the user cannot restructure.
                    if (string.Equals(segment, vendor, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                // A .egg-info / .dist-info metadata directory marks an installed distribution.
                if (segment.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)
                    || segment.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
