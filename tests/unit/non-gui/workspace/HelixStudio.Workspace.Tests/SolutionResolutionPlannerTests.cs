using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class SolutionResolutionPlannerTests
    {
        private static readonly string Root =
            Path.Combine(Path.GetTempPath(), "sln");

        private static string P(params string[] parts)
        {
            return Path.Combine(new[] { Root }.Concat(parts).ToArray());
        }

        private static SolutionAuditReport ReportWithMainModules(params string[] modules)
        {
            SolutionAuditReport report = new SolutionAuditReport(Root);
            report.MainModules.AddRange(modules);
            return report;
        }

        /// <summary>In-memory file system for exercising Apply without touching disk.</summary>
        private sealed class FakeFs : IWorkspaceFileSystem
        {
            public readonly HashSet<string> Files =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Moves = new List<string>();
            public readonly Dictionary<string, string> Contents =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public FakeFs(params string[] existing)
            {
                foreach (string f in existing) Files.Add(f);
            }

            public bool FileExists(string path) { return Files.Contains(path); }

            public void MoveFile(string oldPath, string newPath)
            {
                if (!Files.Contains(oldPath)) throw new FileNotFoundException(oldPath);
                if (Files.Contains(newPath)) throw new IOException("exists: " + newPath);
                Files.Remove(oldPath);
                Files.Add(newPath);
                Moves.Add(Path.GetFileName(oldPath) + "->" + Path.GetFileName(newPath));
            }

            public string ReadAllText(string path)
            {
                return Contents.TryGetValue(path, out string text) ? text : string.Empty;
            }

            public void WriteAllText(string path, string content)
            {
                Contents[path] = content;
                Files.Add(path);
            }
        }

        // ---- Plan (#3c: single main-equivalent module) ----

        [TestMethod]
        public void Plan_KeepsExactMainPy_AsCanonical()
        {
            SolutionAuditReport report =
                ReportWithMainModules(P("pkg", "__main__.py"), P("main.py"), P("tools", "_main.py"));

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            Assert.AreEqual(P("main.py"), plan.CanonicalMainModule);
            Assert.AreEqual(2, plan.Renames.Count);
            Assert.IsFalse(plan.Renames.Any(r =>
                string.Equals(r.OldPath, P("main.py"), StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public void Plan_PrefersDunderMain_WhenNoPlainMain()
        {
            SolutionAuditReport report =
                ReportWithMainModules(P("a", "_main.py"), P("b", "__main__.py"));

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            Assert.AreEqual(P("b", "__main__.py"), plan.CanonicalMainModule);
            Assert.AreEqual(1, plan.Renames.Count);
        }

        [TestMethod]
        public void Plan_ProposedTargetsAreNotMainEquivalent()
        {
            SolutionAuditReport report =
                ReportWithMainModules(P("main.py"), P("cli", "__main__.py"), P("svc", "main_.py"));

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            Assert.IsTrue(plan.Renames.Count > 0);
            foreach (FileRenameAction r in plan.Renames)
                Assert.IsFalse(PythonSolutionAuditor.IsMainModule(r.NewPath),
                    "target must not itself be a main module: " + r.NewPath);
        }

        [TestMethod]
        public void Plan_AvoidsCollisionsWithinSameDirectory()
        {
            // Three main-equivalents in the SAME folder — the two renames must be distinct.
            SolutionAuditReport report = ReportWithMainModules(
                P("d", "main.py"), P("d", "_main.py"), P("d", "__main__.py"));

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            Assert.AreEqual(2, plan.Renames.Count);
            string t0 = plan.Renames[0].NewPath;
            string t1 = plan.Renames[1].NewPath;
            Assert.AreNotEqual(t0, t1, "rename targets collided");
            // and neither collides with the kept canonical
            Assert.AreNotEqual(P("d", "main.py"), t0);
            Assert.AreNotEqual(P("d", "main.py"), t1);
        }

        [TestMethod]
        public void Plan_NoDuplicateModules_ProducesNoRenames()
        {
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(ReportWithMainModules(P("main.py")));
            Assert.IsFalse(plan.HasAutomatableFixes);
            Assert.AreEqual(0, plan.Renames.Count);
        }

        [TestMethod]
        public void Plan_NullReport_IsSafe()
        {
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(null);
            Assert.IsFalse(plan.HasAutomatableFixes);
            Assert.IsFalse(plan.HasManualFindings);
        }

        // ---- Plan (#3d: duplicate def main() IS auto-fixed via a same-file function rename) ----

        [TestMethod]
        public void Plan_DuplicateDefMain_IsAutoFixed_ViaFunctionRename()
        {
            SolutionAuditReport report = new SolutionAuditReport(Root);
            report.MainFunctionFiles.AddRange(new[] { P("main.py"), P("save_pdb.py") });
            report.Findings.Add(new SolutionAuditFinding(
                SolutionAuditRule.SingleMainFunction, SolutionAuditSeverity.Error,
                "two def main()", report.MainFunctionFiles, "consolidate"));

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            Assert.IsTrue(plan.HasAutomatableFixes);
            Assert.IsFalse(plan.HasManualFindings, "no longer manual — the planner now auto-fixes this");
            Assert.AreEqual(P("main.py"), plan.CanonicalMainFunctionFile);
            Assert.AreEqual(1, plan.FunctionRenames.Count);
            FunctionRenameAction a = plan.FunctionRenames[0];
            Assert.AreEqual(P("save_pdb.py"), a.FilePath);
            Assert.AreEqual("main", a.OldName);
            Assert.AreEqual("save_pdb_main", a.NewName);
        }

        [TestMethod]
        public void Plan_DuplicateDefMain_PrefersSameCanonicalFile_AsMainModuleRule()
        {
            // main.py is ALSO the file both rules keep canonical, so one file ends up
            // "main.py with def main()" end to end, not two different files each keeping
            // one half of the entry point.
            SolutionAuditReport report = new SolutionAuditReport(Root);
            report.MainModules.AddRange(new[] { P("main.py"), P("cli", "__main__.py") });
            report.MainFunctionFiles.AddRange(new[] { P("main.py"), P("cli", "__main__.py"), P("tools", "debug.py") });

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            Assert.AreEqual(P("main.py"), plan.CanonicalMainModule);
            Assert.AreEqual(P("main.py"), plan.CanonicalMainFunctionFile);
            Assert.AreEqual(2, plan.FunctionRenames.Count);
            Assert.IsTrue(plan.FunctionRenames.Any(r => r.FilePath == P("cli", "__main__.py")));
            Assert.IsTrue(plan.FunctionRenames.Any(r => r.FilePath == P("tools", "debug.py")));
        }

        [TestMethod]
        public void Plan_NoDuplicateFunctions_ProducesNoFunctionRenames()
        {
            SolutionAuditReport report = new SolutionAuditReport(Root);
            report.MainFunctionFiles.Add(P("main.py"));

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            Assert.AreEqual(0, plan.FunctionRenames.Count);
            Assert.IsFalse(plan.HasAutomatableFixes);
        }

        // ---- Apply (#3d) ----

        [TestMethod]
        public void Apply_RewritesDeclarationAndSameFileCallSite()
        {
            SolutionAuditReport report = new SolutionAuditReport(Root);
            report.MainFunctionFiles.AddRange(new[] { P("main.py"), P("save_pdb.py") });
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);

            FakeFs fs = new FakeFs(P("main.py"), P("save_pdb.py"));
            fs.WriteAllText(P("save_pdb.py"),
                "def main():\n    print('saving')\n\nif __name__ == \"__main__\":\n    main()\n");

            ResolutionApplyResult result = SolutionResolutionPlanner.Apply(plan, fs);

            Assert.IsTrue(result.AllSucceeded, string.Join("; ", result.FunctionsFailed.Select(f => f.Reason)));
            Assert.AreEqual(1, result.FunctionsApplied.Count);
            string rewritten = fs.ReadAllText(P("save_pdb.py"));
            StringAssert.Contains(rewritten, "def save_pdb_main():");
            StringAssert.Contains(rewritten, "save_pdb_main()");
            Assert.IsFalse(rewritten.Contains("def main("), "the declaration must be renamed, not left alongside a new one");
            Assert.IsFalse(rewritten.Contains("\n    main()"), "the call site must be renamed, not left alongside a new one");
            // The canonical file (main.py) was never even read/written.
            Assert.AreEqual(string.Empty, fs.ReadAllText(P("main.py")));
        }

        [TestMethod]
        public void Apply_AvoidsCollidingWithAnExistingNameInThatFile()
        {
            SolutionAuditReport report = new SolutionAuditReport(Root);
            report.MainFunctionFiles.AddRange(new[] { P("main.py"), P("tools.py") });
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);
            Assert.AreEqual("tools_main", plan.FunctionRenames[0].NewName);

            FakeFs fs = new FakeFs(P("main.py"), P("tools.py"));
            // tools.py already has a function called tools_main — the proposed name
            // collides, so Apply must fall back to a numeric suffix instead of
            // silently creating a duplicate-definition bug.
            fs.WriteAllText(P("tools.py"),
                "def tools_main():\n    pass\n\ndef main():\n    tools_main()\n\nmain()\n");

            ResolutionApplyResult result = SolutionResolutionPlanner.Apply(plan, fs);

            Assert.IsTrue(result.AllSucceeded);
            string rewritten = fs.ReadAllText(P("tools.py"));
            StringAssert.Contains(rewritten, "def tools_main_2():");
            Assert.AreEqual("tools_main_2", result.FunctionsApplied[0].NewName);
        }

        [TestMethod]
        public void Apply_MissingFile_IsRecordedAsFailure_NotThrown()
        {
            SolutionAuditReport report = new SolutionAuditReport(Root);
            report.MainFunctionFiles.AddRange(new[] { P("main.py"), P("gone.py") });
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);
            FakeFs fs = new FakeFs(P("main.py")); // gone.py absent

            ResolutionApplyResult result = SolutionResolutionPlanner.Apply(plan, fs);

            Assert.IsFalse(result.AllSucceeded);
            Assert.AreEqual(1, result.FunctionsFailed.Count);
            Assert.AreEqual(0, result.FunctionsApplied.Count);
        }

        // ---- Apply ----

        [TestMethod]
        public void Apply_MovesFiles_WhenTargetsFree()
        {
            SolutionAuditReport report =
                ReportWithMainModules(P("main.py"), P("cli", "__main__.py"));
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);
            FakeFs fs = new FakeFs(P("main.py"), P("cli", "__main__.py"));

            ResolutionApplyResult result = SolutionResolutionPlanner.Apply(plan, fs);

            Assert.IsTrue(result.AllSucceeded);
            Assert.AreEqual(1, result.Applied.Count);
            Assert.IsFalse(fs.FileExists(P("cli", "__main__.py")));
            Assert.IsTrue(fs.FileExists(plan.Renames[0].NewPath));
            Assert.IsTrue(fs.FileExists(P("main.py")), "canonical must be untouched");
        }

        [TestMethod]
        public void Apply_NeverOverwrites_ExistingTarget()
        {
            SolutionAuditReport report =
                ReportWithMainModules(P("main.py"), P("cli", "__main__.py"));
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);
            // Pre-create the exact target the planner will propose.
            FakeFs fs = new FakeFs(P("main.py"), P("cli", "__main__.py"), plan.Renames[0].NewPath);

            ResolutionApplyResult result = SolutionResolutionPlanner.Apply(plan, fs);

            Assert.IsFalse(result.AllSucceeded);
            Assert.AreEqual(1, result.Failed.Count);
            Assert.AreEqual(0, result.Applied.Count);
            Assert.IsTrue(fs.FileExists(P("cli", "__main__.py")), "source must survive a refused move");
        }

        [TestMethod]
        public void Apply_RecordsMissingSource_AsFailure()
        {
            SolutionAuditReport report =
                ReportWithMainModules(P("main.py"), P("cli", "__main__.py"));
            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);
            FakeFs fs = new FakeFs(P("main.py")); // the module to rename is absent

            ResolutionApplyResult result = SolutionResolutionPlanner.Apply(plan, fs);

            Assert.IsFalse(result.AllSucceeded);
            Assert.AreEqual(1, result.Failed.Count);
            Assert.AreEqual(0, fs.Moves.Count);
        }

        [TestMethod]
        public void Apply_NullFs_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(
                () => SolutionResolutionPlanner.Apply(new SolutionResolutionPlan(), null));
        }
    }
}
