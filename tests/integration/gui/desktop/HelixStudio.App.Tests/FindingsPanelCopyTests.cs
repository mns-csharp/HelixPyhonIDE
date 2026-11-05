using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.DeadCode;
using HelixPythonNamespace.Lint;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 459 (findings panels polish): the Dead Code and Lint panels now carry the same Copy /
    /// Copy All affordance the Compile panel has, via the shared ListViewClipboard. Copied text is
    /// tab-separated with a header line (glyph-free) so it pastes cleanly into an issue or sheet.
    /// </summary>
    [TestClass]
    public class FindingsPanelCopyTests
    {
        private static ListView List(object panel)
        {
            return (ListView)panel.GetType().GetField("_listView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        // Build the TSV via the same public helper the panels use. It lives in the UI assembly
        // (HelixPythonNamespace.UI.Controls), not App — every results panel across all three GUI assemblies
        // shares it, so reaching for it by name in App's assembly found nothing and NRE'd.
        private static string BuildText(ListView lv, bool selectedOnly)
        {
            return HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(lv, selectedOnly);
        }

        [TestMethod]
        public void DeadCodePanel_HasACopyContextMenu()
        {
            using (var panel = new DeadCodePanel())
            {
                Assert.IsNotNull(List(panel).ContextMenuStrip, "Dead Code panel should have a copy context menu");
                Assert.IsTrue(List(panel).ContextMenuStrip.Items.Count >= 2, "Copy and Copy All");
            }
        }

        [TestMethod]
        public void LintPanel_HasACopyContextMenu()
        {
            using (var panel = new LintResultsPanel())
            {
                Assert.IsNotNull(List(panel).ContextMenuStrip, "Lint panel should have a copy context menu");
            }
        }

        [TestMethod]
        public void CopyAll_ProducesTsvWithHeaderAndRows()
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

                string tsv = BuildText(List(panel), selectedOnly: false);
                string[] lines = tsv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

                StringAssert.Contains(lines[0], "Kind", "first line is the column header");
                StringAssert.Contains(lines[0], "Message");
                StringAssert.Contains(lines[1], "utils.py", "the row carries the file name");
                StringAssert.Contains(lines[1], "helper");
                Assert.IsTrue(lines[1].Contains("\t"), "cells are tab-separated");
            }
        }

        [TestMethod]
        public void CopyHeader_IsStrippedOfTheSortGlyph()
        {
            using (var panel = new LintResultsPanel())
            {
                var span = new SourceSpan(new SourcePosition(0, 1, 1), new SourcePosition(1, 1, 2));
                panel.Show(new List<LintResultsPanel.LintRow>
                {
                    new LintResultsPanel.LintRow(@"C:\ws\a.py", new LintFinding
                    { Code = "L1", Severity = AnalysisSeverity.Error, Message = "m", Span = span }),
                });

                // Sort so the Severity header gets a ▲ glyph, then confirm the copied header omits it.
                SortableListView.SortByColumn(List(panel), 0);
                string tsv = BuildText(List(panel), selectedOnly: false);
                string header = tsv.Replace("\r\n", "\n").Split('\n')[0];

                Assert.IsFalse(header.Contains("▲") || header.Contains("▼"),
                    "the copied header must not carry the sort glyph: " + header);
                StringAssert.Contains(header, "Severity");
            }
        }
    }
}
