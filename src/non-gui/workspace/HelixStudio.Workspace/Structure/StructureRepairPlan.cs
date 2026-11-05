using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>What a single repair step does.</summary>
    public enum StructureRepairKind
    {
        /// <summary>Move a directory to a new parent, keeping its name. Repairs a Depth defect.</summary>
        MoveDirectory,

        /// <summary>Rename a directory in place. Repairs a Name defect.</summary>
        RenameDirectory,

        /// <summary>
        /// Rename differing only by case. Separate from <see cref="RenameDirectory"/> because on a
        /// case-insensitive volume it needs a two-step rename through a temporary name — a single
        /// <c>Directory.Move</c> from <c>Foo</c> to <c>foo</c> is a no-op or an error.
        /// </summary>
        RecaseDirectory,

        /// <summary>Update a manifest that referenced a path this plan moved.</summary>
        RewriteManifest,

        /// <summary>Remove a container this plan itself emptied (the <c>src/</c> of the motivating case).</summary>
        DeleteEmptyDirectory,

        /// <summary>Tree-side only: start showing a path that was always on disk. Touches nothing.</summary>
        RenderNode,

        /// <summary>Tree-side only: stop showing a node that has no disk counterpart. Touches nothing.</summary>
        DropNode,
    }

    /// <summary>One step of a repair. Paths are canonical solution-relative.</summary>
    public sealed class StructureRepairOperation
    {
        public StructureRepairOperation(StructureRepairKind kind, string sourcePath, string targetPath, string description)
        {
            Kind = kind;
            SourcePath = sourcePath;
            TargetPath = targetPath;
            Description = description ?? string.Empty;
        }

        public StructureRepairKind Kind { get; }
        public string SourcePath { get; }
        public string TargetPath { get; }
        public string Description { get; }

        /// <summary>
        /// False for the tree-side kinds. The dialog leans on this: a plan of 40 RenderNodes and no
        /// disk operations must not be presented with the same weight as one that moves the user's
        /// packages around.
        /// </summary>
        public bool TouchesDisk
        {
            get { return Kind != StructureRepairKind.RenderNode && Kind != StructureRepairKind.DropNode; }
        }

        public override string ToString()
        {
            if (TargetPath == null) return Kind + ": " + SourcePath;
            return Kind + ": " + SourcePath + " -> " + TargetPath;
        }
    }

    /// <summary>Where a project directory was, and where the plan puts it.</summary>
    public sealed class StructureProjectMove
    {
        public StructureProjectMove(string projectName, string oldRelativePath, string newRelativePath)
        {
            ProjectName = projectName;
            OldRelativePath = oldRelativePath;
            NewRelativePath = newRelativePath;
        }

        public string ProjectName { get; }
        public string OldRelativePath { get; }
        public string NewRelativePath { get; }
    }

    /// <summary>
    /// Stage 3's output: the ordered, inspectable list of everything the repair will do.
    ///
    /// This object — not the two-sentence warning — is what the confirmation dialog must be able to
    /// show. A modal that moves an unknown number of a user's files on a two-word summary is not a
    /// confirmation, it is a dare.
    /// </summary>
    public sealed class StructureRepairPlan
    {
        private readonly List<StructureRepairOperation> _operations = new List<StructureRepairOperation>();
        private readonly List<string> _refusals = new List<string>();
        private readonly List<string> _blockingRefusals = new List<string>();
        private readonly List<string> _manualFollowUps = new List<string>();
        private readonly List<StructureProjectMove> _projectMoves = new List<StructureProjectMove>();

        public StructureRepairPlan(string solutionDirectory)
        {
            SolutionDirectory = solutionDirectory;
        }

        public string SolutionDirectory { get; }

        public IReadOnlyList<StructureRepairOperation> Operations { get { return _operations; } }

        /// <summary>
        /// Defects this plan deliberately will NOT repair, each with the reason. Never empty
        /// silently: a plan that quietly skipped a defect would report success while leaving the
        /// solution mismatched, and the re-verify in stage 6 would then roll back a repair that was
        /// actually fine.
        /// </summary>
        public IReadOnlyList<string> Refusals { get { return _refusals; } }

        /// <summary>
        /// Things the user has to do by hand afterwards, because guessing at them would be worse
        /// than naming them. Surfaced in the dialog, not buried.
        /// </summary>
        public IReadOnlyList<string> ManualFollowUps { get { return _manualFollowUps; } }

        /// <summary>Old → new directory for every project the plan relocates. Drives manifest rewriting.</summary>
        public IReadOnlyList<StructureProjectMove> ProjectMoves { get { return _projectMoves; } }

        public bool IsEmpty { get { return _operations.Count == 0; } }

        /// <summary>True when at least one operation actually writes to disk.</summary>
        public bool TouchesDisk { get { return _operations.Any(o => o.TouchesDisk); } }

        /// <summary>
        /// False when a refusal means executing would leave the solution still mismatched. The
        /// caller should show the report rather than offering a repair that cannot finish the job.
        /// </summary>
        public bool CanFullyRepair { get { return _refusals.Count == 0; } }

        /// <summary>
        /// True when a refusal leaves a defect that only a DISK repair could have cleared.
        ///
        /// Such a plan must never be executed, and the reason is arithmetic rather than taste:
        /// stage 6 rolls back unless every disk-repairable defect is gone, so a plan that has
        /// already refused one of them is guaranteed to move the user's directories and then move
        /// them all back. The user watches a progress cursor, waits out the I/O, and is told the
        /// repair failed — for a collision the planner had detected before touching anything.
        ///
        /// A Type refusal is deliberately NOT blocking: a file-vs-folder clash is not disk-
        /// repairable, stage 6 does not count it, and refusing the whole repair over one would
        /// strand every other defect in the solution behind a defect nothing can fix.
        /// </summary>
        public bool HasBlockingRefusal { get { return _blockingRefusals.Count > 0; } }

        /// <summary>The refusals that stop execution, without the advisory ones.</summary>
        public IReadOnlyList<string> BlockingRefusals { get { return _blockingRefusals; } }

        internal void Add(StructureRepairOperation operation) { _operations.Add(operation); }

        internal void Refuse(string reason, bool blocksExecution)
        {
            _refusals.Add(reason);
            if (blocksExecution) _blockingRefusals.Add(reason);
        }
        internal void AddFollowUp(string note) { if (!_manualFollowUps.Contains(note)) _manualFollowUps.Add(note); }
        internal void AddProjectMove(StructureProjectMove move) { _projectMoves.Add(move); }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("Repair plan for ").AppendLine(SolutionDirectory);
            if (IsEmpty) sb.AppendLine("  (nothing to do)");
            foreach (StructureRepairOperation op in _operations)
                sb.Append("  ").AppendLine(op.ToString());
            if (_refusals.Count > 0)
            {
                sb.AppendLine("  Not repaired:");
                foreach (string r in _refusals) sb.Append("    ").AppendLine(r);
            }
            if (HasBlockingRefusal)
                sb.AppendLine("  This plan will NOT be executed: a refusal above leaves a defect only a "
                    + "disk repair could clear, so running it would move directories and roll them back.");
            if (_manualFollowUps.Count > 0)
            {
                sb.AppendLine("  Check by hand afterwards:");
                foreach (string f in _manualFollowUps) sb.Append("    ").AppendLine(f);
            }
            return sb.ToString();
        }
    }
}
