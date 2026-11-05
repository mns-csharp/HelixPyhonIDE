using System;
using System.IO;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// What the IDE should do with a path handed to it from outside — a shell double-click, a
    /// command-line argument, a drop. Exactly one of <see cref="SolutionPath"/> and
    /// <see cref="FileToOpen"/> is normally set; <see cref="Reason"/> is always set and is the
    /// sentence the status bar shows.
    /// </summary>
    public sealed class SolutionAnchorResult
    {
        /// <summary>The manifest to hand to <c>OpenSolutionAsync</c>, or null when none was found.</summary>
        public string SolutionPath { get; set; }

        /// <summary>A file to show in the editor instead, when no solution encloses it.</summary>
        public string FileToOpen { get; set; }

        /// <summary>Why this answer — user-readable, for the status bar.</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// Widens an arbitrary path into the SOLUTION that owns it.
    ///
    /// This exists because Windows associates files by extension, so registering HelixPython for
    /// <c>.toml</c> (Tools ▸ Register TOML Files) means the IDE is handed whichever <c>.toml</c>
    /// the user double-clicked: the workspace-root <c>pyproject.toml</c>, a MEMBER package's
    /// <c>pyproject.toml</c> two directories down, or some unrelated config file that merely lives
    /// inside a solution. In all three cases the promised outcome is the same — the whole solution
    /// opens, not one file — so the widening has to happen here rather than at the call site.
    ///
    /// The walk is bounded by the first ancestor containing a <c>.git</c> directory (checked, then
    /// stopped at) and by a depth cap, so a stray <c>pyproject.toml</c> far above an unrelated
    /// checkout can never be adopted as "the solution".
    /// </summary>
    public static class SolutionAnchorResolver
    {
        /// <summary>Ancestor directories examined before giving up. Far deeper than any real tree.</summary>
        private const int MaxAncestors = 64;

        /// <summary>
        /// Resolve <paramref name="path"/> to the solution manifest that should be opened.
        /// Never throws and never returns null: an unusable path comes back with both targets null
        /// and a <see cref="SolutionAnchorResult.Reason"/> saying why.
        /// </summary>
        public static SolutionAnchorResult Resolve(string path)
        {
            var result = new SolutionAnchorResult();
            if (string.IsNullOrWhiteSpace(path))
            {
                result.Reason = "No path was supplied.";
                return result;
            }

            string full;
            try { full = Path.GetFullPath(path.Trim('"')); }
            catch (Exception ex)
            {
                result.Reason = "Not a usable path (" + ex.Message + "): " + path;
                return result;
            }

            // A directory a language claims as a workspace (a CMakeLists.txt folder today) opens
            // directly, the same way a drop of that folder does.
            if (Directory.Exists(full))
            {
                if (LanguageServices.LanguageWorkspaceRegistry.TryDetect(full) != null)
                {
                    result.SolutionPath = full;
                    result.Reason = "Opening workspace folder " + full;
                }
                else
                {
                    result.Reason = "No project manifest in " + full;
                }
                return result;
            }

            if (!File.Exists(full))
            {
                result.Reason = "File not found: " + full;
                return result;
            }

            string name = Path.GetFileName(full);
            bool isPyProject = string.Equals(name, "pyproject.toml", StringComparison.OrdinalIgnoreCase);

            // Another language's manifest (Cargo.toml, Project.toml, *.csproj, *.sln, ...) is
            // already the root of its own workspace — its provider owns the widening.
            if (!isPyProject && LanguageServices.LanguageWorkspaceRegistry.IsRecognizedProjectPath(full))
            {
                result.SolutionPath = full;
                result.Reason = "Opening " + name;
                return result;
            }

            string startDir = Path.GetDirectoryName(full);
            string outermostWorkspace = null;   // highest ancestor declaring [tool.uv.workspace]
            string nearestPyProject = null;     // closest ancestor with any pyproject.toml

            string dir = startDir;
            for (int depth = 0; depth < MaxAncestors && !string.IsNullOrEmpty(dir); depth++)
            {
                string candidate = Path.Combine(dir, "pyproject.toml");
                if (File.Exists(candidate))
                {
                    if (nearestPyProject == null) nearestPyProject = candidate;
                    if (DeclaresWorkspace(candidate)) outermostWorkspace = candidate;
                }

                // Repository boundary: check this directory, then stop. Beyond it any manifest
                // belongs to a different checkout.
                if (Directory.Exists(Path.Combine(dir, ".git"))) break;

                string parent = Path.GetDirectoryName(dir);
                if (string.Equals(parent, dir, StringComparison.Ordinal)) break;   // volume root
                dir = parent;
            }

            string anchor = outermostWorkspace ?? nearestPyProject;
            if (anchor != null)
            {
                result.SolutionPath = anchor;
                result.Reason = string.Equals(anchor, full, PathIdentity)
                    ? "Opening solution " + anchor
                    : "Opening the solution that owns " + name + ": " + anchor;
                return result;
            }

            // A .toml with no solution above it is still a file the user asked to see.
            result.FileToOpen = full;
            result.Reason = "No solution found above " + name + " — opening the file in the editor.";
            return result;
        }

        private static StringComparison PathIdentity
        {
            get { return HelixPythonNamespace.Core.IO.PathComparison.Comparison; }
        }

        /// <summary>
        /// True when this pyproject.toml declares <c>[tool.uv.workspace].members</c> — i.e. it is a
        /// solution root rather than a member package. A file that cannot be read or parsed is not
        /// a workspace root; a bad manifest must not abort the walk.
        /// </summary>
        private static bool DeclaresWorkspace(string pyProjectPath)
        {
            try
            {
                PyProjectMetadata meta = PyProjectReader.Read(pyProjectPath);
                return meta != null && meta.IsWorkspace;
            }
            catch { return false; }
        }
    }
}
