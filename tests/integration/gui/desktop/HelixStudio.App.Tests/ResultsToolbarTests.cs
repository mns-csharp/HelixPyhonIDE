using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.DeadCode;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// User request : every results panel — Output, Compile, Tests, Profiler, Metrics,
    /// Analysis, Lint, Dead Code, Complexity, References, Call Hierarchy — carries the same
    /// Clear / Copy / Save-as-txt icon buttons the Terminal panel has.
    /// </summary>
    [TestClass]
    public class ResultsToolbarTests
    {
        private static readonly Type[] PanelTypes =
        {
            typeof(ProblemsPanel), typeof(LintResultsPanel), typeof(DeadCodePanel),
            typeof(MetricsPanel), typeof(ComplexityPanel), typeof(ProfilePanel),
            typeof(TestRunnerPanel), typeof(ReferencesPanel), typeof(CallHierarchyPanel),
        };

        private static ResultsToolbar FindToolbar(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var bar = c as ResultsToolbar;
                if (bar != null) return bar;
                ResultsToolbar nested = FindToolbar(c);
                if (nested != null) return nested;
            }
            return null;
        }

        private static void AssertToolbarIsComplete(ResultsToolbar bar, string who)
        {
            Assert.IsNotNull(bar, who + " must carry a Clear/Copy/Save toolbar");
            Assert.IsNotNull(bar.ClearButton, who + ": Clear button");
            Assert.IsNotNull(bar.CopyButton, who + ": Copy button");
            Assert.IsNotNull(bar.SaveButton, who + ": Save button");
            foreach (Button b in new[] { bar.ClearButton, bar.CopyButton, bar.SaveButton })
            {
                Assert.IsNotNull(b.Image, who + ": every button needs an icon");
                Assert.AreEqual(16, b.Image.Width, who + ": icon width");
                Assert.AreEqual(string.Empty, b.Text, who + ": icon-only, tooltip instead of a caption");
            }
            Assert.AreEqual(DockStyle.Top, bar.Dock, who + ": the strip sits above the content");
        }

        [TestMethod]
        public void EveryResultsPanel_CarriesTheClearCopySaveStrip()
        {
            foreach (Type t in PanelTypes)
            {
                using (var panel = (Control)Activator.CreateInstance(t))
                {
                    AssertToolbarIsComplete(FindToolbar(panel), t.Name);
                }
            }
        }

        /// <summary>
        /// Output and Analysis Results are built inline in MainForm rather than as panel classes,
        /// so they are the two most likely to be missed.
        /// </summary>
        [TestMethod]
        public void OutputAndAnalysisTabs_CarryTheStripToo()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var known = (Dictionary<string, TabPage>)typeof(MainForm)
                    .GetField("_knownTabs", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(form);

                foreach (string key in new[] { "buildResults", "analysis" })
                {
                    TabPage page;
                    Assert.IsTrue(known.TryGetValue(key, out page), key + " tab expected");
                    AssertToolbarIsComplete(FindToolbar(page), key);
                }
            }
        }

        [TestMethod]
        public void ClearButton_EmptiesThePanel()
        {
            using (var panel = new DeadCodePanel())
            {
                var report = new DeadCodeReport();
                report.Findings.Add(new DeadCodeFinding
                {
                    Kind = DeadCodeKind.UnusedFunction,
                    FilePath = @"C:\ws\utils.py",
                    Line = 12,
                    SymbolName = "helper",
                    Message = "function 'helper' is never called",
                });
                panel.Show(report);   // offscreen-guard: a results-panel API, not a window

                ListView grid = panel.Controls.OfType<ListView>().First();
                Assert.AreEqual(1, grid.Items.Count, "precondition: the panel holds a finding");

                FindToolbar(panel).ClearButton.PerformClick();

                Assert.AreEqual(0, grid.Items.Count, "Clear must empty the panel");
            }
        }

        /// <summary>
        /// Copy and Save read the panel's text through the same helper, so an empty panel must be a
        /// no-op rather than a crash or an empty file dialog.
        /// </summary>
        [TestMethod]
        public void CopyOnAnEmptyPanel_IsAHarmlessNoOp()
        {
            using (var panel = new LintResultsPanel())
            {
                FindToolbar(panel).CopyButton.PerformClick();   // must not throw
            }
        }
    }
}
