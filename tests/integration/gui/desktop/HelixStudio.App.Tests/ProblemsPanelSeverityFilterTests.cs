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
    /// Iter 429: the Compile panel has Error / Warning / Message filter toggles with live counts (like
    /// Visual Studio's Error List). Toggling a severity off hides its rows; the counts reflect the full
    /// set regardless of what is filtered.
    /// </summary>
    [TestClass]
    public class ProblemsPanelSeverityFilterTests
    {
        private static CompileDiagnostic Diag(ParseSeverity sev)
        {
            var span = new SourceSpan(new SourcePosition(0, 5, 1), new SourcePosition(1, 5, 2));
            return new CompileDiagnostic(@"C:\ws\m.py", "PY0000", sev, span, "msg");
        }

        private static T Field<T>(ProblemsPanel p, string name)
        {
            return (T)typeof(ProblemsPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p);
        }

        private static void Load(ProblemsPanel p)
        {
            p.ShowDiagnostics(new List<CompileDiagnostic>
            {
                Diag(ParseSeverity.Error), Diag(ParseSeverity.Error),
                Diag(ParseSeverity.Warning),
            });
        }

        [TestMethod]
        public void Counts_ReflectTheFullSet()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                StringAssert.Contains(Field<ToolStripButton>(p, "_btnErrors").Text, "2 Errors");
                StringAssert.Contains(Field<ToolStripButton>(p, "_btnWarnings").Text, "1 Warning");
                StringAssert.Contains(Field<ToolStripButton>(p, "_btnMessages").Text, "0 Messages");
            }
        }

        [TestMethod]
        public void TogglingErrorsOff_HidesErrorRows()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                ListView lv = Field<ListView>(p, "_listView");
                Assert.AreEqual(3, lv.Items.Count, "all three shown initially");

                Field<ToolStripButton>(p, "_btnErrors").Checked = false; // raises CheckedChanged -> Repopulate
                Assert.AreEqual(1, lv.Items.Count, "only the warning remains");

                Field<ToolStripButton>(p, "_btnErrors").Checked = true;
                Assert.AreEqual(3, lv.Items.Count, "re-enabling errors restores them");
            }
        }

        [TestMethod]
        public void SingularLabels_WhenExactlyOne()
        {
            using (var p = new ProblemsPanel())
            {
                p.ShowDiagnostics(new List<CompileDiagnostic> { Diag(ParseSeverity.Error) });
                StringAssert.Contains(Field<ToolStripButton>(p, "_btnErrors").Text, "1 Error");
                Assert.IsFalse(Field<ToolStripButton>(p, "_btnErrors").Text.Contains("Errors"), "singular label");
            }
        }
    }
}
