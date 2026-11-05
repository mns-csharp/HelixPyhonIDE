using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Rename a Python package (a directory that contains <c>__init__.py</c>).
    /// Moves the directory on disk, rewrites every <c>from &lt;oldpkg&gt;… import …</c>
    /// across the solution, and updates the workspace-root
    /// <c>pyproject.toml</c>'s <c>[tool.uv.workspace].members</c> list if the
    /// renamed directory sits directly under the workspace root.
    /// </summary>
    public sealed class PackageRenameService
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public struct ComputedPackageRename
        {
            public List<FileTextEdit> Edits;          // import-path rewrites across .py files
            public FileTextEdit? TomlEdit;            // optional edit to pyproject.toml
            public DirectoryMove DirectoryMove;
            public string OldDottedName;
            public string NewDottedName;
        }

        public struct DirectoryMove
        {
            public string OldPath;
            public string NewPath;
            public DirectoryMove(string oldPath, string newPath) { OldPath = oldPath; NewPath = newPath; }
        }

        /// <summary>
        /// Pre-compute all edits without touching disk. The caller is responsible for
        /// performing the actual <c>Directory.Move</c> and applying <c>Edits</c> (ideally
        /// through an undoable command).
        /// </summary>
        public ComputedPackageRename Compute(
            SolutionModel solution,
            string packageDirectory,
            string newPackageName,
            string workspaceRoot)
        {
            ComputedPackageRename result = default;
            result.Edits = new List<FileTextEdit>();
            if (solution == null || string.IsNullOrEmpty(packageDirectory)
                || string.IsNullOrWhiteSpace(newPackageName))
                return result;

            packageDirectory = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetDirectoryName(packageDirectory);
            string oldName = Path.GetFileName(packageDirectory);
            if (string.Equals(oldName, newPackageName, StringComparison.Ordinal)) return result;

            string newDir = Path.Combine(parent ?? string.Empty, newPackageName);
            result.DirectoryMove = new DirectoryMove(packageDirectory, newDir);

            // Derive old & new dotted namespaces using the same rules the importer uses.
            string oldDotted = DeriveDotted(solution, packageDirectory);
            string newDotted = ReplaceLastSegment(oldDotted, newPackageName);
            result.OldDottedName = oldDotted;
            result.NewDottedName = newDotted;

            // Walk every .py file in the solution and compute import rewrites.
            if (!string.IsNullOrEmpty(oldDotted) && !string.IsNullOrEmpty(newDotted) && oldDotted != newDotted)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ProjectModel p in solution.Projects)
                {
                    if (p == null || string.IsNullOrEmpty(p.ProjectDirectory) || !Directory.Exists(p.ProjectDirectory)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(p.ProjectDirectory, "*.py", SearchOption.AllDirectories); }
                    catch { continue; }
                    foreach (string f in files)
                    {
                        string full = Path.GetFullPath(f);
                        if (!seen.Add(full)) continue;
                        string src;
                        try { src = File.ReadAllText(full); }
                        catch { continue; }
                        var r = PythonIdentifierRewriter.RewriteModuleImports(src, oldDotted, newDotted);
                        if (r.Count == 0 || r.Text == src) continue;
                        result.Edits.Add(new FileTextEdit(full, src, r.Text));
                    }
                }
            }

            // Workspace toml: swap the old segment for the new one in members = [...]
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                string tomlPath = Path.Combine(workspaceRoot, "pyproject.toml");
                if (File.Exists(tomlPath))
                {
                    // Only update when the renamed directory is a direct child of the workspace
                    // root (because that's what ends up in `members = [...]`).
                    string parentFull = Path.GetFullPath(parent ?? string.Empty);
                    string rootFull = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(parentFull.TrimEnd(Path.DirectorySeparatorChar), rootFull,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string toml;
                        try { toml = File.ReadAllText(tomlPath); }
                        catch { toml = null; }
                        if (!string.IsNullOrEmpty(toml))
                        {
                            string updated = UpdateMembersLine(toml, oldName, newPackageName);
                            if (updated != null && updated != toml)
                            {
                                result.TomlEdit = new FileTextEdit(tomlPath, toml, updated);
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Apply a <see cref="ComputedPackageRename"/> in place: perform the directory
        /// move first (so subsequent reads/writes hit the new tree), then the text
        /// edits. Returns a compact result for status reporting.
        /// </summary>
        public (int filesChanged, int importHits) Apply(ComputedPackageRename c)
        {
            var mv = c.DirectoryMove;
            try
            {
                if (!string.IsNullOrEmpty(mv.OldPath) && !string.IsNullOrEmpty(mv.NewPath) &&
                    !string.Equals(mv.OldPath, mv.NewPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(mv.OldPath) && !Directory.Exists(mv.NewPath))
                        Directory.Move(mv.OldPath, mv.NewPath);
                }
            }
            catch { /* best-effort */ }

            int hits = 0;
            foreach (FileTextEdit e in c.Edits)
            {
                // Rewrite path: if the edit's path was under the old directory, rewrite to new.
                string target = RebasePath(e.Path, mv.OldPath, mv.NewPath);
                try { File.WriteAllText(target, e.NewText, Utf8NoBom); hits += 1; }
                catch { }
            }
            if (c.TomlEdit.HasValue)
            {
                var t = c.TomlEdit.Value;
                try { File.WriteAllText(t.Path, t.NewText, Utf8NoBom); }
                catch { }
            }

            return (c.Edits.Count, hits);
        }

        // ---- helpers ---------------------------------------------------------

        private static string RebasePath(string originalPath, string oldDir, string newDir)
        {
            if (string.IsNullOrEmpty(oldDir) || string.IsNullOrEmpty(newDir)) return originalPath;
            string fullOrig = Path.GetFullPath(originalPath);
            string fullOld = Path.GetFullPath(oldDir).TrimEnd(Path.DirectorySeparatorChar);
            if (fullOrig.StartsWith(fullOld + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(newDir).TrimEnd(Path.DirectorySeparatorChar)
                    + fullOrig.Substring(fullOld.Length);
            }
            return originalPath;
        }

        private static string DeriveDotted(SolutionModel solution, string packageDirectory)
        {
            // Use the existing SymbolResolver namespace logic (via __init__.py path) when
            // a solution is available; otherwise fall back to "just the dir name".
            if (solution != null)
            {
                try
                {
                    string initPath = Path.Combine(packageDirectory, "__init__.py");
                    var resolver = new SymbolResolver(solution);
                    string ns = resolver.DeriveModuleNamespace(initPath);
                    if (!string.IsNullOrEmpty(ns)) return ns;
                }
                catch { }
            }
            return Path.GetFileName(packageDirectory);
        }

        private static string ReplaceLastSegment(string dotted, string newSegment)
        {
            if (string.IsNullOrEmpty(dotted)) return newSegment;
            int lastDot = dotted.LastIndexOf('.');
            if (lastDot < 0) return newSegment;
            return dotted.Substring(0, lastDot + 1) + newSegment;
        }

        /// <summary>
        /// Surgical replace of a single segment in a single-line <c>members = [...]</c>
        /// array. Returns the updated toml text, or <c>null</c> if the members array
        /// isn't present in single-line form (safe fallback — no mangling).
        /// </summary>
        private static string UpdateMembersLine(string toml, string oldSegment, string newSegment)
        {
            Match m = Regex.Match(toml,
                @"(?m)^\s*members\s*=\s*\[(?<body>[^\]\n]*)\]\s*$");
            if (!m.Success) return null;

            string body = m.Groups["body"].Value;

            // Try to locate the exact "oldSegment" in quotes (possibly with a path prefix).
            // Safe match: quoted segment equal to oldSegment, or ending in "/oldSegment".
            string pattern = "\"(?<pre>[^\"]*?)" + Regex.Escape(oldSegment) + "\"";
            bool changed = false;
            string newBody = Regex.Replace(body, pattern, mm =>
            {
                string pre = mm.Groups["pre"].Value;
                // Accept when either "oldSegment" is the entire value, or it's the final
                // path segment ("…/oldSegment").
                if (pre.Length == 0 ||
                    pre.EndsWith("/", StringComparison.Ordinal) ||
                    pre.EndsWith("\\", StringComparison.Ordinal))
                {
                    changed = true;
                    return "\"" + pre + newSegment + "\"";
                }
                return mm.Value;
            });

            if (!changed) return null;

            return toml.Substring(0, m.Index) + "members = [" + newBody + "]"
                + toml.Substring(m.Index + m.Length);
        }
    }
}
