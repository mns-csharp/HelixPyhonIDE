using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Stage 3 — turns a <see cref="StructureParityReport"/> into an ordered, inspectable
    /// <see cref="StructureRepairPlan"/>. Pure: reads the filesystem only to check for collisions,
    /// and writes nothing.
    ///
    /// The ordering is not cosmetic; it is what makes execution safe:
    ///
    /// <list type="number">
    /// <item><b>Moves, deepest source first</b> — moving a parent before a queued child would
    /// invalidate the child's source path mid-plan.</item>
    /// <item><b>Renames after moves</b> — a Depth defect and a Name defect on one directory are two
    /// steps on the same folder (relocate it, then fix its name), so the rename must operate on the
    /// settled path.</item>
    /// <item><b>Manifest rewrites after both</b> — they record where things ended up.</item>
    /// <item><b>Empty-container deletion last</b>, and only for directories this plan emptied.</item>
    /// </list>
    ///
    /// <b>A plan that would overwrite an existing path is not produced.</b> It refuses and says so,
    /// exactly as <c>SolutionMembershipFixer</c> refuses on name collision rather than overwriting.
    /// Losing a user's directory to a structure-tidying feature is not a trade this engine gets to
    /// make.
    /// </summary>
    public sealed class StructureRepairPlanner
    {
        public static StructureRepairPlan PlanFor(StructureParityReport report)
        {
            return new StructureRepairPlanner().Plan(report);
        }

        public StructureRepairPlan Plan(StructureParityReport report)
        {
            var plan = new StructureRepairPlan(report == null ? null : report.SolutionDirectory);
            if (report == null || report.IsEmpty) return plan;
            string root = report.SolutionDirectory;

            var moves = new List<StructureRepairOperation>();
            var renames = new List<StructureRepairOperation>();

            // Where each directory has ended up so far, so a Name repair queued after a Depth
            // repair on the same folder starts from the moved path rather than the original one.
            var relocated = new Dictionary<string, string>(HelixPythonNamespace.Core.IO.PathComparison.Comparer);
            var emptiedContainers = new List<string>();

            foreach (StructureDefect defect in report.Defects)
            {
                switch (defect.Class)
                {
                    case StructureDefectClass.Depth:
                        PlanDepth(root, defect, plan, moves, relocated, emptiedContainers);
                        break;
                    case StructureDefectClass.Name:
                    case StructureDefectClass.Case:
                        PlanRename(root, defect, plan, renames, relocated);
                        break;
                    case StructureDefectClass.Orphan:
                        plan.Add(new StructureRepairOperation(StructureRepairKind.RenderNode,
                            defect.DiskPath, null, "show '" + defect.DiskPath + "' in the Solution Explorer"));
                        break;
                    case StructureDefectClass.Phantom:
                        plan.Add(new StructureRepairOperation(StructureRepairKind.DropNode,
                            defect.NodePath, null, "stop showing '" + defect.NodePath + "' — it is not on disk"));
                        break;
                    case StructureDefectClass.Type:
                        // Advisory, not blocking: a Type clash is not disk-repairable, so stage 6
                        // does not count it and the rest of the plan can still run.
                        plan.Refuse("'" + defect.DiskPath + "' is a file on disk and a folder in the tree (or the "
                            + "reverse). Every automatic resolution either deletes a file or invents one, so this "
                            + "one needs a human decision.", defect.IsDiskRepairable);
                        break;
                }
            }

            // Deepest source first — see the ordering note on the class.
            moves.Sort((a, b) => StructurePath.Segments(b.SourcePath).Length
                .CompareTo(StructurePath.Segments(a.SourcePath).Length));
            foreach (StructureRepairOperation op in moves) plan.Add(op);
            foreach (StructureRepairOperation op in renames) plan.Add(op);

            AddManifestRewrites(root, plan);

            foreach (string container in emptiedContainers.Distinct(HelixPythonNamespace.Core.IO.PathComparison.Comparer))
            {
                plan.Add(new StructureRepairOperation(StructureRepairKind.DeleteEmptyDirectory,
                    container, null, "remove '" + container + "' once it is empty"));
            }
            return plan;
        }

        private static void PlanDepth(
            string root, StructureDefect defect, StructureRepairPlan plan,
            List<StructureRepairOperation> moves, Dictionary<string, string> relocated,
            List<string> emptiedContainers)
        {
            if (IsSolutionRoot(defect.DiskPath)) { RefuseRootRelocation(defect, plan); return; }
            if (defect.DiskPath == null)
            {
                plan.Refuse("project '" + defect.NodePath + "' lives outside the solution directory. Moving a "
                    + "directory into the solution from elsewhere on disk is not something this engine will do "
                    + "unasked — add it as an existing project, or move it yourself.", defect.IsDiskRepairable);
                return;
            }

            // Relocate to the declared parent, keeping the folder's current name. Any name
            // difference is a separate defect with its own rename step, applied afterwards.
            string targetParent = StructurePath.Parent(defect.NodePath);
            string target = StructurePath.Combine(targetParent, StructurePath.Leaf(defect.DiskPath));

            if (StructurePath.AreSame(target, defect.DiskPath)) return;
            if (Exists(root, target))
            {
                plan.Refuse("cannot move '" + defect.DiskPath + "' to '" + target + "': something is already there.",
                    defect.IsDiskRepairable);
                return;
            }

            moves.Add(new StructureRepairOperation(StructureRepairKind.MoveDirectory,
                defect.DiskPath, target, "move '" + defect.DiskPath + "' up to '" + target + "'"));
            relocated[defect.NodePath] = target;

            string container = StructurePath.Parent(defect.DiskPath);
            if (container.Length > 0) emptiedContainers.Add(container);

            plan.AddProjectMove(new StructureProjectMove(
                StructurePath.Leaf(defect.NodePath), defect.DiskPath, StructurePath.Normalize(defect.NodePath)));
        }

        private static void PlanRename(
            string root, StructureDefect defect, StructureRepairPlan plan,
            List<StructureRepairOperation> renames, Dictionary<string, string> relocated)
        {
            if (IsSolutionRoot(defect.DiskPath)) { RefuseRootRelocation(defect, plan); return; }
            if (defect.DiskPath == null)
            {
                plan.Refuse("cannot rename for '" + defect.NodePath + "': " + defect.Detail, defect.IsDiskRepairable);
                return;
            }

            // Start from wherever a Depth repair has already put this folder.
            string source;
            if (!relocated.TryGetValue(defect.NodePath, out source)) source = defect.DiskPath;

            string target = StructurePath.Normalize(defect.NodePath);
            bool caseOnly = defect.Class == StructureDefectClass.Case;

            // Ordinal, for the same reason StructureParityChecker.ClassifyAnchor is ordinal: a
            // case-only rename's source and target ARE the same path under Windows' identity
            // rules, so the identity comparison would silently drop the only repair that fixes it.
            if (string.Equals(source, target, StringComparison.Ordinal)) return;
            if (!caseOnly && StructurePath.AreSame(source, target)) return;
            // A case-only rename "collides" with itself on a case-insensitive volume — that is the
            // whole reason it needs the two-step form, not a reason to refuse it.
            if (!caseOnly && Exists(root, target))
            {
                plan.Refuse("cannot rename '" + source + "' to '" + target + "': something is already there.",
                    defect.IsDiskRepairable);
                return;
            }

            renames.Add(new StructureRepairOperation(
                caseOnly ? StructureRepairKind.RecaseDirectory : StructureRepairKind.RenameDirectory,
                source, target, "rename '" + source + "' to '" + target + "'"));
            relocated[defect.NodePath] = target;

            plan.AddProjectMove(new StructureProjectMove(StructurePath.Leaf(target), defect.DiskPath, target));
        }

        /// <summary>
        /// Queues the manifest updates a relocation makes mandatory, and names the ones this engine
        /// will not guess at.
        ///
        /// Moving <c>src/bugmap</c> to <c>bugmap</c> is five lines of file I/O and a long tail of
        /// references. Skipping the tail turns a cosmetic mismatch into a broken build — and in the
        /// <c>pytest.ini</c> case, into a green test run that collected nothing.
        /// </summary>
        private static void AddManifestRewrites(string root, StructureRepairPlan plan)
        {
            if (plan.ProjectMoves.Count == 0) return;

            AddIfPresent(root, plan, "pyproject.toml",
                "update [tool.uv.workspace] members and [tool.setuptools.packages.find] to the new layout");
            AddIfPresent(root, plan, "pytest.ini",
                "update testpaths / pythonpath to the new layout");
            AddIfPresent(root, plan, "setup.cfg",
                "update package discovery paths to the new layout");

            plan.AddFollowUp("[project.scripts] entry points resolve through IMPORT paths, so a move leaves them "
                + "working but a RENAME does not — check them if a project was renamed.");
            plan.AddFollowUp("Any *.csooptool-layout.json stores an ABSOLUTE solutionFilePath and per-node keys; "
                + "canvas layout may reset.");
            plan.AddFollowUp("Breakpoints and recent-file entries store paths and will detach from moved files.");
        }

        private static void AddIfPresent(string root, StructureRepairPlan plan, string relativeFile, string what)
        {
            if (!Exists(root, relativeFile)) return;
            plan.Add(new StructureRepairOperation(StructureRepairKind.RewriteManifest,
                relativeFile, null, what));
        }

        /// <summary>
        /// The solution directory itself, which in this canonical form is the empty relative path.
        ///
        /// A backstop, not a code path anyone should reach: the only defect that ever named it was
        /// the bogus Name defect a root-anchored member used to produce, and
        /// <see cref="ProjectNodePath"/> stops that at the source. It stays because of what the
        /// executor would otherwise be handed — <c>Directory.Move(solutionRoot,
        /// solutionRoot/&lt;name&gt;)</c>, a move of the user's whole solution into a subdirectory
        /// of itself. Windows refuses it and the unwind rolls back, but a repair engine must not
        /// depend on the filesystem to catch a plan it should never have emitted.
        /// </summary>
        private static bool IsSolutionRoot(string relativePath)
        {
            return relativePath != null && StructurePath.Normalize(relativePath).Length == 0;
        }

        private static void RefuseRootRelocation(StructureDefect defect, StructureRepairPlan plan)
        {
            plan.Refuse("project '" + defect.NodePath + "' is the solution's own root — its node stands for the "
                + "solution directory, which this engine will never move or rename. " + defect.Detail,
                defect.IsDiskRepairable);
        }

        private static bool Exists(string root, string relativePath)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relativePath)) return false;
            string absolute = StructurePath.ToAbsolute(root, relativePath);
            return Directory.Exists(absolute) || File.Exists(absolute);
        }
    }
}
