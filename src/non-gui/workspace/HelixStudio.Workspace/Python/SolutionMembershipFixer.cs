using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>A folder a stray source file can be moved into so it becomes part of a project.</summary>
    public sealed class MembershipTarget
    {
        /// <summary>The workspace member project that owns this folder.</summary>
        public string ProjectName { get; private set; }

        /// <summary>Absolute path of the destination folder.</summary>
        public string DirectoryPath { get; private set; }

        /// <summary>Path shown to the user — relative to the solution root when it is inside it.</summary>
        public string RelativePath { get; private set; }

        /// <summary>
        /// True when the folder is an importable package (it has an <c>__init__.py</c>). A project's
        /// own folder can be a package too (the flat layout this repo's fixtures use); in a src-layout
        /// project it is not, and dropping a module there makes it a workspace member without making
        /// it importable — worth saying out loud in the picker rather than silently offering both as
        /// if they were equivalent.
        /// </summary>
        public bool IsPackage { get; private set; }

        /// <summary>True for the project's own root folder.</summary>
        public bool IsProjectRoot { get; private set; }

        public MembershipTarget(string projectName, string directoryPath, string relativePath,
            bool isPackage, bool isProjectRoot)
        {
            ProjectName = projectName;
            DirectoryPath = directoryPath;
            RelativePath = relativePath;
            IsPackage = isPackage;
            IsProjectRoot = isProjectRoot;
        }
    }

    /// <summary>Outcome of moving a file into a project folder.</summary>
    public sealed class MembershipFixResult
    {
        public bool Succeeded { get; private set; }
        /// <summary>Where the file ended up. Null unless <see cref="Succeeded"/>.</summary>
        public string NewPath { get; private set; }
        /// <summary>A sentence for the status bar or an error dialog. Never null.</summary>
        public string Message { get; private set; }

        private MembershipFixResult(bool ok, string newPath, string message)
        {
            Succeeded = ok;
            NewPath = newPath;
            Message = message ?? string.Empty;
        }

        public static MembershipFixResult Ok(string newPath, string message)
        {
            return new MembershipFixResult(true, newPath, message);
        }

        public static MembershipFixResult Fail(string message)
        {
            return new MembershipFixResult(false, null, message);
        }
    }

    /// <summary>
    /// Resolves rule PY8001 — "this file is not part of any project in the solution".
    ///
    /// Membership is decided by CONTAINMENT (see <c>SolutionPolicy.IsWorkspaceMember</c>): a file
    /// belongs to a project when it lives inside that project's directory. So the fix for a stray
    /// file is to move it into the project the user wants it in — there is no "include without
    /// moving" to offer, because there is no per-file membership list to add it to.
    ///
    /// The other legitimate resolution — making the file's OWN folder a workspace member — is the
    /// existing Solution Explorer ▸ Add Existing Project command, and only applies when the file
    /// sits in a folder of its own. The files that actually trigger this rule most often (loose
    /// scripts at the solution root) have no such folder, which is why moving is what this offers.
    ///
    /// Pure file-system + string work: no UI, no solution model, so it is directly testable.
    /// </summary>
    public static class SolutionMembershipFixer
    {
        // Folders that are never a sensible destination, and never worth walking into.
        private static readonly string[] SkippedFolders =
        {
            "__pycache__", ".venv", "venv", "env", ".git", ".idea", ".vs", ".pytest_cache",
            ".mypy_cache", ".tox", ".nox", "build", "dist", ".eggs", "node_modules",
        };

        /// <summary>How deep below a project root to look for package folders.</summary>
        private const int MaxPackageDepth = 3;

        /// <summary>
        /// Every folder a stray file could be moved into: each member project's own directory plus
        /// the package folders inside it. Package folders come first per project — they are the
        /// answer the user wants nine times out of ten.
        /// </summary>
        /// <param name="solutionRoot">Used only to shorten the displayed paths; may be null.</param>
        /// <param name="projects">One descriptor per workspace member (name + directory).</param>
        public static List<MembershipTarget> TargetsFor(
            string solutionRoot, IEnumerable<PackageDescriptor> projects)
        {
            var targets = new List<MembershipTarget>();
            if (projects == null) return targets;

            foreach (PackageDescriptor p in projects)
            {
                if (p == null || string.IsNullOrEmpty(p.DirectoryPath)) continue;
                string root;
                try { root = Path.GetFullPath(p.DirectoryPath); }
                catch { continue; }
                if (!Directory.Exists(root)) continue;

                var forThisProject = new List<MembershipTarget>();
                forThisProject.Add(new MembershipTarget(
                    p.Name, root, Display(solutionRoot, root), HasInit(root), true));

                foreach (string dir in PackageFoldersUnder(root, 1))
                    forThisProject.Add(new MembershipTarget(
                        p.Name, dir, Display(solutionRoot, dir), true, false));

                // Packages first, then the project root if it isn't itself a package.
                forThisProject.Sort(delegate (MembershipTarget a, MembershipTarget b)
                {
                    if (a.IsPackage != b.IsPackage) return a.IsPackage ? -1 : 1;
                    return string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
                });
                targets.AddRange(forThisProject);
            }
            return targets;
        }

        /// <summary>
        /// Move <paramref name="filePath"/> into <paramref name="target"/>. Refuses rather than
        /// overwrites: a same-named file in the destination is the one case where a silent move
        /// would destroy work.
        /// </summary>
        public static MembershipFixResult AddFileToProject(string filePath, MembershipTarget target)
        {
            if (string.IsNullOrEmpty(filePath)) return MembershipFixResult.Fail("No file to move.");
            if (target == null || string.IsNullOrEmpty(target.DirectoryPath))
                return MembershipFixResult.Fail("No destination project folder was chosen.");

            string source, destDir;
            try
            {
                source = Path.GetFullPath(filePath);
                destDir = Path.GetFullPath(target.DirectoryPath);
            }
            catch (Exception ex) { return MembershipFixResult.Fail("Invalid path: " + ex.Message); }

            if (!File.Exists(source))
                return MembershipFixResult.Fail("'" + Path.GetFileName(filePath) + "' no longer exists.");
            if (!Directory.Exists(destDir))
                return MembershipFixResult.Fail("The destination folder no longer exists: " + destDir);

            string sourceDir = Path.GetDirectoryName(source) ?? string.Empty;
            if (string.Equals(sourceDir.TrimEnd(Path.DirectorySeparatorChar),
                              destDir.TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase))
                return MembershipFixResult.Fail("'" + Path.GetFileName(source) + "' is already in that folder.");

            string dest = Path.Combine(destDir, Path.GetFileName(source));
            if (File.Exists(dest))
                return MembershipFixResult.Fail(
                    "'" + Path.GetFileName(source) + "' already exists in " + target.RelativePath
                    + ". Rename one of them first — this will not overwrite it.");

            try { File.Move(source, dest); }
            catch (Exception ex) { return MembershipFixResult.Fail("Could not move the file: " + ex.Message); }

            return MembershipFixResult.Ok(dest,
                "Moved " + Path.GetFileName(source) + " into " + target.RelativePath
                + " (" + target.ProjectName + ")");
        }

        // Package folders (those carrying an __init__.py) below `dir`, breadth-limited so a deep
        // vendored tree can't turn the picker into a thousand-row list.
        private static IEnumerable<string> PackageFoldersUnder(string dir, int depth)
        {
            if (depth > MaxPackageDepth) yield break;
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { yield break; }

            foreach (string sub in subdirs)
            {
                string leaf = Path.GetFileName(sub);
                if (IsSkipped(leaf)) continue;
                if (HasInit(sub)) yield return sub;
                foreach (string deeper in PackageFoldersUnder(sub, depth + 1)) yield return deeper;
            }
        }

        private static bool IsSkipped(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return true;
            foreach (string s in SkippedFolders)
                if (string.Equals(folderName, s, StringComparison.OrdinalIgnoreCase)) return true;
            return folderName.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)
                || folderName.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasInit(string dir)
        {
            try { return File.Exists(Path.Combine(dir, "__init__.py")); }
            catch { return false; }
        }

        // A path relative to the solution root, so the picker reads "src/lib_utils" and not a
        // 90-character absolute path that no longer fits the column.
        private static string Display(string solutionRoot, string fullPath)
        {
            if (string.IsNullOrEmpty(solutionRoot)) return fullPath;
            string root;
            try { root = Path.GetFullPath(solutionRoot).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return fullPath; }
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return ".";
            string prefix = root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
            return fullPath;
        }
    }
}
