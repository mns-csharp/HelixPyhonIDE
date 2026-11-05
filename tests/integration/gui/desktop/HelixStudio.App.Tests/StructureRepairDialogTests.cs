using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The PY8004 consent dialog and the block that gives Cancel its meaning.
    ///
    /// <b>Nothing here calls <c>ShowDialog</c>.</b> This suite's standing convention is to stop at
    /// the wiring rather than drive a modal end-to-end: the run is single-process and single-UI-
    /// thread, so a dialog nobody dismisses blocks every remaining test and can strand a box on the
    /// developer's desktop. The dialog is constructed and inspected; the decision it feeds is tested
    /// through <c>ComputeRunPreflight</c> directly.
    /// </summary>
    [TestClass]
    public class StructureRepairDialogTests
    {
        private static StructureParityReport ReportWith(params StructureDefect[] defects)
        {
            var report = new StructureParityReport(@"C:\solution");
            foreach (StructureDefect d in defects) report.Add(d);
            return report;
        }

        private static StructureDefect Depth()
        {
            return new StructureDefect(StructureDefectClass.Depth, "engine", "src/engine",
                "drawn under the solution but lives in 'src'");
        }

        private static StructureDefect Orphan()
        {
            return new StructureDefect(StructureDefectClass.Orphan, null, "README.md",
                "file exists on disk but the tree shows no node for it");
        }

        // ---- the summary the user actually reads ---------------------------------------------

        /// <summary>
        /// The header leads with how many folders will MOVE, because that is the part that feels
        /// irreversible. A count of defects alone tells the user nothing about what is at stake.
        /// </summary>
        [TestMethod]
        public void Summary_LeadsWithTheNumberOfFoldersThatWillMove()
        {
            StructureParityReport report = ReportWith(Depth());
            StructureRepairPlan plan = new StructureRepairPlanner().Plan(report);

            string summary = StructureRepairDialog.BuildSummary(report, plan);

            StringAssert.Contains(summary, "1 difference");
            StringAssert.Contains(summary, "will be moved or renamed");
        }

        /// <summary>
        /// The opposite case, and the one that stops the prompt from being alarming when nothing is
        /// at stake: a plan that only starts SHOWING files already on disk says exactly that.
        /// </summary>
        [TestMethod]
        public void Summary_SaysNothingMoves_WhenTheRepairIsOnlyRendering()
        {
            StructureParityReport report = ReportWith(Orphan(), Orphan());
            StructureRepairPlan plan = new StructureRepairPlanner().Plan(report);

            string summary = StructureRepairDialog.BuildSummary(report, plan);

            StringAssert.Contains(summary, "2 differences");
            StringAssert.Contains(summary, "Nothing will be moved or renamed");
        }

        [TestMethod]
        public void Summary_WarnsWhenSomeDefectsWillBeLeftAlone()
        {
            var report = ReportWith(new StructureDefect(StructureDefectClass.Type, "x", "x",
                "the tree shows a directory where the disk has a file"));
            StructureRepairPlan plan = new StructureRepairPlanner().Plan(report);

            StringAssert.Contains(StructureRepairDialog.BuildSummary(report, plan), "human decision");
        }

        // ---- the dialog itself ----------------------------------------------------------------

        /// <summary>
        /// The prompt text is the engine's constant, not a string retyped in the form. One wording,
        /// asserted without constructing anything, identical on every route that can raise it.
        /// </summary>
        [TestMethod]
        public void ThePromptIsTheEnginesOwnWording()
        {
            Assert.AreEqual(
                "Directory structure is not compatible with the IDE. "
                + "The IDE will now restructure your directory structure.",
                DirectoryStructureCorrectionEngine.WarningMessage);
        }

        /// <summary>
        /// Every operation, refusal and follow-up is listed. A modal that moves an unknown number of
        /// a user's directories on a two-word summary is not a confirmation, so the plan has to be
        /// reachable — and complete — before they press OK.
        /// </summary>
        [TestMethod]
        public void TheDialogListsTheWholePlan()
        {
            StructureParityReport report = ReportWith(Depth(), Orphan());
            StructureRepairPlan plan = new StructureRepairPlanner().Plan(report);

            using (var dialog = new StructureRepairDialog(report, plan))
            {
                ListView list = dialog.Controls.OfType<ListView>().Single();
                int expected = plan.Operations.Count + plan.Refusals.Count + plan.ManualFollowUps.Count;
                Assert.AreEqual(expected, list.Items.Count, "the detail view must show the whole plan");

                var rendered = list.Items.Cast<ListViewItem>()
                    .Select(i => i.SubItems[1].Text).ToList();
                Assert.IsTrue(rendered.Any(t => t.Contains("src/engine") && t.Contains("engine")),
                    "the move must be shown as old → new: " + string.Join(" | ", rendered));
            }
        }

        /// <summary>
        /// The plan starts hidden and Cancel holds focus. Enter on a dialog the user has not read
        /// must not start moving their directories.
        /// </summary>
        [TestMethod]
        public void TheDialogOpensCollapsed_WithCancelFocused()
        {
            StructureParityReport report = ReportWith(Depth());
            using (var dialog = new StructureRepairDialog(report, new StructureRepairPlanner().Plan(report)))
            {
                Assert.IsFalse(dialog.Controls.OfType<ListView>().Single().Visible,
                    "the plan detail must start collapsed");
                var cancel = dialog.ActiveControl as Button;
                Assert.IsNotNull(cancel, "focus must be on a button");
                Assert.AreEqual("Cancel", cancel.Text, "Cancel must be the safe default");
                Assert.AreSame(cancel, dialog.CancelButton);
            }
        }

        // ---- what Cancel actually costs --------------------------------------------------------

        /// <summary>
        /// The assertion that makes Cancel mean something. Without this the prompt is decorative:
        /// declining the restructure would leave the IDE compiling and running a layout it has just
        /// determined it cannot vouch for.
        ///
        /// Uses <c>new MainForm()</c> deliberately — the parameterless constructor builds no runtime
        /// panels, and this path returns before touching any of them, so no composed shell (and no
        /// message pump) is needed.
        /// </summary>
        [TestMethod]
        public void AnUnverifiedStructure_BlocksRunAndDebug()
        {
            using (var form = new MainForm())
            {
                var solution = new SolutionModel(Path.Combine(Path.GetTempPath(), "x", "pyproject.toml"), "x");

                form.StructureUnverifiedReason = "3 items on disk do not match the Solution Explorer";
                var blocked = form.ComputeRunPreflight(solution);

                Assert.IsFalse(blocked.CanRun);
                StringAssert.Contains(blocked.BlockReason, "do not match the Solution Explorer");
                StringAssert.Contains(blocked.BlockReason, "Compile, Run and Debug are blocked");
            }
        }

        /// <summary>
        /// PY8004 is judged BEFORE PY8001-3, so the reason the user sees is the structure, not a
        /// membership error computed from a layout the IDE has already flagged as untrustworthy.
        /// Sending them to fix the wrong thing is worse than saying nothing.
        /// </summary>
        [TestMethod]
        public void TheStructureBlock_IsReportedBeforeTheOtherPolicyRules()
        {
            using (var form = new MainForm())
            {
                var solution = new SolutionModel(Path.Combine(Path.GetTempPath(), "x", "pyproject.toml"), "x");

                form.StructureUnverifiedReason = null;
                string withoutStructure = form.ComputeRunPreflight(solution).BlockReason ?? string.Empty;

                form.StructureUnverifiedReason = "1 item on disk does not match the Solution Explorer";
                string withStructure = form.ComputeRunPreflight(solution).BlockReason ?? string.Empty;

                Assert.AreNotEqual(withoutStructure, withStructure);
                StringAssert.Contains(withStructure, "Solution Explorer");
            }
        }

        /// <summary>A verified solution must not be blocked by this rule at all.</summary>
        [TestMethod]
        public void AVerifiedStructure_DoesNotBlockAnything()
        {
            using (var form = new MainForm())
            {
                var solution = new SolutionModel(Path.Combine(Path.GetTempPath(), "x", "pyproject.toml"), "x");
                form.StructureUnverifiedReason = null;

                string reason = form.ComputeRunPreflight(solution).BlockReason ?? string.Empty;
                Assert.IsFalse(reason.Contains("Solution Explorer"), reason);
            }
        }
    }
}
