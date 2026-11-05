using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 455 (Compile panel polish): sorting the Severity column orders rows by severity RANK
    /// (Fatal → Error → Warning → Info), the way a professional Error List groups them — not
    /// alphabetically ("Error" before "Fatal", "Warning" last), which the plain text sort produced.
    /// </summary>
    [TestClass]
    public class ProblemsPanelSeveritySortTests
    {
        private static CompileDiagnostic Diag(ParseSeverity sev, string file)
        {
            var span = new SourceSpan(new SourcePosition(0, 5, 1), new SourcePosition(1, 5, 2));
            return new CompileDiagnostic(file, "PY0000", sev, span, "msg");
        }

        private static ListView List(ProblemsPanel p)
        {
            return (ListView)typeof(ProblemsPanel).GetField("_listView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p);
        }

        [TestMethod]
        public void SortingSeverity_OrdersByRankNotAlphabetically()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(700, 400);
                form.ShowInTaskbar = false;

                var panel = new ProblemsPanel { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                // Intentionally added out of severity order and out of alphabetical order.
                panel.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    Diag(ParseSeverity.Info, "a.py"),
                    Diag(ParseSeverity.Warning, "b.py"),
                    Diag(ParseSeverity.Error, "c.py"),
                    Diag(ParseSeverity.Fatal, "d.py"),
                });

                ListView lv = List(panel);
                SortableListView.SortByColumn(lv, 0); // ascending by severity rank
                Application.DoEvents();

                // Expected rank order: Fatal + Error share rank 0 (glyph 0), then Warning (1), then Info (2).
                var order = new List<string>();
                foreach (ListViewItem it in lv.Items) order.Add(it.SubItems[0].Text);

                Assert.AreEqual(4, order.Count);
                // The first two are the rank-0 severities (Fatal/Error), in some stable order; the
                // key correctness point is Warning precedes Info and both trail the errors.
                int idxError = order.IndexOf("Error");
                int idxFatal = order.IndexOf("Fatal");
                int idxWarning = order.IndexOf("Warning");
                int idxInfo = order.IndexOf("Info");

                Assert.IsTrue(idxError < idxWarning, "Error must sort before Warning");
                Assert.IsTrue(idxFatal < idxWarning, "Fatal must sort before Warning");
                Assert.IsTrue(idxWarning < idxInfo, "Warning must sort before Info (alphabetical would reverse this)");

                form.Close();
            }
        }

        [TestMethod]
        public void SortingSeverityDescending_PutsInfoFirst()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(700, 400);
                form.ShowInTaskbar = false;

                var panel = new ProblemsPanel { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                panel.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    Diag(ParseSeverity.Error, "c.py"),
                    Diag(ParseSeverity.Info, "a.py"),
                    Diag(ParseSeverity.Warning, "b.py"),
                });

                ListView lv = List(panel);
                SortableListView.SortByColumn(lv, 0); // asc
                SortableListView.SortByColumn(lv, 0); // toggle to desc
                Application.DoEvents();

                Assert.AreEqual("Info", lv.Items[0].SubItems[0].Text, "descending rank puts the least-severe row first");
                Assert.AreEqual("Error", lv.Items[lv.Items.Count - 1].SubItems[0].Text, "and the most-severe last");

                form.Close();
            }
        }
    }
}
