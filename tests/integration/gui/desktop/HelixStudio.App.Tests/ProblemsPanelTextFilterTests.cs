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
    /// Iter 453 (Compile panel polish): a text filter box on the Error List narrows the rows by
    /// message / code / file (case-insensitive), composing with the severity toggles. When it hides
    /// everything the empty-state says so rather than reading "Run Compile".
    /// </summary>
    [TestClass]
    public class ProblemsPanelTextFilterTests
    {
        private static CompileDiagnostic Diag(string file, string code, ParseSeverity sev, string msg)
        {
            var span = new SourceSpan(new SourcePosition(0, 5, 1), new SourcePosition(1, 5, 2));
            return new CompileDiagnostic(file, code, sev, span, msg);
        }

        private static T Field<T>(ProblemsPanel p, string name)
        {
            return (T)typeof(ProblemsPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p);
        }

        private static ToolStripTextBox FilterBox(ProblemsPanel p) => Field<ToolStripTextBox>(p, "_filterText");
        private static ListView List(ProblemsPanel p) => Field<ListView>(p, "_listView");
        private static Label Empty(ProblemsPanel p) => Field<Label>(p, "_emptyLabel");

        private static void Load(ProblemsPanel p)
        {
            p.ShowDiagnostics(new List<CompileDiagnostic>
            {
                Diag(@"C:\ws\models.py", "PY6005", ParseSeverity.Error, "return type 'str' is not assignable to 'None'"),
                Diag(@"C:\ws\views.py", "PY6002", ParseSeverity.Error, "too many positional arguments"),
                Diag(@"C:\ws\models.py", "PY5001", ParseSeverity.Warning, "'os' imported but unused"),
            });
        }

        [TestMethod]
        public void FilterByCode_ShowsOnlyMatchingRows()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                Assert.AreEqual(3, List(p).Items.Count, "all three shown before filtering");

                FilterBox(p).Text = "PY6005";

                Assert.AreEqual(1, List(p).Items.Count, "only the PY6005 row survives the code filter");
                StringAssert.Contains(((CompileDiagnostic)List(p).Items[0].Tag).Code, "PY6005");
            }
        }

        [TestMethod]
        public void FilterByMessage_IsCaseInsensitive()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                FilterBox(p).Text = "POSITIONAL";
                Assert.AreEqual(1, List(p).Items.Count, "message match is case-insensitive");
                StringAssert.Contains(((CompileDiagnostic)List(p).Items[0].Tag).Message, "positional");
            }
        }

        [TestMethod]
        public void FilterByFileName_MatchesLeafName()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                FilterBox(p).Text = "views";
                Assert.AreEqual(1, List(p).Items.Count, "only diagnostics in views.py survive");
            }
        }

        [TestMethod]
        public void FilterComposesWithSeverityToggle()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                // models.py has one error + one warning. Filter to models, then hide warnings.
                FilterBox(p).Text = "models";
                Assert.AreEqual(2, List(p).Items.Count, "both models.py rows match the text");

                Field<ToolStripButton>(p, "_btnWarnings").Checked = false;
                Assert.AreEqual(1, List(p).Items.Count, "the warning is now hidden; only the error remains");
            }
        }

        [TestMethod]
        public void FilterWithNoMatches_ShowsAnHonestEmptyState()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                FilterBox(p).Text = "zzznomatch";

                Assert.AreEqual(0, List(p).Items.Count);
                StringAssert.Contains(Empty(p).Text, "zzznomatch", "the empty state names the filter term");
                Assert.IsFalse(Empty(p).Text.Contains("Run Build"),
                    "a filter hid the rows — the 'Run Compile' message would be misleading");
            }
        }

        [TestMethod]
        public void ClearingTheFilter_RestoresEveryRow()
        {
            using (var p = new ProblemsPanel())
            {
                Load(p);
                FilterBox(p).Text = "PY6005";
                Assert.AreEqual(1, List(p).Items.Count);

                FilterBox(p).Text = string.Empty;
                Assert.AreEqual(3, List(p).Items.Count, "clearing the filter brings every row back");
            }
        }
    }
}
