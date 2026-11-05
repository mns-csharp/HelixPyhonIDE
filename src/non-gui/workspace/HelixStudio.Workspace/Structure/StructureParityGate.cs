namespace HelixPythonNamespace.Workspace.Structure
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The verdict on solution policy rule 4 for one load: may Compile/Run/Debug proceed, and if
    /// not, what is wrong and what the user can do about it.
    /// </summary>
    public sealed class StructureParityVerdict
    {
        /// <summary>
        /// True only when the tree and the disk are the same structure. The claim is the report's,
        /// never a screenshot's and never a bare <see cref="StructureParityReport.IsEmpty"/> — a
        /// scan that stopped early finds nothing and means nothing.
        /// </summary>
        public bool IsVerified;

        /// <summary>
        /// "N items on disk do not match the Solution Explorer" — the bare count, for a host that
        /// wants to append its own suffix (the IDE appends "(restructure declined)" and
        /// "(repair failed)"). Null when verified.
        /// </summary>
        public string Summary;

        /// <summary>What is wrong, if the host takes no further action. Null when verified.</summary>
        public string Reason;

        /// <summary>What the user should actually DO about it. Null when verified.</summary>
        public string Remedy;

        /// <summary>
        /// The repair the engine planned, when one exists. Null when there is nothing to plan, when
        /// planning threw, or when the report never got far enough to plan from.
        /// </summary>
        public StructureRepairPlan Plan;

        /// <summary>
        /// True when <see cref="Plan"/> has real disk work and no blocking refusal — i.e. a host
        /// with someone to confirm it could offer the restructure. A headless host cannot, and
        /// stops at <see cref="Reason"/>/<see cref="Remedy"/> instead.
        /// </summary>
        public bool RepairAvailable;
    }

    /// <summary>
    /// The stages of the PY8004 load gate that do not need a window — shared by the IDE's
    /// <c>SolutionCoordinator.EnsureStructureParityAsync</c> (which adds the confirm dialog and the
    /// repair itself) and by the headless <c>helixstudio</c> launcher (which never restructures a
    /// user's directories and simply refuses to run).
    ///
    /// It lives here rather than in the shell because a second implementation of the gate would be
    /// a second answer to "may this solution run", and rule 4's whole point is that the IDE does
    /// not vouch for a structure it has not verified. Two hosts, one verdict.
    /// </summary>
    public static class StructureParityGate
    {
        /// <summary>
        /// Evaluate one load's parity report. <paramref name="engine"/> is used for the interrupted-
        /// repair probe and the plan; pass a fresh <see cref="DirectoryStructureCorrectionEngine"/>.
        /// </summary>
        public static StructureParityVerdict Evaluate(
            StructureParityReport report, DirectoryStructureCorrectionEngine engine)
        {
            // A null report means the scan did not run or threw — "not verified", never "clean".
            // Nothing to offer in that case, so the caller proceeds as before.
            if (report == null) return new StructureParityVerdict { IsVerified = true };
            if (engine == null) engine = new DirectoryStructureCorrectionEngine();

            // A repair that began and never finished, BEFORE anything else. Planning on top of a
            // half-applied restructure computes moves from a layout that is neither what the user
            // had nor what was intended, and journals over the only record of how it got that way.
            IList<string> interrupted;
            try { interrupted = engine.FindInterruptedRepair(report.SolutionDirectory); }
            catch { interrupted = new List<string>(); }
            if (interrupted.Count > 0)
                return Unverified(
                    null,
                    "a previous restructure did not finish (" + interrupted.Count
                        + (interrupted.Count == 1 ? " operation" : " operations") + " left in flight)",
                    "The folders are part-way between two layouts. See .helixstudio\\structure-repair.journal "
                        + "for what was applied — the last entry with no \"DONE\" is the one that was "
                        + "interrupted — put it back by hand, then reopen the solution.");

            // A walk that stopped early found nothing because it did not look, so an empty report
            // from it is not a clean one. Checked before IsEmpty for exactly that reason.
            if (!report.ScanCompleted)
                return Unverified(
                    null,
                    "the folder scan could not finish, so the structure was never checked",
                    "Check that every folder under the solution is readable and that no folder link "
                        + "points back into the solution, then reopen it.");

            if (report.IsEmpty) return new StructureParityVerdict { IsVerified = true };

            StructureRepairPlan plan;
            try { plan = engine.PlanRepair(report); }
            catch { plan = null; }

            string summary = report.Defects.Count
                + (report.Defects.Count == 1 ? " item" : " items")
                + " on disk do not match the Solution Explorer";

            if (plan == null)
                return Unverified(summary, summary,
                    "The IDE could not work out a correction for this one. Resolve it on disk, then "
                        + "reopen the solution.");

            // The planner has already declined something only a disk repair could have fixed, so
            // executing the rest could never make the solution verified. Offering the prompt anyway
            // would move the user's directories and then move them all back.
            if (plan.HasBlockingRefusal)
            {
                var refused = Unverified(summary, summary,
                    "The IDE will not restructure this one: " + plan.BlockingRefusals[0]
                        + " Resolve that on disk, then reopen the solution.");
                refused.Plan = plan;
                return refused;
            }

            if (!plan.TouchesDisk)
            {
                // Nothing to move — every remaining difference is one the tree should simply draw.
                // Telling this user to "accept the restructure when prompted" sent them round a
                // loop with no exit, because no prompt is raised for a plan with no disk work.
                var undrawable = Unverified(summary, summary,
                    "No folders need to move — these are paths the Solution Explorer did not draw. "
                        + "Reopen the solution; if it stays blocked, the tree cannot render them and "
                        + "the IDE cannot clear this one itself.");
                undrawable.Plan = plan;
                return undrawable;
            }

            // A restructure IS available. An interactive host offers it; a headless one reports
            // this remedy and refuses to run, because the action behind the prompt MOVES the user's
            // directories and nobody said yes.
            var offerable = Unverified(summary, summary,
                "A restructure is available but there is no interactive session to confirm it.");
            offerable.Plan = plan;
            offerable.RepairAvailable = true;
            return offerable;
        }

        /// <summary>
        /// The single sentence Compile/Run/Debug refuse with, composed from a verdict's two halves.
        /// Returns null for a verified verdict, which is what makes it usable directly as
        /// <c>SolutionRunOptions.StructureBlockReason</c>.
        /// </summary>
        public static string ComposeBlockReason(string reason, string remedy)
        {
            if (string.IsNullOrEmpty(reason)) return null;
            string advice = string.IsNullOrEmpty(remedy)
                // No remedy means the host could not name one. Say that, rather than naming an
                // action the IDE will not offer.
                ? "Reopen the solution; if it stays blocked, the IDE cannot correct this one itself."
                : remedy;
            return "The solution's folders do not match the Solution Explorer (" + reason
                + "). Compile, Run and Debug are blocked until the structure is corrected. " + advice;
        }

        private static StructureParityVerdict Unverified(string summary, string reason, string remedy)
        {
            return new StructureParityVerdict
            {
                IsVerified = false,
                Summary = summary,
                Reason = reason,
                Remedy = remedy,
            };
        }
    }
}
