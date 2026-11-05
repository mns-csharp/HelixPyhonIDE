using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 437: the Compile panel exposes ErrorCount / WarningCount / MessageCount and raises
    /// DiagnosticsChanged, so the host can mirror the build health in the status bar.
    /// </summary>
    [TestClass]
    public class ProblemsPanelCountsTests
    {
        private static CompileDiagnostic Diag(ParseSeverity sev)
        {
            var span = new SourceSpan(new SourcePosition(0, 1, 1), new SourcePosition(1, 1, 2));
            return new CompileDiagnostic(@"C:\ws\m.py", "PY0000", sev, span, "msg");
        }

        [TestMethod]
        public void Counts_MatchSeverities()
        {
            using (var p = new ProblemsPanel())
            {
                p.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    Diag(ParseSeverity.Error), Diag(ParseSeverity.Fatal),
                    Diag(ParseSeverity.Warning),
                    Diag(ParseSeverity.Info),
                });
                Assert.AreEqual(2, p.ErrorCount, "error + fatal count as errors");
                Assert.AreEqual(1, p.WarningCount);
                Assert.AreEqual(1, p.MessageCount);
            }
        }

        [TestMethod]
        public void DiagnosticsChanged_FiresOnShowAndClear()
        {
            using (var p = new ProblemsPanel())
            {
                int fired = 0;
                p.DiagnosticsChanged += delegate { fired++; };
                p.ShowDiagnostics(new List<CompileDiagnostic> { Diag(ParseSeverity.Error) });
                p.Clear();
                Assert.AreEqual(2, fired, "one for Show, one for Clear");
            }
        }

        [TestMethod]
        public void Counts_ZeroWhenCleared()
        {
            using (var p = new ProblemsPanel())
            {
                p.ShowDiagnostics(new List<CompileDiagnostic> { Diag(ParseSeverity.Error) });
                p.Clear();
                Assert.AreEqual(0, p.ErrorCount);
                Assert.AreEqual(0, p.WarningCount);
            }
        }
    }
}
