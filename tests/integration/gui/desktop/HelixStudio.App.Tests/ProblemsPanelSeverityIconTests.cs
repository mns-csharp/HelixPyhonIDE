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
    /// Iter 427: the Compile (Problems) panel shows a severity glyph per row — a red disc for errors,
    /// an amber triangle for warnings, a blue disc for info — the way a professional Error List reads.
    /// Guards that each row gets the right icon index and the image list is wired up.
    /// </summary>
    [TestClass]
    public class ProblemsPanelSeverityIconTests
    {
        private static CompileDiagnostic Diag(ParseSeverity sev, string code)
        {
            var span = new SourceSpan(new SourcePosition(0, 5, 1), new SourcePosition(1, 5, 2));
            return new CompileDiagnostic(@"C:\ws\m.py", code, sev, span, "msg");
        }

        private static ListView List(ProblemsPanel panel)
        {
            return (ListView)typeof(ProblemsPanel).GetField("_listView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        [TestMethod]
        public void EachRow_GetsSeverityGlyph()
        {
            using (var panel = new ProblemsPanel())
            {
                panel.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    Diag(ParseSeverity.Error, "PY6005"),
                    Diag(ParseSeverity.Warning, "PY5001"),
                    Diag(ParseSeverity.Info, "PY9001"),
                });
                ListView lv = List(panel);
                Assert.IsNotNull(lv.SmallImageList, "the severity image list should be attached");
                Assert.AreEqual(3, lv.SmallImageList.Images.Count, "error/warning/info glyphs");
                Assert.AreEqual(0, lv.Items[0].ImageIndex, "error -> disc glyph");
                Assert.AreEqual(1, lv.Items[1].ImageIndex, "warning -> triangle glyph");
                Assert.AreEqual(2, lv.Items[2].ImageIndex, "info -> info glyph");
            }
        }

        [TestMethod]
        public void FatalUsesErrorGlyph()
        {
            using (var panel = new ProblemsPanel())
            {
                panel.ShowDiagnostics(new List<CompileDiagnostic> { Diag(ParseSeverity.Fatal, "PY1101") });
                Assert.AreEqual(0, List(panel).Items[0].ImageIndex, "fatal shares the error glyph");
            }
        }
    }
}
