using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Testing;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Regression cover for the Test Runner summary line (iter 208). A completed run
    /// must NOT leave the transient "Running… %" progress text on screen — it lied about
    /// a finished run still being in flight (seen in screenshots/testrunner/106 as
    /// "Running… 100% (3/3)" with all rows already resolved). <see cref="TestRunnerPanel.MarkRunComplete"/>
    /// replaces it with the resting "Done — X passed, Y failed" totals.
    /// </summary>
    [TestClass]
    public class TestRunnerPanelSummaryTests
    {
        private static string SummaryText(TestRunnerPanel panel)
        {
            FieldInfo f = typeof(TestRunnerPanel).GetField(
                "_summary", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_summary field is expected on TestRunnerPanel.");
            return ((Label)f.GetValue(panel)).Text;
        }

        private static ListView ListViewOf(TestRunnerPanel panel)
        {
            FieldInfo f = typeof(TestRunnerPanel).GetField(
                "_listView", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_listView field is expected on TestRunnerPanel.");
            return (ListView)f.GetValue(panel);
        }

        [TestMethod]
        public void TestRow_ShowsDistinguishingShortName_FullIdInTooltip()
        {
            using (var panel = new TestRunnerPanel())
            {
                // Two pytest node ids sharing a long directory prefix (iter 219: the shared prefix
                // used to fill the whole column, so every row read identically as
                // "my_unit_test_package/tests/test_advanced_geomet...").
                panel.UpsertNodes(new[]
                {
                    new TestNode { Id = "1", Display = "my_unit_test_package/tests/test_advanced_geometry.py::TestVec::test_add", Status = TestStatus.Passed },
                    new TestNode { Id = "2", Display = "my_unit_test_package/tests/test_advanced_geometry.py::TestVec::test_sub", Status = TestStatus.Failed },
                });

                var lv = ListViewOf(panel);
                Assert.AreEqual(2, lv.Items.Count);

                string row0 = lv.Items[0].SubItems[1].Text;
                string row1 = lv.Items[1].SubItems[1].Text;
                Assert.AreEqual("test_advanced_geometry.py::TestVec::test_add", row0);
                Assert.AreEqual("test_advanced_geometry.py::TestVec::test_sub", row1);
                Assert.AreNotEqual(row0, row1, "Rows must be visually distinguishable.");

                // The complete node id remains available on hover.
                StringAssert.Contains(lv.Items[0].ToolTipText, "my_unit_test_package/tests/");
            }
        }

        private static TestNode[] OnePassOneFail()
        {
            return new[]
            {
                new TestNode { Id = "AddsTwo", Display = "AddsTwo", Status = TestStatus.Passed, DurationSeconds = 0.004 },
                new TestNode { Id = "Wrong",   Display = "Wrong",   Status = TestStatus.Failed, DurationSeconds = 0.027 },
            };
        }

        [TestMethod]
        public void UpdateSummary_ShowsRunningProgressWhileInFlight()
        {
            using (var panel = new TestRunnerPanel())
            {
                panel.UpdateSummary(50, 1, 2);
                StringAssert.StartsWith(SummaryText(panel), "Running…");
            }
        }

        [TestMethod]
        public void MarkRunComplete_ReplacesRunningWithFinalTotals()
        {
            using (var panel = new TestRunnerPanel())
            {
                panel.UpsertNodes(OnePassOneFail());
                // Simulate a trailing progress event landing after the last test resolved.
                panel.UpdateSummary(100, 2, 2);
                StringAssert.StartsWith(SummaryText(panel), "Running…");

                panel.MarkRunComplete();

                string summary = SummaryText(panel);
                StringAssert.StartsWith(summary, "Done —");
                StringAssert.Contains(summary, "1 passed");
                StringAssert.Contains(summary, "1 failed");
                Assert.IsFalse(summary.Contains("Running"),
                    "A completed run must not still read as 'Running'. Was: " + summary);
            }
        }
    }
}
