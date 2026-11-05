using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Stage 2 — compares the projected tree spine against the disk and produces the report that
    /// is the oracle for the whole feature (PY8004).
    ///
    /// The comparison is PROJECT-ANCHORED rather than a flat set difference, and that choice is
    /// what makes the output usable. A naive diff of two path sets reports a member sitting in
    /// <c>src/bugmap</c> as: one orphan directory (<c>src</c>), one orphan directory
    /// (<c>src/bugmap</c>), one phantom directory (<c>bugmap</c>), and one orphan + one phantom for
    /// every single file in the package. That is hundreds of rows describing ONE problem — the
    /// package is one level deeper than the tree claims — and it names no repair. Anchoring on the
    /// projects first, then rebasing each project's files through the anchor, yields exactly one
    /// defect per real disagreement, each with a repair attached.
    /// </summary>
    public sealed class StructureParityChecker
    {
        /// <summary>Scan, project, and diff. The one call the load path needs.</summary>
        public static StructureParityReport Check(SolutionModel solution)
        {
            return new StructureParityChecker().Compare(solution, CancellationToken.None);
        }

        public StructureParityReport Compare(SolutionModel solution, CancellationToken cancel)
        {
            string root = solution == null ? null : StructurePath.Canonical(solution.SolutionDirectory);
            var report = new StructureParityReport(root);
            if (solution == null || string.IsNullOrEmpty(root)) return report;

            List<ProjectAnchor> anchors = BuildAnchors(solution, report);

            var scanner = new DiskLayoutScanner { ExcludedRelativePaths = ExcludedPaths(anchors) };
            StructureLayout disk = scanner.Scan(root, cancel);
            // A walk that stopped early cannot yield a clean verdict — see ScanCompleted.
            report.ScanCompleted = !scanner.WasTruncated;
            StructureLayout tree = new TreeLayoutProjector().Project(solution);

            CompareEntries(disk, tree, anchors, report, cancel);
            return report;
        }

        // ---- project anchoring -------------------------------------------------------------

        /// <summary>
        /// Where a project's node claims to be (<see cref="Declared"/>, a single segment
        /// directly under the solution root, or <c>""</c> for a member rooted at the solution
        /// directory) versus where its directory actually is (<see cref="Actual"/>).
        /// </summary>
        private sealed class ProjectAnchor
        {
            public ProjectModel Project;
            public string Declared;    // "" when the member IS the solution root — ProjectNodePath
            public string Actual;      // null when the directory is not inside the solution at all
        }

        private static List<ProjectAnchor> BuildAnchors(SolutionModel solution, StructureParityReport report)
        {
            string root = StructurePath.Canonical(solution.SolutionDirectory);
            var anchors = new List<ProjectAnchor>();
            var declaredSeen = new Dictionary<string, ProjectModel>(HelixPythonNamespace.Core.IO.PathComparison.Comparer);

            foreach (ProjectModel project in solution.Projects)
            {
                if (project == null || string.IsNullOrEmpty(project.Name)) continue;
                var anchor = new ProjectAnchor
                {
                    Project = project,
                    Declared = ProjectNodePath.Declared(project, root),
                    Actual = StructurePath.Relative(root, project.ProjectDirectory),
                };
                anchors.Add(anchor);

                // Two projects drawn at one path is a tree that cannot be right whatever the disk
                // says, so it is reported before any disk comparison and it blocks repair: the
                // planner would otherwise move two directories onto each other.
                ProjectModel clash;
                if (declaredSeen.TryGetValue(anchor.Declared, out clash))
                {
                    report.Add(new StructureDefect(StructureDefectClass.Name, anchor.Declared, anchor.Actual,
                        "two projects claim the same tree node: '" + clash.Name + "' and '" + project.Name + "'"));
                }
                else declaredSeen[anchor.Declared] = project;

                ClassifyAnchor(anchor, report);
            }

            // Longest actual path first, so a project nested inside another is matched by the
            // deepest owner — the same longest-prefix rule SolutionPolicy uses for rule 3.
            anchors.Sort((a, b) => (b.Actual ?? string.Empty).Length.CompareTo((a.Actual ?? string.Empty).Length));
            return anchors;
        }

        private static void ClassifyAnchor(ProjectAnchor anchor, StructureParityReport report)
        {
            if (anchor.Actual == null)
            {
                report.Add(new StructureDefect(StructureDefectClass.Depth, anchor.Declared, null,
                    "project directory is outside the solution directory: " + anchor.Project.ProjectDirectory));
                return;
            }
            // ORDINAL, deliberately — not PathComparison's identity comparison.
            //
            // This is the one place in the engine that must SEE a case difference. On Windows
            // PathComparison.Comparison is OrdinalIgnoreCase, so `Skew_Tests` and `skew_tests`
            // compare equal and an early return here makes the Case defect class permanently
            // unreachable. Identity ("is this the same directory?") is the wrong question at this
            // line; the question is "is the tree's label spelled the way the folder is spelled?",
            // which is a convention question and is answered character by character.
            if (string.Equals(anchor.Declared, anchor.Actual, StringComparison.Ordinal)) return;

            // Depth and name are judged independently, and a directory can exhibit both (a project
            // named `bugmap_examples` living in `src/examples`). They are separate defects because
            // they have separate repairs, applied in this order: move, then rename.
            //
            // The PARENT comparison stays case-insensitive: a container spelled differently is not
            // a reason to claim the package sits at the wrong depth.
            string declaredParent = StructurePath.Parent(anchor.Declared);
            string actualParent = StructurePath.Parent(anchor.Actual);
            if (!StructurePath.EqualsIgnoringCase(declaredParent, actualParent))
            {
                report.Add(new StructureDefect(StructureDefectClass.Depth, anchor.Declared, anchor.Actual,
                    "project '" + anchor.Project.Name + "' is drawn directly under the solution but lives in '"
                    + (actualParent.Length == 0 ? "(solution root)" : actualParent) + "'"));
            }

            string declaredLeaf = StructurePath.Leaf(anchor.Declared);
            string actualLeaf = StructurePath.Leaf(anchor.Actual);
            if (string.Equals(declaredLeaf, actualLeaf, StringComparison.Ordinal)) return;

            if (StructurePath.EqualsIgnoringCase(declaredLeaf, actualLeaf))
            {
                report.Add(new StructureDefect(StructureDefectClass.Case, anchor.Declared, anchor.Actual,
                    "folder '" + actualLeaf + "' differs from the project name '" + declaredLeaf + "' only by case"));
            }
            else
            {
                report.Add(new StructureDefect(StructureDefectClass.Name, anchor.Declared, anchor.Actual,
                    "project is named '" + declaredLeaf + "' but its folder is named '" + actualLeaf + "'"));
            }
        }

        private static List<string> ExcludedPaths(List<ProjectAnchor> anchors)
        {
            var excluded = new List<string>();
            foreach (ProjectAnchor anchor in anchors)
            {
                if (anchor.Actual == null || anchor.Project.ExcludedPaths == null) continue;
                foreach (string relative in anchor.Project.ExcludedPaths)
                {
                    if (string.IsNullOrEmpty(relative)) continue;
                    excluded.Add(StructurePath.Combine(anchor.Actual, relative));
                }
            }
            return excluded;
        }

        // ---- entry comparison --------------------------------------------------------------

        private static void CompareEntries(
            StructureLayout disk, StructureLayout tree,
            List<ProjectAnchor> anchors, StructureParityReport report, CancellationToken cancel)
        {
            foreach (string diskDirectory in disk.Directories)
            {
                cancel.ThrowIfCancellationRequested();
                if (IsAnchorAncestor(diskDirectory, anchors)) continue;
                string expected = DiskToNode(diskDirectory, anchors);
                if (tree.HasDirectory(expected)) continue;
                if (tree.HasFile(expected))
                {
                    report.Add(new StructureDefect(StructureDefectClass.Type, expected, diskDirectory,
                        "the tree shows a file where the disk has a directory"));
                    continue;
                }
                report.Add(new StructureDefect(StructureDefectClass.Orphan, null, diskDirectory,
                    "directory exists on disk but the tree shows no node for it"));
            }

            foreach (string diskFile in disk.Files)
            {
                cancel.ThrowIfCancellationRequested();
                string expected = DiskToNode(diskFile, anchors);
                if (tree.HasFile(expected)) continue;
                if (tree.HasDirectory(expected))
                {
                    report.Add(new StructureDefect(StructureDefectClass.Type, expected, diskFile,
                        "the tree shows a directory where the disk has a file"));
                    continue;
                }
                report.Add(new StructureDefect(StructureDefectClass.Orphan, null, diskFile,
                    "file exists on disk but the tree shows no node for it"));
            }

            foreach (string treeDirectory in tree.Directories)
            {
                cancel.ThrowIfCancellationRequested();
                string onDisk = NodeToDisk(treeDirectory, anchors);
                if (disk.HasDirectory(onDisk)) continue;
                // A Type clash was already reported from the disk side; reporting it again from
                // the tree side would blame one disagreement twice.
                if (disk.HasFile(onDisk)) continue;
                report.Add(new StructureDefect(StructureDefectClass.Phantom, treeDirectory, null,
                    "the tree shows a folder that does not exist on disk"));
            }

            foreach (string treeFile in tree.Files)
            {
                cancel.ThrowIfCancellationRequested();
                string onDisk = NodeToDisk(treeFile, anchors);
                if (disk.HasAnything(onDisk)) continue;
                report.Add(new StructureDefect(StructureDefectClass.Phantom, treeFile, null,
                    "the tree shows a file that does not exist on disk"));
            }
        }

        /// <summary>
        /// True for a directory that exists on disk ONLY because it contains a project whose
        /// anchor is already reported as a Depth defect — <c>src/</c> in the motivating case.
        ///
        /// Without this, the invisible container is reported twice: once as the Depth defect that
        /// names the real problem and offers a repair, and once as a bare Orphan that names the
        /// same directory and offers a different one. Two rows, two instructions, one problem.
        /// </summary>
        private static bool IsAnchorAncestor(string diskDirectory, List<ProjectAnchor> anchors)
        {
            foreach (ProjectAnchor anchor in anchors)
            {
                if (anchor.Actual == null) continue;
                if (StructurePath.AreSame(anchor.Actual, diskDirectory)) return false; // it IS a project
                if (StructurePath.IsUnder(anchor.Actual, diskDirectory)
                    && !StructurePath.AreSame(anchor.Declared, anchor.Actual))
                    return true;
            }
            return false;
        }

        /// <summary>Rebase a disk path onto the node path the tree would draw for it.</summary>
        private static string DiskToNode(string diskPath, List<ProjectAnchor> anchors)
        {
            foreach (ProjectAnchor anchor in anchors) // longest actual first
            {
                if (anchor.Actual == null) continue;
                if (StructurePath.AreSame(diskPath, anchor.Actual)) return anchor.Declared;
                if (StructurePath.IsUnder(diskPath, anchor.Actual))
                {
                    string remainder = StructurePath.Remainder(diskPath, anchor.Actual);
                    return StructurePath.Combine(anchor.Declared, remainder);
                }
            }
            return diskPath;
        }

        /// <summary>The inverse of <see cref="DiskToNode"/>: where a node path should be on disk.</summary>
        private static string NodeToDisk(string nodePath, List<ProjectAnchor> anchors)
        {
            ProjectAnchor best = null;
            foreach (ProjectAnchor anchor in anchors)
            {
                if (anchor.Actual == null) continue;
                bool matches = StructurePath.AreSame(nodePath, anchor.Declared)
                    || StructurePath.IsUnder(nodePath, anchor.Declared);
                if (!matches) continue;
                if (best == null || anchor.Declared.Length > best.Declared.Length) best = anchor;
            }
            if (best == null) return nodePath;
            if (StructurePath.AreSame(nodePath, best.Declared)) return best.Actual;
            string remainder = StructurePath.Remainder(nodePath, best.Declared);
            return StructurePath.Combine(best.Actual, remainder);
        }
    }
}
