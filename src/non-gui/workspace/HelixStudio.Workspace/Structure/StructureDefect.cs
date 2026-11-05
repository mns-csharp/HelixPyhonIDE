using System;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// The kind of disagreement between the Solution Explorer tree and the disk.
    ///
    /// The class is not a severity — it is the input to <see cref="StructureRepairPlanner"/>,
    /// which chooses a repair per class. That is why <see cref="Depth"/> and <see cref="Name"/>
    /// are separate even though one directory can exhibit both: they have two different repairs
    /// (move, then rename) that have to happen in that order.
    /// </summary>
    public enum StructureDefectClass
    {
        /// <summary>A node with no disk counterpart. The model is stale; the node is a lie.</summary>
        Phantom,

        /// <summary>A real, non-excluded disk path that the tree does not show at all.</summary>
        Orphan,

        /// <summary>The node's ancestor chain differs from the disk path's ancestor chain.</summary>
        Depth,

        /// <summary>Same location, different name.</summary>
        Name,

        /// <summary>Names differ only by case. Separate from <see cref="Name"/> because
        /// repairing it needs a two-step rename on a case-insensitive volume.</summary>
        Case,

        /// <summary>The tree says folder and the disk says file, or the reverse. Never
        /// auto-repaired — every automatic resolution either deletes a file or invents one.</summary>
        Type,
    }

    /// <summary>
    /// One disagreement between the tree and the disk.
    ///
    /// Paths are solution-relative and forward-slash normalised, so users can read them and tests
    /// can assert on them consistently on every platform.
    /// </summary>
    public sealed class StructureDefect
    {
        public StructureDefect(StructureDefectClass defectClass, string nodePath, string diskPath, string detail)
        {
            Class = defectClass;
            NodePath = nodePath;
            DiskPath = diskPath;
            Detail = detail ?? string.Empty;
        }

        /// <summary>Which disagreement this is. Determines the repair.</summary>
        public StructureDefectClass Class { get; }

        /// <summary>Solution-relative path the TREE claims. Null for <see cref="StructureDefectClass.Orphan"/>.</summary>
        public string NodePath { get; }

        /// <summary>Solution-relative path on DISK. Null for <see cref="StructureDefectClass.Phantom"/>.</summary>
        public string DiskPath { get; }

        /// <summary>Human-readable explanation, shown in the repair dialog and in test failures.</summary>
        public string Detail { get; }

        /// <summary>
        /// False only for <see cref="StructureDefectClass.Type"/>. A report containing one
        /// unrepairable defect can still be planned — the planner repairs what it can and refuses
        /// to guess at the rest, rather than refusing the whole solution.
        /// </summary>
        public bool IsRepairable
        {
            get { return Class != StructureDefectClass.Type; }
        }

        /// <summary>
        /// True only for the classes a repair fixes by MOVING something.
        ///
        /// The distinction matters at exactly one place — stage 6's re-verification — and getting
        /// it wrong makes the engine unable to ever succeed. <see cref="StructureDefectClass.Orphan"/>
        /// and <see cref="StructureDefectClass.Phantom"/> are repaired by the RENDERER (start
        /// showing this path; stop showing that node), so a model-based re-check still reports them
        /// after a perfectly correct disk repair. Treating them as outstanding work would roll back
        /// every successful run: the executor would move the packages, re-check, still see the
        /// solution's README with no node, and undo everything.
        /// </summary>
        public bool IsDiskRepairable
        {
            get
            {
                return Class == StructureDefectClass.Depth
                    || Class == StructureDefectClass.Name
                    || Class == StructureDefectClass.Case;
            }
        }

        public override string ToString()
        {
            string where;
            if (NodePath == null) where = "disk: " + DiskPath;
            else if (DiskPath == null) where = "tree: " + NodePath;
            else where = "tree: " + NodePath + "  disk: " + DiskPath;
            return Class.ToString().ToUpperInvariant() + " — " + Detail + " (" + where + ")";
        }
    }
}
