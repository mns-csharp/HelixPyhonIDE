using System;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Services;
using HelixPythonNamespace.Workspace.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Structure parity (PY8004): the Solution Explorer tree and the on-disk layout must be one
    /// structure rendered twice.
    ///
    /// Every fixture here is loaded through the production <see cref="SolutionService.LoadAsync"/>
    /// path and checked with <see cref="DirectoryStructureCorrectionEngine.Verify"/>. There is no
    /// <c>MainForm</c> and no message pump: the checker projects the tree from the MODEL, not from
    /// a <c>TreeView</c>, which is exactly what lets these run in the fast non-GUI suite.
    ///
    /// READ-ONLY. Nothing here mutates a fixture — the repair side works on temp copies, in
    /// <see cref="StructureRepairTests"/>.
    /// </summary>
    [TestClass]
    public class StructureParityTests
    {
        private const string FixtureRoot = @"tests\fixtures\python";

        internal static string Fixture(string which)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, FixtureRoot)))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "fixture tree not found above " + AppDomain.CurrentDomain.BaseDirectory);
            string path = Path.Combine(dir.FullName, FixtureRoot, which);
            if (!Directory.Exists(path)) Assert.Inconclusive("Optional workspace test data is unavailable.");
            return path;
        }

        internal static SolutionModel Load(string root)
        {
            var loaded = new SolutionService()
                .LoadAsync(Path.Combine(root, "pyproject.toml")).GetAwaiter().GetResult();
            Assert.IsNotNull(loaded, root + " must load");
            Assert.IsNotNull(loaded.Solution, root + " must load: " + loaded.ErrorMessage);
            return loaded.Solution;
        }

        private static StructureParityReport Check(string which)
        {
            return new DirectoryStructureCorrectionEngine().Verify(Load(Fixture(which)));
        }

        // ---- the clean half ------------------------------------------------------------------





        // ---- the flat solution: the member IS the solution root ------------------------------

        /// <summary>
        /// <b>The shape the New Project wizard writes, which the engine used to reject on sight.</b>
        ///
        /// <c>py.console</c> creates a single-project solution with no member subdirectory: the
        /// root <c>pyproject.toml</c> carries both <c>[project]</c> and
        /// <c>[tool.helixstudio] kind</c>, so <c>ProjectDirectory</c> IS <c>SolutionDirectory</c>
        /// and the project node stands for the solution directory rather than a folder of its own.
        /// <c>SolutionService</c> loads that deliberately.
        ///
        /// Creating a console project and immediately being told the solution is "not compatible
        /// with the IDE" is what this fixture pins. Reported: six defects — a Phantom for the
        /// <c>project.Name</c> folder the projector invented, an Orphan for every one of the four
        /// real root files, and a Name defect reading "project is named 'x' but its folder is
        /// named ''". Both existing parity fixtures are multi-member workspaces, which is why none
        /// of it was caught; a flat solution had never been through the checker.
        ///
        /// The Orphans were not four independent misses. <c>DiskToNode</c> rebased through
        /// <c>Substring(anchor.Actual.Length + 1)</c>, and an anchor at the root is <c>""</c>, so
        /// every disk path lost its first character (<c>main.py</c> → <c>ain.py</c>) and could not
        /// match anything. See <c>StructurePath.Remainder</c>.
        /// </summary>
        [TestMethod]
        public void CompletedRepairJournal_IsIdeState_NotAResourceOrParityDefect()
        {
            string root = NewTempSolutionDirectory();
            Directory.CreateDirectory(Path.Combine(root, ".helixstudio"));
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"app\"\n\n[tool.helixstudio]\nkind = \"app\"\nstartup = true\n");
            File.WriteAllText(Path.Combine(root, "main.py"), "def main():\n    return 0\n");
            File.WriteAllText(Path.Combine(root, ".helixstudio", "structure-repair.journal"),
                "BEGIN RenameDirectory: old -> app\nDONE RenameDirectory: old -> app\n");

            SolutionModel solution = Load(root);
            ProjectModel project = solution.Projects.Single();
            CollectionAssert.DoesNotContain(project.ResourceFiles.ToList(),
                ".helixstudio/structure-repair.journal");

            StructureParityReport report = new DirectoryStructureCorrectionEngine().Verify(solution);
            Assert.IsTrue(report.IsVerifiedClean, report.Describe());
        }



        /// <summary>
        /// The model half of the same claim, so a future regression says which side moved: the
        /// loader anchors the member at the solution root and inventories the root files onto it,
        /// exactly as <c>StructureInventory</c>'s "a member rooted at the solution" branch intends.
        /// If this passes and the two above fail, the projector or the checker has drifted from it
        /// again.
        /// </summary>


        /// <summary>
        /// The other root-anchored shape: a workspace whose root <c>pyproject.toml</c> is itself a
        /// member as well as the aggregator — what a solution becomes the moment a second project
        /// is added to a wizard-created one. The root member's node is at <c>""</c> and the added
        /// member's is at its own name, and the checker has to rebase through both.
        /// </summary>
        [TestMethod]
        public void AWorkspaceRootThatIsAlsoAMember_IsClean()
        {
            string root = NewTempSolutionDirectory();
            Directory.CreateDirectory(Path.Combine(root, "engine"));
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"app\"\n\n[tool.uv.workspace]\nmembers = [\"engine\"]\n"
                + "\n[tool.helixstudio]\nkind = \"app\"\nstartup = true\n");
            File.WriteAllText(Path.Combine(root, "main.py"),
                "def main():\n    print(\"hi\")\n\n\nif __name__ == \"__main__\":\n    main()\n");
            File.WriteAllText(Path.Combine(root, "engine", "pyproject.toml"),
                "[project]\nname = \"engine\"\n\n[tool.helixstudio]\nkind = \"library\"\n");
            File.WriteAllText(Path.Combine(root, "engine", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "engine", "shapes.py"), "class Square:\n    pass\n");

            SolutionModel solution = Load(root);
            Assert.AreEqual(2, solution.Projects.Count,
                string.Join(", ", solution.Projects.Select(p => p.Name)));

            StructureParityReport report = new DirectoryStructureCorrectionEngine().Verify(solution);
            Assert.IsTrue(report.IsVerifiedClean, report.Describe());
        }

        /// <summary>
        /// The backstop, pinned because of what it prevents rather than because anything still
        /// reaches it. The bogus Name defect above planned
        /// <c>RenameDirectory: "" -> cuda_python_practice</c>, whose source resolves to the
        /// solution directory itself: <c>Directory.Move</c> of the user's whole solution into a
        /// subdirectory of itself. Windows refuses it and the unwind rolls back, but the planner
        /// must not emit it and then rely on the filesystem to say no.
        /// </summary>
        [TestMethod]
        public void ADefectNamingTheSolutionRoot_IsRefused_NotPlannedAsAMove()
        {
            var report = new StructureParityReport(NewTempSolutionDirectory());
            report.Add(new StructureDefect(StructureDefectClass.Name, "app", "", "hand-made"));

            StructureRepairPlan plan = new DirectoryStructureCorrectionEngine().PlanRepair(report);
            Assert.IsFalse(plan.TouchesDisk, plan.Describe());
            Assert.IsTrue(plan.Refusals.Any(r => r.Contains("solution's own root")), plan.Describe());
        }

        // ---- the skewed half: one member per repairable defect class -------------------------







        /// <summary>
        /// The regression this class exists to prevent recurring.
        ///
        /// <c>PathComparison.Comparison</c> is <c>OrdinalIgnoreCase</c> on Windows, so comparing
        /// the project name against the folder name for IDENTITY makes <c>Skew_Tests</c> equal to
        /// <c>skew_tests</c> and the whole Case class permanently unreachable — the checker returns
        /// "no defect" and the engine looks like it works. It was written that way first and this
        /// fixture caught it. Whether the two spellings are the same DIRECTORY is not the question
        /// being asked at that line.
        /// </summary>


        /// <summary>
        /// The root content no member owns is INVENTORIED AND RENDERED, not reported.
        ///
        /// It used to be three orphans, and there was nothing anyone could do about them: the
        /// repair for an orphan is "render it", the renderer did not, and so the defect stood for
        /// the life of the solution and kept Compile/Run/Debug blocked. The fix is not to stop
        /// reporting them — it is to draw them. This asserts both halves: the loader put them on
        /// the model, and the checker consequently has nothing to say.
        /// </summary>


        /// <summary>
        /// <c>pkgs/</c> exists on disk and has no node, but it is NOT reported as an orphan: it is
        /// only there because it contains a package whose Depth defect already names the problem
        /// and already carries the repair. Reporting both would give the user two rows and two
        /// different instructions for one situation — the same reason PY8003 stays silent when
        /// PY8001 has already claimed a file.
        /// </summary>


        // ---- the real-world oracle -----------------------------------------------------------

        /// <summary>
        /// <c>bugmap_oop</c> is the tracked copy of the solution this engine was written from. The
        /// IDE reported "Loaded: 5 project(s), 205 type(s)" against the live original and raised no
        /// structural complaint at all, while drawing five nodes that did not correspond to the
        /// directories they claimed.
        ///
        /// Asserted by CONTENT, not by total count: the fixture carries unrelated drift (loose
        /// files, an <c>examples/</c> directory belonging to no member) whose orphan count is not
        /// something this test should pin.
        /// </summary>


        /// <summary>
        /// The user-written code in <c>examples/</c> belongs to no member. It is still a PY8001
        /// membership violation — but it is no longer a PARITY defect, because the tree shows it.
        ///
        /// That is the ordering rule resolving itself rather than being applied. PY8004 deferred
        /// PY8001 on a shared file because a structure the IDE had not verified made "is this file
        /// inside a member?" unanswerable. Once the file has a node at its real path, containment
        /// is no longer in doubt and the membership rule can own the complaint alone — one problem,
        /// one instruction, which is what the deferral was protecting.
        /// </summary>


        // ---- the two classes a directory layout cannot express -------------------------------

        /// <summary>
        /// Phantom needs a model that disagrees with a real disk, so it is built by hand rather
        /// than as a fixture: a project whose directory simply is not there.
        /// </summary>
        [TestMethod]
        public void AProjectDirectoryThatDoesNotExist_IsAPhantom()
        {
            string root = NewTempSolutionDirectory();
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"s\"\n");

            var solution = new SolutionModel(Path.Combine(root, "pyproject.toml"), "s");
            solution.Projects.Add(new ProjectModel(Path.Combine(root, "ghost", "pyproject.toml"), "ghost", solution));

            StructureParityReport report = new DirectoryStructureCorrectionEngine().Verify(solution);
            var phantoms = report.OfClass(StructureDefectClass.Phantom).Select(d => d.NodePath).ToList();
            CollectionAssert.Contains(phantoms, "ghost", report.Describe());
        }

        /// <summary>
        /// A Type clash is never auto-repaired, and the plan must say so rather than silently
        /// producing a shorter plan: every automatic resolution here either deletes a file or
        /// invents one.
        /// </summary>
        [TestMethod]
        public void AFileWhereTheTreeShowsAFolder_IsATypeClash_AndIsRefused()
        {
            string root = NewTempSolutionDirectory();
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"s\"\n");
            File.WriteAllText(Path.Combine(root, "ghost"), "not a directory");

            var solution = new SolutionModel(Path.Combine(root, "pyproject.toml"), "s");
            solution.Projects.Add(new ProjectModel(Path.Combine(root, "ghost", "pyproject.toml"), "ghost", solution));

            var engine = new DirectoryStructureCorrectionEngine();
            StructureParityReport report = engine.Verify(solution);
            Assert.AreEqual(1, report.CountOf(StructureDefectClass.Type), report.Describe());
            Assert.IsTrue(report.HasUnrepairable);

            StructureRepairPlan plan = engine.PlanRepair(report);
            Assert.IsFalse(plan.CanFullyRepair, plan.Describe());
            Assert.IsTrue(plan.Refusals.Any(r => r.Contains("human decision")), plan.Describe());
        }

        // ---- the gap that used to be pinned, now closed ---------------------------------------

        /// <summary>
        /// <b>A .py file with no class and no module-level function is inventoried, and so is not
        /// an orphan.</b>
        ///
        /// This test used to assert the opposite, and said why: the Solution Explorer bucketed
        /// files it discovered through <c>ProjectModel.Types</c> and <c>.Functions</c>, and
        /// <c>PackageResourceScanner</c> skips <c>.py</c> by design, so a symbol-free module — the
        /// overwhelmingly common empty <c>__init__.py</c> — was invisible in the tree while sitting
        /// plainly in Explorer. It was a TRUE POSITIVE against the renderer, and the renderer never
        /// did anything about it: the repair for an orphan is "render it", nothing rendered, and
        /// the defect stood forever. Nine of them were what kept the motivating solution blocked
        /// after a repair the engine had reported as successful.
        ///
        /// The old version left a note that whoever made the tree show symbol-free modules would
        /// see this test fail and learn that the fixture orphan counts move with it. That is
        /// exactly what happened; those counts moved to zero in the same change.
        /// </summary>
        [TestMethod]
        public void AnEmptyInitFile_IsInventoried_AndSoIsNotAnOrphan()
        {
            string root = NewTempSolutionDirectory();
            Directory.CreateDirectory(Path.Combine(root, "pkg"));
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"s\"\n\n[tool.uv.workspace]\nmembers = [\"pkg\"]\n");
            File.WriteAllText(Path.Combine(root, "pkg", "pyproject.toml"),
                "[project]\nname = \"pkg\"\n\n[tool.helixstudio]\nkind = \"library\"\n");
            File.WriteAllText(Path.Combine(root, "pkg", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "pkg", "shapes.py"), "class Square:\n    pass\n");

            SolutionModel solution = Load(root);
            ProjectModel package = solution.Projects.Single(p => p.Name == "pkg");

            CollectionAssert.Contains(package.ContentFiles.ToList(), "__init__.py",
                string.Join(", ", package.ContentFiles));

            StructureParityReport report = new DirectoryStructureCorrectionEngine().Verify(solution);
            Assert.IsTrue(report.IsVerifiedClean, report.Describe());
        }

        internal static string NewTempSolutionDirectory()
        {
            // Deliberately not deleted. The convention in this repository is to leave test temp
            // solution directories under %TEMP% rather than race an async reload continuation that
            // may still be reading them; it costs nothing here and keeps the habit uniform.
            string path = Path.Combine(Path.GetTempPath(), "helix-parity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
