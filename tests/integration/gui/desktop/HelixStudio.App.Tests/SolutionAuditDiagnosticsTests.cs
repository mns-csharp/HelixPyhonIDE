using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.App.Services;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.Workspace.Python;

// Loop 533: WarnRunWillBeBlockedTests lives in this same file/namespace, right below, since it
// exercises the same SolutionAuditPresenter class this file already covers.

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 353. Opening SymPy replaced the load summary with "Loaded with compliance warnings —
    /// see solution-management rules." — a sentence that names no rule, no file and no place to
    /// look, and which had already thrown away the project/type counts the user wanted.
    ///
    /// The findings existed only inside a modal, so dismissing it lost them. They are diagnostics;
    /// they belong in the Problems panel like every other diagnostic in the IDE.
    /// </summary>
    [TestClass]
    public class SolutionAuditDiagnosticsTests
    {
        [TestMethod]
        public void EachOffendingFile_BecomesItsOwnProblemsRow()
        {
            var report = Report(new SolutionAuditFinding(
                SolutionAuditRule.SingleMainModule, SolutionAuditSeverity.Error,
                "More than one main-equivalent module.",
                new[] { @"D:\repos\sympy\bin\main.py", @"D:\repos\sympy\tools\__main__.py" },
                "Keep one entry module."));

            IReadOnlyList<CompileDiagnostic> rows = SolutionAuditPresenter.ToDiagnostics(report);

            CollectionAssert.AreEqual(
                new[] { @"D:\repos\sympy\bin\main.py", @"D:\repos\sympy\tools\__main__.py" },
                rows.Select(r => r.FilePath).ToArray(),
                "a row per file is what makes the violation navigable");
            Assert.IsTrue(rows.All(r => r.Code == "SLN002"), "the rule's stable code");
            Assert.IsTrue(rows.All(r => r.Severity == ParseSeverity.Error));
        }

        [TestMethod]
        public void TheSuggestedResolution_TravelsWithTheMessage()
        {
            var report = Report(new SolutionAuditFinding(
                SolutionAuditRule.SingleMainFunction, SolutionAuditSeverity.Error,
                "More than one module-level def main().",
                new[] { @"D:\repos\sympy\isympy.py" },
                "Keep one main() and call it from the entry module."));

            CompileDiagnostic row = SolutionAuditPresenter.ToDiagnostics(report).Single();

            StringAssert.Contains(row.Message, "Keep one main() and call it from the entry module.",
                "a diagnostic a user cannot act on is only half a diagnostic");
            Assert.AreEqual("SLN003", row.Code);
        }

        /// <summary>
        /// The defect in one assertion: a per-file row talks about THAT file, not "in 10 files".
        /// </summary>
        [TestMethod]
        public void EachRowReadsForItsOwnFile_NotTheAggregate()
        {
            var report = Report(new SolutionAuditFinding(
                SolutionAuditRule.SingleMainFunction, SolutionAuditSeverity.Error,
                "The solution declares a module-level 'def main()' in 3 files; exactly one entry point is allowed.",
                new[] { @"D:\r\a.py", @"D:\r\b.py", @"D:\r\c.py" },
                "Consolidate to one entry point."));

            foreach (CompileDiagnostic row in SolutionAuditPresenter.ToDiagnostics(report))
            {
                StringAssert.Contains(row.Message, "Declares a module-level 'def main()'",
                    "the row leads with what is wrong with this one file");
                StringAssert.Contains(row.Message, "(3 found)", "the aggregate count is kept as context");
                Assert.IsFalse(row.Message.Contains("in 3 files"),
                    "the aggregate 'in N files' phrasing must not appear on a single-file row");
            }
        }

        /// <summary>A rule that passed reports Info with no paths — it is not a problem.</summary>
        [TestMethod]
        public void PassingRules_DoNotAppearInTheList()
        {
            var report = Report(
                new SolutionAuditFinding(SolutionAuditRule.SingleMainModule, SolutionAuditSeverity.Info,
                    "Exactly one main-equivalent module.", new string[0], string.Empty),
                new SolutionAuditFinding(SolutionAuditRule.SingleMainFunction, SolutionAuditSeverity.Error,
                    "More than one module-level def main().", new[] { @"D:\repos\sympy\isympy.py" }, string.Empty));

            IReadOnlyList<CompileDiagnostic> rows = SolutionAuditPresenter.ToDiagnostics(report);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("SLN003", rows[0].Code);
        }

        [TestMethod]
        public void AFindingWithNoPaths_StillGetsARow()
        {
            var report = Report(new SolutionAuditFinding(
                SolutionAuditRule.MainModuleEquivalence, SolutionAuditSeverity.Warning,
                "Entry modules are matched by equivalence.", new string[0], string.Empty));

            CompileDiagnostic row = SolutionAuditPresenter.ToDiagnostics(report).Single();

            Assert.AreEqual(string.Empty, row.FilePath, "no file to blame, but the rule still reports");
            Assert.AreEqual(ParseSeverity.Warning, row.Severity);
            Assert.AreEqual("SLN001", row.Code);
        }

        [TestMethod]
        public void ANullReport_IsNotAnError()
        {
            Assert.AreEqual(0, SolutionAuditPresenter.ToDiagnostics(null).Count);
        }

        [TestMethod]
        public void ViolatedRuleCount_CountsRulesNotFiles()
        {
            var report = Report(
                new SolutionAuditFinding(SolutionAuditRule.SingleMainFunction, SolutionAuditSeverity.Error,
                    "m", new[] { @"D:\r\a.py", @"D:\r\b.py", @"D:\r\c.py" }, ""),
                new SolutionAuditFinding(SolutionAuditRule.SingleMainModule, SolutionAuditSeverity.Info,
                    "ok", new string[0], ""));

            Assert.AreEqual(1, SolutionAuditPresenter.CountViolatedRules(report),
                "three files sharing one broken rule is one issue, not three");
        }

        [TestMethod]
        public void IssuePhrase_SeparatesIssuesFromTheFilesTheyTouch()
        {
            Assert.AreEqual("1 compliance issue (10 files)",
                SolutionCoordinator.ComplianceIssuePhrase(1, 10));
            Assert.AreEqual("2 compliance issues (11 files)",
                SolutionCoordinator.ComplianceIssuePhrase(2, 11));
            Assert.AreEqual("1 compliance issue",
                SolutionCoordinator.ComplianceIssuePhrase(1, 1),
                "no parenthetical when the file count matches the issue count");
        }

        private static SolutionAuditReport Report(params SolutionAuditFinding[] findings)
        {
            var report = new SolutionAuditReport(@"D:\repos\sympy");
            foreach (SolutionAuditFinding f in findings) report.Findings.Add(f);
            return report;
        }
    }

    /// <summary>
    /// Loop 533. WarnRunWillBeBlocked's non-empty-findings branch calls MessageBox.Show and is
    /// therefore untestable here (see winforms-test-hazards memory: never exercise a live
    /// MessageBox path in a test — nobody can click it). Both guard branches below return before
    /// reaching that call, so they're safe to verify directly.
    /// </summary>
    [TestClass]
    public class WarnRunWillBeBlockedTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            System.Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");   // restore the SAFE default, never null
        }

        [TestMethod]
        public void NullFindings_NoOp()
        {
            // Would throw from inside MessageBox.Show if this reached it instead of returning early.
            SolutionAuditPresenter.WarnRunWillBeBlocked(null, null, "Import");
        }

        [TestMethod]
        public void EmptyFindings_NoOp()
        {
            SolutionAuditPresenter.WarnRunWillBeBlocked(
                null, new List<PackageComplianceFinding>(), "Import");
        }

        [TestMethod]
        public void NonInteractive_SkipsTheModal_EvenWithFindings()
        {
            System.Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");
            var findings = new List<PackageComplianceFinding>
            {
                new PackageComplianceFinding(
                    "demo", PackageRule.ExecutableMissingMain, "no def main() found",
                    new List<string> { @"D:\repos\demo\app.py" })
            };

            // A null owner would NRE inside MessageBox.Show; reaching it here would hang or crash,
            // not just fail an assertion, so this is a meaningful check that the guard fired first.
            SolutionAuditPresenter.WarnRunWillBeBlocked(null, findings, "Import");
        }
    }
}
