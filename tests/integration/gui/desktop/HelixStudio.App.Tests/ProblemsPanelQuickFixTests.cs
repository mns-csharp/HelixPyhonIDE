using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Problems panel now OFFERS the resolution for PY8001 instead of only describing it.
    ///
    /// PY8001 ("this file is not part of any project in the solution") is a hard blocker on Run and
    /// Debug, so a user who hits it is stuck until they work out the workspace layout by hand. The
    /// row now carries "Add to Project…" on its context menu and on Ctrl+., and the host performs
    /// the move. Only diagnostics with a real resolution get the affordance — an always-present but
    /// usually-dead menu entry is worse than none.
    /// </summary>
    [TestClass]
    public class ProblemsPanelQuickFixTests
    {
        private static CompileDiagnostic Diag(string code, string file)
        {
            var pos = new SourcePosition(0, 1, 1);
            return new CompileDiagnostic(file, code, ParseSeverity.Error,
                new SourceSpan(pos, pos), code + " message");
        }

        [TestMethod]
        public void OnlyPY8001_IsOfferedAFix()
        {
            Assert.IsTrue(ProblemsPanel.HasQuickFix(
                Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, @"C:\sol\stray.py")));

            Assert.IsFalse(ProblemsPanel.HasQuickFix(
                Diag(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, @"C:\sol\stray.py")),
                "turning module-level defs into classes is a refactor, not a one-click move");
            Assert.IsFalse(ProblemsPanel.HasQuickFix(Diag("PY2101", @"C:\sol\stray.py")));
            Assert.IsFalse(ProblemsPanel.HasQuickFix(
                Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, null)),
                "there is nothing to move without a file path");
            Assert.IsFalse(ProblemsPanel.HasQuickFix(null));
        }

        [TestMethod]
        public void RequestingTheFix_RaisesForTheSelectedPY8001Row()
        {
            using (var panel = new ProblemsPanel())
            {
                var _ = panel.Handle;   // realise, so the ListView has real selection
                CompileDiagnostic stray =
                    Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, @"C:\sol\stray.py");
                panel.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    stray,
                    Diag(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, @"C:\sol\procedural.py"),
                });

                var raised = new List<CompileDiagnostic>();
                panel.QuickFixRequested += (s, d) => raised.Add(d);

                Select(panel, 0);
                Assert.IsTrue(panel.RequestQuickFix(), "the PY8001 row offers the fix");
                Assert.AreEqual(1, raised.Count);
                Assert.AreSame(stray, raised[0], "the host is handed the diagnostic that was selected");

                Select(panel, 1);
                Assert.IsFalse(panel.RequestQuickFix(), "the PY8002 row has no fix to request");
                Assert.AreEqual(1, raised.Count, "and raises nothing");
            }
        }

        [TestMethod]
        public void WithNothingSelected_RequestingTheFixIsANoOp()
        {
            using (var panel = new ProblemsPanel())
            {
                var _ = panel.Handle;
                panel.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, @"C:\sol\stray.py"),
                });
                bool raised = false;
                panel.QuickFixRequested += delegate { raised = true; };

                Assert.IsFalse(panel.RequestQuickFix());
                Assert.IsFalse(raised);
            }
        }

        /// <summary>
        /// Once the fix is applied the blocker is gone, so its row must go with it — a resolved
        /// error still listed as an error reads as "the fix didn't work".
        /// </summary>
        [TestMethod]
        public void AResolvedDiagnostic_LeavesTheListAndTheCounts()
        {
            using (var panel = new ProblemsPanel())
            {
                var _ = panel.Handle;
                CompileDiagnostic fixedOne =
                    Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, @"C:\sol\stray.py");
                CompileDiagnostic other =
                    Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, @"C:\sol\other.py");
                panel.ShowDiagnostics(new List<CompileDiagnostic> { fixedOne, other });
                Assert.AreEqual(2, panel.ErrorCount);

                int changes = 0;
                panel.DiagnosticsChanged += delegate { changes++; };

                Assert.IsTrue(panel.RemoveDiagnostic(fixedOne));
                Assert.AreEqual(1, panel.ErrorCount, "the status bar count follows the list");
                Assert.AreEqual(1, List(panel).Items.Count);
                Assert.AreSame(other, List(panel).Items[0].Tag, "the unresolved one stays");
                Assert.AreEqual(1, changes, "the host is told to refresh its own counters");

                Assert.IsFalse(panel.RemoveDiagnostic(fixedOne), "removing it twice is a no-op");
            }
        }

        /// <summary>The row itself has to say the fix exists — one nobody can find is no fix at all.</summary>
        [TestMethod]
        public void AFixableRow_AdvertisesTheFixInItsTooltip()
        {
            using (var panel = new ProblemsPanel())
            {
                var _ = panel.Handle;
                panel.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, @"C:\sol\stray.py"),
                    Diag("PY2101", @"C:\sol\broken.py"),
                });

                StringAssert.Contains(Row(panel, 0).ToolTipText, ProblemsPanel.QuickFixCaption);
                Assert.AreEqual(@"C:\sol\broken.py", Row(panel, 1).ToolTipText,
                    "a row with no fix keeps the plain path tooltip");
            }
        }

        // ---------------------------------------------------------------- the host side
        //
        // Only the branches that never reach the modal picker are driven here: this suite must not
        // put a dialog on a real desktop with no one to dismiss it (the standing convention for
        // MessageBox-gated actions in HelixPythonNamespace.App.Tests). The move itself, and the proof that it
        // clears PY8001, are covered dialog-free in
        // HelixPythonNamespace.Workspace.IntegrationTests.SolutionMembershipFixerTests.

        [TestMethod]
        public void TheHost_IgnoresADiagnosticWithNoFix()
        {
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                // No solution is open and the code has no fix: both guards must return quietly
                // rather than raise anything or open a picker.
                form.OnProblemQuickFix(null,
                    Diag(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, @"C:\sol\procedural.py"));
                form.OnProblemQuickFix(null, Diag("PY2101", @"C:\sol\broken.py"));
                form.OnProblemQuickFix(null,
                    Diag(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, @"C:\sol\stray.py"));
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        [TestMethod]
        public void TheHost_OffersEveryMemberPackageOfTheOpenSolution()
        {
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                string root = FixtureSolution.Path_("python", "python-test-sample-oop");
                FixtureSolution.OpenSolutionForTree(form, root);
                // The open is asynchronous and this suite shares one host; under contention it can
                // still be in flight here. That is an environment condition, not a defect in what
                // this test is about, so bail out rather than fail on it (observed once at 82s).
                if (form._currentSolution == null)
                    Assert.Inconclusive("the fixture solution did not finish loading in this host");

                MethodInfo m = typeof(MainForm).GetMethod("MembershipTargets",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(m, "MainForm.MembershipTargets expected");
                var targets = (System.Collections.IEnumerable)m.Invoke(
                    form, new object[] { form._currentSolution });

                var paths = new List<string>();
                foreach (object t in targets)
                    paths.Add((string)t.GetType().GetProperty("RelativePath").GetValue(t, null));

                Assert.IsTrue(paths.Count > 0, "the open solution must offer somewhere to put the file");
                CollectionAssert.Contains(paths, "lib_utils",   // flat layout, book §1.6
                    "the picker is built from the solution that is actually open");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        private static ListView List(ProblemsPanel panel)
        {
            FieldInfo f = typeof(ProblemsPanel).GetField("_listView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "ProblemsPanel._listView expected");
            var lv = f.GetValue(panel) as ListView;
            Assert.IsNotNull(lv);
            return lv;
        }

        private static ListViewItem Row(ProblemsPanel panel, int index)
        {
            ListView lv = List(panel);
            Assert.IsTrue(index < lv.Items.Count, "row " + index + " expected");
            return lv.Items[index];
        }

        private static void Select(ProblemsPanel panel, int index)
        {
            ListView lv = List(panel);
            lv.SelectedItems.Clear();
            lv.Items[index].Selected = true;
        }
    }
}
