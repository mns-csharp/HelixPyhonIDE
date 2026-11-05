using System;
using System.Threading;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// The Directory Structure Correction Engine — the single entry point for solution policy
    /// rule 4 (<c>PY8004</c>): the Solution Explorer tree and the on-disk directory structure are
    /// the same structure rendered twice.
    ///
    /// It handles a source project that has a TOML manifest and is otherwise IDE-compliant but
    /// whose disk layout disagrees with the tree the IDE draws for it, in six stages:
    ///
    /// <list type="number">
    /// <item><see cref="Verify"/> — scan (read-only) and diff, producing the report that is the
    /// oracle for every claim about this feature;</item>
    /// <item><see cref="PlanRepair"/> — turn the report into an ordered, inspectable plan;</item>
    /// <item>the caller shows <see cref="WarningMessage"/> with OK/Cancel and the plan detail;</item>
    /// <item><see cref="Repair"/> — execute transactionally with a journal and rollback;</item>
    /// <item>re-verify, and roll back if the result is not clean.</item>
    /// </list>
    ///
    /// <b>Call order matters at load time.</b> The check belongs in the solution-loading path, after
    /// the model is built and BEFORE the tree is rendered — and in the loader rather than on a menu
    /// handler, so Recent Files, drag-and-drop and command-line open all pass through it too. It
    /// also runs BEFORE the compliance rules: PY8001 asks whether a file is inside a member, which
    /// is a path-containment question, and containment is exactly what a structure mismatch puts in
    /// doubt.
    ///
    /// <b>Cancel is not "proceed anyway".</b> There is no proceed-anyway. A cancelled repair leaves
    /// the solution structurally unverified, PY8004 stands, and Compile/Run/Debug stay blocked
    /// exactly as they do for the other three rules — because the point of the rule is that the IDE
    /// will not draw a structure it has not verified.
    /// </summary>
    public sealed class DirectoryStructureCorrectionEngine
    {
        /// <summary>
        /// The prompt shown when a loaded solution does not match its disk. Lives here rather than
        /// in a form so the wording is testable and identical on every route that can trigger it.
        /// </summary>
        public const string WarningMessage =
            "Directory structure is not compatible with the IDE. " +
            "The IDE will now restructure your directory structure.";

        /// <summary>Stage 1–2. Read-only: nothing on disk is touched.</summary>
        public StructureParityReport Verify(SolutionModel solution)
        {
            return Verify(solution, CancellationToken.None);
        }

        public StructureParityReport Verify(SolutionModel solution, CancellationToken cancel)
        {
            return new StructureParityChecker().Compare(solution, cancel);
        }

        /// <summary>Stage 3. Pure: reads the filesystem only to detect collisions.</summary>
        public StructureRepairPlan PlanRepair(StructureParityReport report)
        {
            return new StructureRepairPlanner().Plan(report);
        }

        /// <summary>
        /// Stages 5–6. Only call this after the user has accepted <see cref="WarningMessage"/>.
        ///
        /// <paramref name="reverify"/> must re-load the solution from the repaired disk and return a
        /// fresh report; passing null skips stage 6, which is only appropriate in a unit test that
        /// is asserting on the file operations themselves.
        /// </summary>
        public StructureRepairResult Repair(StructureRepairPlan plan, Func<StructureParityReport> reverify)
        {
            return new StructureRepairExecutor().Execute(plan, reverify);
        }

        /// <summary>
        /// True when this solution can be drawn as-is. The load path's one question.
        /// </summary>
        public bool IsCompatible(SolutionModel solution)
        {
            return Verify(solution).IsVerifiedClean;
        }

        /// <summary>
        /// Operations from a previous repair on this solution that began and never finished, oldest
        /// first; empty when the last run reached a conclusion or none ever ran.
        ///
        /// Checked on the load path BEFORE a new repair is offered. Planning a fresh repair on top
        /// of a half-applied one computes its moves from a layout that is neither the user's
        /// original nor the intended result, and then journals over the only record of how it got
        /// that way.
        /// </summary>
        public System.Collections.Generic.IList<string> FindInterruptedRepair(string solutionDirectory)
        {
            return StructureRepairJournal.ReadInterrupted(solutionDirectory);
        }
    }
}
