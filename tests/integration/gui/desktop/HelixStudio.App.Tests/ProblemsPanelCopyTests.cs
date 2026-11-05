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
    /// Iter 431: the Compile panel supports Copy / Copy All (and Ctrl+C) so diagnostics can be shared.
    /// A copied line reads "file(line): severity CODE: message" — the compiler-style form editors and
    /// issue trackers understand.
    /// </summary>
    [TestClass]
    public class ProblemsPanelCopyTests
    {
        private static CompileDiagnostic Diag(ParseSeverity sev, string code, int line, string msg)
        {
            var span = new SourceSpan(new SourcePosition(0, line, 1), new SourcePosition(1, line, 2));
            return new CompileDiagnostic(@"C:\ws\models.py", code, sev, span, msg);
        }

        [TestMethod]
        public void FormatDiagnosticLine_IsCompilerStyle()
        {
            string line = ProblemsPanel.FormatDiagnosticLine(
                Diag(ParseSeverity.Error, "PY6001", 349, "'Sequence' has no attribute 'append'"));
            Assert.AreEqual("models.py(349): error PY6001: 'Sequence' has no attribute 'append'", line);
        }

        [TestMethod]
        public void BuildClipboardText_JoinsRowsWithNewlines()
        {
            string text = ProblemsPanel.BuildClipboardText(new List<CompileDiagnostic>
            {
                Diag(ParseSeverity.Warning, "PY5001", 1, "'os' imported but unused"),
                Diag(ParseSeverity.Error, "PY6005", 5, "bad return"),
            });
            var lines = text.Split('\n');
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains(lines[0], "warning PY5001");
            StringAssert.Contains(lines[1], "error PY6005");
        }

        [TestMethod]
        public void ContextMenu_OffersCopyCopyAllAndClear()
        {
            using (var panel = new ProblemsPanel())
            {
                ListView lv = (ListView)typeof(ProblemsPanel)
                    .GetField("_listView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                ContextMenuStrip menu = lv.ContextMenuStrip;
                Assert.IsNotNull(menu, "the list should have a context menu");
                var texts = new List<string>();
                foreach (ToolStripItem it in menu.Items) texts.Add(it.Text);
                CollectionAssert.Contains(texts, "Copy");
                CollectionAssert.Contains(texts, "Copy All");
                CollectionAssert.Contains(texts, "Clear");
            }
        }
    }
}
