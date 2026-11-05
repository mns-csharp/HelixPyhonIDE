using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 333. Engine results belong to the solution that produced them, but
    /// NewEmptyWorkspace only cleared the analysis/build panes. Compiler diagnostics, lint and
    /// dead-code hits, metrics, profiles and test results all survived File ▸ Close Solution and
    /// were still on screen under the NEXT solution — rows pointing at files no longer open.
    /// </summary>
    [TestClass]
    public class SolutionClosePanelResetTests
    {
        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        [TestMethod]
        public void ClosingASolution_ClearsEveryEngineResultPanel()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                // Seed a row into the Problems grid, exactly as a compile would.
                var problems = Field<HelixPythonNamespace.App.Controls.ProblemsPanel>(form, "_problemsPanel");
                Assert.IsNotNull(problems);
                problems.ShowDiagnostics(new[]
                {
                    new HelixPythonNamespace.VirtualCompiler.CompileDiagnostic(
                        @"C:\gone\shapes.py", "PY9001",
                        HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error,
                        new HelixPythonNamespace.Python.Text.SourceSpan(
                            new HelixPythonNamespace.Python.Text.SourcePosition(0, 12, 1),
                            new HelixPythonNamespace.Python.Text.SourcePosition(20, 12, 20)),
                        "diagnostic from the solution being closed"),
                });

                ListView grid = problems.Controls.OfType<ListView>().FirstOrDefault();
                Assert.IsNotNull(grid);
                Assert.AreEqual(1, grid.Items.Count, "precondition: the panel holds a result");

                // Close the solution the way File ▸ Close does.
                object coordinator = Field<object>(form, "_solutionCoordinator");
                coordinator.GetType()
                    .GetMethod("NewEmptyWorkspace", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(coordinator, null);

                Assert.AreEqual(0, grid.Items.Count,
                    "results from the closed solution must not remain under the next one");
            }
        }
    }
}
