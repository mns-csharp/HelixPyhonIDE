using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HelixPythonNamespace.VirtualCompiler.Compliance;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Stage 1 — walks the solution directory and records what is actually there.
    ///
    /// <b>Strictly read-only.</b> Nothing in the parity engine touches disk before the user has
    /// answered the repair dialog, and this is the class where that boundary is easiest to violate
    /// by accident. It is the same read-only boundary IMP-02 draws before the Import Engine
    /// generates anything, for the same reason.
    ///
    /// Exclusions come from <see cref="ThirdPartyDetector"/> — the SAME detector the three
    /// compliance rules use, deliberately, so a <c>.venv</c> that PY8001 ignores is not reported as
    /// an orphan here. Sharing it also inherits its generosity argument: over-excluding leaves a
    /// directory unpoliced, under-excluding fills the user's repair dialog with vendored files they
    /// cannot restructure.
    /// </summary>
    public sealed class DiskLayoutScanner
    {
        /// <summary>
        /// Paths that are IDE bookkeeping rather than the user's structure. These are excluded on
        /// top of <see cref="ThirdPartyDetector"/>'s list because they are ours, not third-party:
        /// showing the user a tree node for the journal this very engine writes would be absurd.
        /// </summary>
        private static readonly string[] IdeSegments = { ".helixstudio", ".vs", ".idea", ".vscode" };

        /// <summary>
        /// Additional solution-relative paths to treat as invisible — the projects'
        /// <c>[tool.helixstudio] excluded</c> entries, which the loader already skips when
        /// populating the tree. Without them every excluded file would report as an Orphan, i.e.
        /// the engine would fight a feature.
        /// </summary>
        public IList<string> ExcludedRelativePaths { get; set; }

        public StructureLayout Scan(string solutionDirectory)
        {
            return Scan(solutionDirectory, CancellationToken.None);
        }

        /// <summary>
        /// How deep the walk will go before it stops descending.
        ///
        /// A backstop, not a policy: no real solution nests this far, and the cost of being wrong
        /// in the other direction is that the scan recurses until the stack is gone. A
        /// <c>StackOverflowException</c> cannot be caught on .NET Framework — the process dies —
        /// and this scan runs while the user is opening a solution, so the failure mode is "the
        /// IDE vanished when I opened my project".
        /// </summary>
        private const int MaximumDepth = 64;

        /// <summary>
        /// True when the walk stopped early. The caller must treat a truncated layout as
        /// "not verified" rather than as fact — every unwalked path would otherwise look like a
        /// directory the tree correctly omits, i.e. the scan would manufacture a clean report out
        /// of its own failure.
        /// </summary>
        public bool WasTruncated { get; private set; }

        public StructureLayout Scan(string solutionDirectory, CancellationToken cancel)
        {
            WasTruncated = false;
            var layout = new StructureLayout();
            string root = StructurePath.Canonical(solutionDirectory);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return layout;
            Walk(root, root, layout, cancel, 0);
            return layout;
        }

        /// <summary>
        /// A directory that is a reparse point — a junction, a symbolic link, a mount point.
        ///
        /// Never descended into, for two independent reasons. The fatal one: a junction pointing at
        /// any of its own ancestors is an infinite path, and the walk below would recurse until the
        /// process died. The merely bad one: a junction pointing somewhere large and legitimate
        /// makes the scan enumerate a tree that is not the solution and report every path in it as
        /// an Orphan the user cannot act on. A workspace may itself be reached through a junction,
        /// so this case occurs in practice.
        ///
        /// The link's own entry is still RECORDED — it is a real thing at a real path that the tree
        /// should show — it is only not walked through.
        /// </summary>
        private static bool IsReparsePoint(string directory)
        {
            try
            {
                var attributes = File.GetAttributes(directory);
                return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch (UnauthorizedAccessException) { return true; }   // cannot inspect it: do not enter it
            catch (IOException) { return true; }
        }

        private void Walk(string root, string directory, StructureLayout layout, CancellationToken cancel, int depth)
        {
            cancel.ThrowIfCancellationRequested();

            if (depth > MaximumDepth) { WasTruncated = true; return; }

            string[] subdirectories;
            string[] files;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch (UnauthorizedAccessException) { WasTruncated = true; return; }
            catch (IOException) { WasTruncated = true; return; }

            foreach (string file in files)
            {
                string rel = StructurePath.Relative(root, file);
                if (rel == null || IsExcluded(rel)) continue;
                layout.AddFile(rel);
            }

            foreach (string sub in subdirectories)
            {
                string rel = StructurePath.Relative(root, sub);
                if (rel == null || IsExcluded(rel)) continue;
                // Recorded even when empty: an empty directory is still a directory the user can
                // see in Explorer, and "the tree does not show it" is exactly the complaint.
                layout.AddDirectory(rel);
                if (IsReparsePoint(sub)) continue;
                Walk(root, sub, layout, cancel, depth + 1);
            }
        }

        private bool IsExcluded(string relativePath)
        {
            if (ThirdPartyDetector.IsThirdParty(relativePath)) return true;

            foreach (string segment in StructurePath.Segments(relativePath))
            {
                foreach (string ide in IdeSegments)
                {
                    // Convention question, so case-insensitive on every platform — same rule as
                    // ThirdPartyDetector's own segment matching.
                    if (string.Equals(segment, ide, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            if (ExcludedRelativePaths != null)
            {
                foreach (string excluded in ExcludedRelativePaths)
                {
                    if (string.IsNullOrEmpty(excluded)) continue;
                    if (StructurePath.AreSame(relativePath, excluded)) return true;
                    if (StructurePath.IsUnder(relativePath, excluded)) return true;
                }
            }
            return false;
        }
    }
}
