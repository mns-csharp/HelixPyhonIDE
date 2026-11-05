using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class EditorMainParityDiagnosticsTests
    {
        [TestMethod]
        public void SavingValidAppMain_RemovesOnlyResolvedProjectPy9001()
        {
            string root = FixtureSolution.CopyToTemp("python", "python-test-sample-oop");
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                FixtureSolution.OpenSolution(form, root);

                string appMain = Path.Combine(root, "app_cli", "main.py");
                string staleInSameProject = Path.Combine(root, "app_cli", "commands.py");
                string unrelatedProject = Path.Combine(root, "app_gui", "main.py");
                EditorTabPage tab = FixtureSolution.OpenFile(form, appMain);
                form._problemsPanel.ShowDiagnostics(new List<CompileDiagnostic>
                {
                    Diagnostic(staleInSameProject),
                    Diagnostic(unrelatedProject)
                });

                tab.Editor.Text = "def my_function():\n    for\n\ndef main():\n    pass\n";
                Assert.IsTrue(tab.SaveFile(), "the editor must save a temporarily incomplete function");
                Assert.IsFalse(form._problemsPanel.Diagnostics.Any(d =>
                    d.Code == "PY9001" && d.FilePath.StartsWith(Path.Combine(root, "app_cli"),
                        System.StringComparison.OrdinalIgnoreCase)),
                    "an incomplete statement must not hide main() and produce a false PY9001");

                tab.Editor.Text = "def my_function():\n    for i in range(3):\n        pass\n\ndef main():\n    pass\n";
                Assert.IsTrue(tab.SaveFile(), "the editor must save the corrected entry point");

                Assert.IsFalse(form._problemsPanel.Diagnostics.Any(d =>
                    d.Code == "PY9001" && d.FilePath.StartsWith(Path.Combine(root, "app_cli"),
                        System.StringComparison.OrdinalIgnoreCase)),
                    "a valid def main() must clear stale PY9001 rows from its project");
                Assert.IsTrue(form._problemsPanel.Diagnostics.Any(d =>
                    d.Code == "PY9001" && d.FilePath == unrelatedProject),
                    "resolving one project must preserve diagnostics from other projects");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        private static CompileDiagnostic Diagnostic(string path)
        {
            var span = new SourceSpan(new SourcePosition(0, 1, 1), new SourcePosition(1, 1, 2));
            return new CompileDiagnostic(path, "PY9001", ParseSeverity.Error, span,
                "An app must have at least one def main() function somewhere in the project.");
        }
    }
}
