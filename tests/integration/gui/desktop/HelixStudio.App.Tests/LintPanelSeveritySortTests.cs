using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Lint;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 457 (Lint panel polish): the Lint panel's Severity column has no icon, so sorting it used
    /// the cell text — "Error, Info, Warning" alphabetically, putting Info before Warning. It now
    /// ranks the text (Error → Warning → Info), matching the Problems panel.
    /// </summary>
    [TestClass]
    public class LintPanelSeveritySortTests
    {
        private static LintResultsPanel.LintRow Row(AnalysisSeverity sev, string file, int line)
        {
            var span = new SourceSpan(new SourcePosition(0, line, 1), new SourcePosition(1, line, 2));
            return new LintResultsPanel.LintRow(file, new LintFinding
            {
                Code = "LINT0", Severity = sev, Message = "msg", Span = span,
            });
        }

        private static ListView List(LintResultsPanel p)
        {
            return (ListView)typeof(LintResultsPanel).GetField("_listView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p);
        }

        [TestMethod]
        public void SortingSeverity_RanksErrorWarningInfo_NotAlphabetically()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(700, 400);
                form.ShowInTaskbar = false;

                var panel = new LintResultsPanel { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                panel.Show(new List<LintResultsPanel.LintRow>
                {
                    Row(AnalysisSeverity.Info, "a.py", 1),
                    Row(AnalysisSeverity.Warning, "b.py", 2),
                    Row(AnalysisSeverity.Error, "c.py", 3),
                });

                ListView lv = List(panel);
                SortableListView.SortByColumn(lv, 0); // ascending by severity rank
                Application.DoEvents();

                var order = new List<string>();
                foreach (ListViewItem it in lv.Items) order.Add(it.SubItems[0].Text);

                CollectionAssert.AreEqual(new List<string> { "Error", "Warning", "Info" }, order,
                    "severity must rank Error → Warning → Info, not sort alphabetically (Error, Info, Warning)");

                form.Close();
            }
        }
    }
}
