using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 441: F8 / Shift+F8 walk the Compile panel's diagnostics — ActivateAdjacent(+1/-1) moves the
    /// selection (wrapping) and activates it, so the host jumps to each error in turn. No-op when empty.
    /// </summary>
    [TestClass]
    public class ProblemsPanelNavigationTests
    {
        private static CompileDiagnostic Diag(int line)
        {
            var span = new SourceSpan(new SourcePosition(0, line, 1), new SourcePosition(1, line, 2));
            return new CompileDiagnostic(@"C:\ws\m.py", "PY0000", ParseSeverity.Error, span, "line " + line);
        }

        private static ProblemsPanel WithThree(out List<int> activatedLines)
        {
            var panel = new ProblemsPanel();
            var lines = new List<int>();
            panel.DiagnosticActivated += (s, d) => lines.Add(d.Span.Start.Line);
            panel.ShowDiagnostics(new List<CompileDiagnostic> { Diag(10), Diag(20), Diag(30) });
            activatedLines = lines;
            return panel;
        }

        [TestMethod]
        public void Next_WalksForwardThenWraps()
        {
            var panel = WithThree(out var activated);
            using (panel)
            {
                panel.ActivateAdjacent(1);   // -> row 0 (line 10)
                panel.ActivateAdjacent(1);   // -> row 1 (line 20)
                panel.ActivateAdjacent(1);   // -> row 2 (line 30)
                panel.ActivateAdjacent(1);   // wraps -> row 0 (line 10)
                CollectionAssert.AreEqual(new[] { 10, 20, 30, 10 }, activated);
            }
        }

        [TestMethod]
        public void Previous_FromStart_WrapsToLast()
        {
            var panel = WithThree(out var activated);
            using (panel)
            {
                panel.ActivateAdjacent(-1);  // nothing selected -> last (line 30)
                panel.ActivateAdjacent(-1);  // -> line 20
                CollectionAssert.AreEqual(new[] { 30, 20 }, activated);
            }
        }

        [TestMethod]
        public void Empty_IsNoOp()
        {
            using (var panel = new ProblemsPanel())
            {
                panel.ShowDiagnostics(new List<CompileDiagnostic>());
                Assert.IsFalse(panel.ActivateAdjacent(1), "nothing to navigate");
            }
        }
    }
}
