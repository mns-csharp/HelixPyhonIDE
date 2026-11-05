using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.CodeReview;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Code Review engine wired to the IDE, driven through the menu item a user clicks.
    ///
    /// Deliberately NOT asserted by constructing a report and handing it to the panel: that would
    /// prove the panel renders, which is the easy half. What has to hold is that Analysis ▸ Code
    /// Review ▸ Review Whole Solution, on a real fixture solution opened through
    /// <see cref="MainForm.OpenSolutionAsync(string)"/>, ends with findings in the grid and the tab
    /// on the strip.
    /// </summary>
    [TestClass]
    public class CodeReviewPanelWiringTests
    {
        private static T Field<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name + " field expected on " + target.GetType().Name);
            return (T)field.GetValue(target);
        }

        private static ToolStripMenuItem CodeReviewMenu(MainForm form)
        {
            var analysis = Field<ToolStripMenuItem>(form, "analysisMenuItem");
            Assert.IsNotNull(analysis, "Analysis menu expected");
            foreach (ToolStripItem item in analysis.DropDownItems)
            {
                if ((item.Text ?? string.Empty).Replace("&", string.Empty)
                        .StartsWith("Code Review", StringComparison.OrdinalIgnoreCase))
                    return (ToolStripMenuItem)item;
            }
            Assert.Fail("Analysis ▸ Code Review is missing from the live menu");
            return null;
        }

        private static ToolStripItem Command(MainForm form, string label)
        {
            foreach (ToolStripItem item in CodeReviewMenu(form).DropDownItems)
                if ((item.Text ?? string.Empty).Replace("&", string.Empty)
                        .StartsWith(label, StringComparison.OrdinalIgnoreCase)) return item;
            Assert.Fail("Analysis ▸ Code Review ▸ " + label + " is missing");
            return null;
        }

        /// <summary>
        /// Realize the shell OFF every monitor before driving it.
        ///
        /// Not optional, and both halves were measured: <c>TabPages.Insert</c> is silently dropped
        /// on a TabControl with no window handle, so <c>ShowTab("codeReview")</c> appears to do
        /// nothing; and <c>ListViewItem.Selected = true</c> on a handle-less ListView never raises
        /// SelectedIndexChanged, so the detail pane stays on its placeholder. Neither is a product
        /// bug — in the running IDE the window exists — but a test that skips this asserts against
        /// a shell that was never built.
        /// </summary>
        private static void Realize(MainForm form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            form.Size = new System.Drawing.Size(1200, 800);
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
        }

        private static void OpenAnalysisMenu(MainForm form)
        {
            form.UpdateMenuWorkflowVisibility();
            typeof(MainForm).GetMethod("OnAnalysisMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { form, EventArgs.Empty });
        }

        [TestMethod]
        public void TheCodeReviewSubmenuOffersItsThreeScopesAndAnExport()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                ToolStripMenuItem menu = CodeReviewMenu(form);

                Assert.IsNotNull(Command(form, "Review Whole Solution"));
                Assert.IsNotNull(Command(form, "Review Active File"));
                Assert.IsNotNull(Command(form, "Review Uncommitted Changes"));
                Assert.IsNotNull(Command(form, "Export Last Review"));
                Assert.IsTrue(menu.DropDownItems.Count >= 5, "three scopes, a separator and the export");
            }
        }

        /// <summary>
        /// Same rule as Find Dead Code and Asymptotic Complexity: every review rule reads the Python
        /// AST, so on a C++ solution the command could only ever report nothing.
        /// </summary>
        [TestMethod]
        public void CodeReviewIsOfferedForPythonAndHiddenForCpp()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.OpenSolution(form, ProjectLanguage.Python, cuda: false);
                OpenAnalysisMenu(form);
                Assert.IsTrue(CodeReviewMenu(form).Available, "Python: Code Review must be offered");
            }

            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.OpenSolution(form, ProjectLanguage.Cpp, cuda: false);
                OpenAnalysisMenu(form);
                Assert.IsFalse(CodeReviewMenu(form).Available,
                    "C++: the review rules are bound to the Python AST — the command must not be offered");
            }
        }

        [TestMethod]
        public void TheCodeReviewTabIsRegisteredButHiddenUntilAReviewRuns()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                Assert.IsTrue(form._knownTabs.ContainsKey("codeReview"),
                    "the tab must be registered so View ▸ Panels and ShowTab can find it");
                TabPage tab = form._knownTabs["codeReview"];
                Assert.IsFalse(form._mainTabControl.TabPages.Contains(tab),
                    "an engine tab must not occupy the strip before its engine has produced anything");
                Assert.IsNotNull(form._codeReviewPanel);
                Assert.IsNull(form._codeReviewPanel.LastReport);
            }
        }

        /// <summary>
        /// The end-to-end oracle. Opens a real fixture solution and clicks the real menu item; the
        /// review must reach the panel with findings, and the tab must appear on the strip.
        /// </summary>
        [TestMethod]
        public void ClickingReviewWholeSolutionFillsThePanelAndShowsTheTab()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                Realize(form);
                // A fixture with real defects in it — simple-project is clean, and a review that
                // finds nothing cannot show that the results reached the grid.
                FixtureSolution.OpenSolution(form, FixtureSolution.Path_("python", "python-test-sample-oop"));

                OpenAnalysisMenu(form);
                Command(form, "Review Whole Solution").PerformClick();

                ReviewReport report = WaitForReport(form);
                Assert.IsNotNull(report, "the review never reached the panel");
                Assert.IsTrue(report.Summary.FilesReviewed > 0, "no file was reviewed");
                Assert.IsTrue(report.Findings.Count > 0,
                    "this fixture has real defects; a review of it that finds nothing has not run");

                TabPage tab = form._knownTabs["codeReview"];
                Assert.IsTrue(form._mainTabControl.TabPages.Contains(tab),
                    "the results tab must appear when the engine produces results");

                // The grid, not just the model: a report the panel never rendered is not a result.
                ListView grid = Field<ListView>(form._codeReviewPanel, "_list");
                Assert.AreEqual(report.Findings.Count, grid.Items.Count,
                    "every finding in the report must be a row in the grid");

                // And the verdict strip must state the verdict rather than the placeholder.
                Label verdict = Field<Label>(form._codeReviewPanel, "_verdictLabel");
                Assert.IsTrue(verdict.Text.StartsWith(ReviewMarkdownWriter.VerdictText(report.Verdict),
                    StringComparison.Ordinal), "verdict strip says: " + verdict.Text);

                TestMainForm.PumpUntilIdle();
            }
        }

        /// <summary>
        /// Selecting a row must produce the rationale and the suggested change. A grid row holds
        /// only "what is wrong"; the other two thirds of a review comment live in the detail pane,
        /// and a panel that never fills it has shown a lint list.
        /// </summary>
        [TestMethod]
        public void SelectingAFindingShowsItsRationaleAndSuggestedChange()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                Realize(form);
                FixtureSolution.OpenSolution(form, FixtureSolution.Path_("python", "python-test-sample-oop"));
                OpenAnalysisMenu(form);
                Command(form, "Review Whole Solution").PerformClick();

                ReviewReport report = WaitForReport(form);
                Assert.IsNotNull(report);

                ListView grid = Field<ListView>(form._codeReviewPanel, "_list");
                Assert.IsTrue(grid.Items.Count > 0);
                grid.Items[0].Selected = true;

                TextBox details = Field<TextBox>(form._codeReviewPanel, "_details");
                ReviewFinding first = (ReviewFinding)grid.Items[0].Tag;

                Assert.IsTrue(details.Text.Contains(first.RuleId), "detail pane: " + details.Text);
                Assert.IsTrue(details.Text.Contains("Why it matters"));
                Assert.IsTrue(details.Text.Contains("Suggested change"));
                Assert.IsTrue(details.Text.Contains(first.Suggestion));

                TestMainForm.PumpUntilIdle();
            }
        }

        /// <summary>
        /// A verdict belongs to the solution that was reviewed. Carrying it into the next solution
        /// would be a claim about code the review never read — the same rule the other result
        /// panels follow (PanelCoordinator.DefaultResultTabs).
        /// </summary>
        [TestMethod]
        public void ClosingTheSolutionClearsTheReview()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                Realize(form);
                FixtureSolution.OpenSolution(form, FixtureSolution.Path_("python", "python-test-sample-oop"));
                OpenAnalysisMenu(form);
                Command(form, "Review Whole Solution").PerformClick();
                Assert.IsNotNull(WaitForReport(form));

                // The production teardown, not a direct Clear(): what has to hold is that the
                // solution-close path itself drops the verdict.
                object coordinator = Field<object>(form, "_solutionCoordinator");
                MethodInfo reset = coordinator.GetType().GetMethod("ResetEngineResultPanels",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.IsNotNull(reset, "SolutionCoordinator.ResetEngineResultPanels expected");
                reset.Invoke(coordinator, null);

                Assert.IsNull(form._codeReviewPanel.LastReport,
                    "closing a solution must drop its verdict — carrying it over would be a claim "
                    + "about code the review never read");
                ListView grid = Field<ListView>(form._codeReviewPanel, "_list");
                Assert.AreEqual(0, grid.Items.Count);

                TestMainForm.PumpUntilIdle();
            }
        }

        /// <summary>The panel filters a view over the last report — it must not re-run the engine.</summary>
        [TestMethod]
        public void FilteringNarrowsTheGridWithoutLosingTheReport()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                Realize(form);
                FixtureSolution.OpenSolution(form, FixtureSolution.Path_("python", "python-test-sample-oop"));
                OpenAnalysisMenu(form);
                Command(form, "Review Whole Solution").PerformClick();

                ReviewReport report = WaitForReport(form);
                Assert.IsNotNull(report);

                ListView grid = Field<ListView>(form._codeReviewPanel, "_list");
                int all = grid.Items.Count;

                ComboBox categories = Field<ComboBox>(form._codeReviewPanel, "_categoryFilter");
                categories.SelectedItem = "Correctness";
                Application.DoEvents();

                int correctness = 0;
                foreach (ReviewFinding finding in report.Findings)
                    if (finding.Category == ReviewCategory.Correctness) correctness++;

                Assert.AreEqual(correctness, grid.Items.Count, "the filter must select exactly that category");
                Assert.IsTrue(correctness < all, "this fixture has findings in more than one category");
                Assert.AreSame(report, form._codeReviewPanel.LastReport, "the report itself must survive filtering");

                categories.SelectedIndex = 0;
                Application.DoEvents();
                Assert.AreEqual(all, grid.Items.Count, "clearing the filter restores every row");

                TestMainForm.PumpUntilIdle();
            }
        }

        // The review runs on the shared job runner: background work, UI callback. Pump until the
        // callback has landed rather than sleeping a fixed amount.
        private static ReviewReport WaitForReport(MainForm form)
        {
            for (int i = 0; i < 600; i++)
            {
                Application.DoEvents();
                if (form._codeReviewPanel.LastReport != null) return form._codeReviewPanel.LastReport;
                System.Threading.Thread.Sleep(25);
            }
            return form._codeReviewPanel.LastReport;
        }
    }
}
