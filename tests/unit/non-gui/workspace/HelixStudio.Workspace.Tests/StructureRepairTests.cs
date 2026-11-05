using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The correcting half of the engine: planning (stage 3) and transactional execution with
    /// re-verification (stages 5–6).
    ///
    /// Every test that writes works on a COPY of a fixture in <c>%TEMP%</c>. The tracked fixtures
    /// are never mutated — a repair test that restructured <c>tests/fixtures/</c> in place would
    /// destroy the oracle it is asserting against, and would do it on the first run.
    /// </summary>
    [TestClass]
    public class StructureRepairTests
    {
        private static string CopyFixtureToTemp(string which)
        {
            string source = StructureParityTests.Fixture(which);
            string target = StructureParityTests.NewTempSolutionDirectory();
            CopyTree(source, target);
            return target;
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, "__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
                CopyTree(directory, Path.Combine(target, name));
            }
        }

        private static StructureParityReport Verify(string root)
        {
            return new DirectoryStructureCorrectionEngine().Verify(StructureParityTests.Load(root));
        }

        // ---- planning ------------------------------------------------------------------------

        /// <summary>
        /// Moves before renames, and deepest source first.
        ///
        /// Both halves are load-bearing. A rename queued before its move would operate on a path
        /// the move is about to invalidate; a shallow move applied before a deeper one would
        /// relocate a parent out from under a queued child. Neither shows up in a plan that is only
        /// eyeballed, which is why the order is asserted rather than assumed.
        /// </summary>


        /// <summary>
        /// An orphan is repaired by SHOWING it, not by moving it. A plan whose only defects are
        /// orphans must not touch the disk at all — if it did, opening a solution with a stray
        /// README would start rearranging the user's repository.
        ///
        /// The model here is built BY HAND and deliberately not inventoried, because that is the
        /// only way an orphan still arises: the production loader now records the real disk content
        /// on the model and the tree renders it, so a stray README loaded normally is a node rather
        /// than a defect. What is left is the case the Orphan class is really for — a model that
        /// has fallen behind the disk — and this pins that its repair is still tree-side.
        /// </summary>
        [TestMethod]
        public void Plan_RepairsAnOrphanByRenderingIt_NotByMovingAnything()
        {
            string root = StructureParityTests.NewTempSolutionDirectory();
            Directory.CreateDirectory(Path.Combine(root, "pkg"));
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"s\"\n\n[tool.uv.workspace]\nmembers = [\"pkg\"]\n");
            File.WriteAllText(Path.Combine(root, "pkg", "pyproject.toml"),
                "[project]\nname = \"pkg\"\n\n[tool.helixstudio]\nkind = \"library\"\n");
            File.WriteAllText(Path.Combine(root, "pkg", "shapes.py"), "class Square:\n    pass\n");
            File.WriteAllText(Path.Combine(root, "README.md"), "# stray, and unknown to the model\n");

            var solution = new SolutionModel(Path.Combine(root, "pyproject.toml"), "s");
            solution.Projects.Add(new ProjectModel(Path.Combine(root, "pkg", "pyproject.toml"), "pkg", solution));

            var engine = new DirectoryStructureCorrectionEngine();
            StructureParityReport report = engine.Verify(solution);
            StructureRepairPlan plan = engine.PlanRepair(report);

            CollectionAssert.Contains(
                report.OfClass(StructureDefectClass.Orphan).Select(d => d.DiskPath).ToList(),
                "README.md", report.Describe());
            Assert.IsFalse(plan.IsEmpty, plan.Describe());
            Assert.IsFalse(plan.TouchesDisk, "an orphan-only plan must be tree-side\n" + plan.Describe());
            Assert.IsTrue(plan.Operations.All(o => o.Kind == StructureRepairKind.RenderNode), plan.Describe());
        }

        /// <summary>
        /// Refuse rather than overwrite. <c>SolutionMembershipFixer</c> set this precedent for
        /// PY8001's quick fix and it matters more here, because the thing that would be lost is a
        /// whole directory rather than one file.
        /// </summary>


        // ---- execution -----------------------------------------------------------------------

        /// <summary>
        /// The whole engine, end to end, on a real skewed solution: verify → plan → execute →
        /// re-verify. The re-check is the assertion that matters; the individual
        /// <c>Directory.Exists</c> calls below only say WHERE things went.
        /// </summary>


        /// <summary>
        /// <b>The assertion that stops this feature from breaking builds.</b>
        ///
        /// Moving <c>pkgs/skew_engine</c> to <c>skew_engine</c> is five lines of file I/O and a
        /// long tail of references. If <c>[tool.uv.workspace] members</c> is not updated with it,
        /// every member of the repaired solution becomes a PY8001 violation — the engine would
        /// "fix" the tree by breaking the workspace.
        /// </summary>


        /// <summary>
        /// The manifest rewrite is surgical: comments, unrelated tables and the file's own line
        /// endings survive. A whole-file re-emit would CRLF-ify an LF manifest and turn a two-word
        /// change into a whole-file diff for anyone sharing the repository off Windows — the exact
        /// defect <c>PyProjectStartupWriter</c> had to be fixed for.
        /// </summary>


        /// <summary>
        /// A file whose name merely CONTAINS a moved path must not be rewritten. The rewriter only
        /// replaces a whole quoted string, because a blanket search-and-replace of <c>pkgs</c>
        /// would also corrupt a description, a URL and a dependency name on its way past the one
        /// array that needed changing.
        /// </summary>


        /// <summary>
        /// A case-only rename must actually change the name on disk. A single
        /// <c>Directory.Move("Skew_Tests", "skew_tests")</c> on NTFS is a no-op or an error,
        /// because the two names are one path as far as the filesystem is concerned — the repair
        /// has to go via a temporary name. Asserted against the real directory entry, not against
        /// <c>Directory.Exists</c>, which answers case-insensitively and would pass either way.
        /// </summary>


        /// <summary>
        /// Stage 6 is not decoration. If the re-check is not clean the repair is rolled back and
        /// reported as a failure — a half-restructured solution matches neither what the user had
        /// nor what the IDE expects, and reporting that as success would be the worst outcome this
        /// engine can produce.
        /// </summary>


        /// <summary>
        /// The journal is the user's record of what happened, so it is written as the repair runs
        /// rather than summarised at the end — a crash mid-plan must still leave evidence.
        /// </summary>


        /// <summary>
        /// The engine's own bookkeeping is not part of the user's structure: the journal it writes
        /// must not turn into an orphan the next time the solution is checked. Without the
        /// <c>.helixstudio</c> exclusion, running a repair would guarantee a defect on the very
        /// next load — an engine that cannot converge.
        /// </summary>


        /// <summary>
        /// Repair is idempotent: running it again on an already-clean solution plans nothing and
        /// writes nothing. An engine that keeps finding work after it has finished would prompt the
        /// user on every single open.
        /// </summary>


        /// <summary>
        /// The repaired solution is COMPLIANT, not merely disk-repaired.
        ///
        /// This is the assertion the engine did not have, and its absence is the whole reason the
        /// motivating solution ended up permanently blocked. Success was judged by
        /// <c>StructureDefect.IsDiskRepairable</c> — a predicate defined to EXCLUDE the classes the
        /// repair cannot fix — so a run that left fourteen orphans standing reported "Structure
        /// repair: OK" and handed back a solution with Compile/Run/Debug blocked and no prompt the
        /// user could accept. An engine whose success criterion omits its own failures cannot be
        /// hardened by tightening the failures; the criterion has to be the user-visible outcome.
        /// </summary>


        /// <summary>
        /// A refusal that leaves a disk-repairable defect stops the repair BEFORE it starts.
        ///
        /// Stage 6 rolls back unless every disk-repairable defect is gone, so executing such a plan
        /// is guaranteed to move the user's directories and then move all of them back: real I/O,
        /// a wait, and a "repair failed" message, for a collision the planner had already detected
        /// without touching anything. The assertion that matters is the last one — the directory
        /// that would have moved is still where it was.
        /// </summary>


        /// <summary>
        /// A Type clash refuses without blocking. It is not disk-repairable, so stage 6 does not
        /// count it, and refusing the whole repair over one would strand every other defect in the
        /// solution behind a defect nothing can fix.
        /// </summary>
        [TestMethod]
        public void Repair_ATypeClashRefusal_DoesNotBlockTheRestOfThePlan()
        {
            string root = StructureParityTests.NewTempSolutionDirectory();
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"s\"\n");
            File.WriteAllText(Path.Combine(root, "ghost"), "not a directory");

            var solution = new SolutionModel(Path.Combine(root, "pyproject.toml"), "s");
            solution.Projects.Add(new ProjectModel(Path.Combine(root, "ghost", "pyproject.toml"), "ghost", solution));

            var engine = new DirectoryStructureCorrectionEngine();
            StructureRepairPlan plan = engine.PlanRepair(engine.Verify(solution));

            Assert.IsFalse(plan.CanFullyRepair, plan.Describe());
            Assert.IsFalse(plan.HasBlockingRefusal, plan.Describe());
        }

        /// <summary>
        /// The journal records the INTENT before the operation, not the success after it.
        ///
        /// A completed-log is a record of the things that went right — precisely the entries that
        /// need no recovery. The entry that matters is the operation that was applied and never
        /// acknowledged, which a post-hoc log cannot contain by construction: if the process dies
        /// between the move and the append, the only operation needing an undo is the only one
        /// missing from the file.
        /// </summary>


        /// <summary>
        /// An operation that began and was never acknowledged is legible as interrupted. This is
        /// the state a crash, a kill or a power loss leaves behind, and before the journal was
        /// readable the next load simply saw a differently-shaped solution and derived a fresh set
        /// of defects from the wreckage.
        /// </summary>
        [TestMethod]
        public void AnUnacknowledgedOperation_ReadsBackAsInterrupted()
        {
            string root = StructureParityTests.NewTempSolutionDirectory();
            Directory.CreateDirectory(Path.Combine(root, ".helixstudio"));
            File.WriteAllLines(StructureRepairJournal.PathFor(root), new[]
            {
                "# structure repair for " + root,
                "BEGIN MoveDirectory: pkgs/skew_engine -> skew_engine",
                "DONE MoveDirectory: pkgs/skew_engine -> skew_engine",
                "BEGIN RenameDirectory: application -> skew_app",
            });

            IList<string> pending = StructureRepairJournal.ReadInterrupted(root);

            Assert.AreEqual(1, pending.Count, string.Join(", ", pending));
            Assert.AreEqual("RenameDirectory: application -> skew_app", pending[0]);
            Assert.IsTrue(StructureRepairJournal.WasInterrupted(root));
        }

        /// <summary>
        /// A second run moves the previous journal aside instead of overwriting it. That record is
        /// worth most in exactly the case where a second run happens — the previous repair failed,
        /// its rollback was incomplete, and the journal is the user's only account of which half of
        /// their solution moved.
        /// </summary>

            }
}
