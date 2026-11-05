using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.UI.Projection;
using HelixPythonNamespace.UI.Rendering;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Diagnostic probe: after renaming a type from the Solution Explorer, "Open in Designer" must
    /// still find the renamed type's visual on the canvas — the rename triggers a full
    /// ReloadSolutionAsync, and the projection builder is rebuilt underneath it.
    ///
    /// Runs against the REAL <c>python-test-sample-oop</c> fixture solution, edited in place and
    /// restored by <see cref="FixtureWorkspace"/>.
    ///
    /// On the declaring-file move: SymbolRenameService moves the declaring file only when its stem
    /// matches the old type name with <c>StringComparison.Ordinal</c> — a CASE-SENSITIVE compare.
    /// For Python that essentially never fires, because the convention is a snake_case module
    /// (<c>logger.py</c>) declaring a PascalCase class (<c>Logger</c>), and "logger" != "Logger".
    /// An earlier version of this probe asserted that renaming <c>Logger</c> would rename
    /// <c>logger.py</c> to <c>LoggerRenamed.py</c>; that expectation was invented rather than read
    /// off the implementation, and it failed for the right reason. The rule is pinned below as it is.
    /// </summary>
    [TestClass]
    public class Diag_RenameTypeOpenInDesigner
    {
        private static TypeModel FindType(SolutionModel solution, string name)
        {
            foreach (ProjectModel p in solution.Projects)
            {
                TypeModel t = p.Types.Find(x => x.Name == name);
                if (t != null) return t;
            }
            return null;
        }

        private static void RenameThenOpenInDesigner(string original, string renamedTo)
        {
            using (var fx = FixtureWorkspace.Open("python", "python-test-sample-oop"))
            {
                MainForm form = TestMainForm.CreateComposed();
                try
                {
                    FixtureSolution.RealizeHeadless(form);
                    SolutionModel loaded = FixtureSolution.OpenSolution(form, fx.Root);

                    TypeModel type = FindType(loaded, original);
                    Assert.IsNotNull(type, "the fixture's " + original + " must be in the loaded solution");
                    string declaringFile = type.SourceFilePath;

                    form.OnTreeRename(form, new TreeRenameEventArgs(null, type, renamedTo));
                    TestMainForm.PumpUntilIdle();

                    Assert.IsNotNull(form._currentSolution, "the reload must have left a solution in place");
                    TypeModel renamed = FindType(form._currentSolution, renamedTo);
                    Assert.IsNotNull(renamed, "the reloaded solution must contain the renamed type");

                    // The declaring file keeps its name: a snake_case module stem never matches a
                    // PascalCase class name under an Ordinal compare.
                    Assert.IsTrue(File.Exists(declaringFile),
                        "the declaring module must still exist under its original name: " + declaringFile);

                    Assert.IsNotNull(form._projectionBuilder,
                        "ReloadSolutionAsync must have (re)built a projection builder");

                    form.OnTreeOpenInDesigner(form, new TreeOperationEventArgs(null, renamed));
                    TestMainForm.PumpUntilIdle(iterations: 10, delayMs: 25);

                    NodeVisual visual = form._projectionBuilder.FindVisual(renamed.FullyQualifiedName);
                    Assert.IsNotNull(visual,
                        "Open in Designer must find a visual for the renamed type. Looked up FQN '" +
                        renamed.FullyQualifiedName + "'.");
                }
                finally
                {
                    FixtureSolution.TearDown(form);
                }
            }
        }

        /// <summary>geometry.py's stem does not match Matrix2x3, so no DeclaringFileMove fires.</summary>
        [TestMethod]
        public void RenameType_ThenOpenInDesigner_FindsVisual()
        {
            RenameThenOpenInDesigner("Matrix2x3", "Matrix2x3Renamed");
        }

        /// <summary>
        /// logger.py declares Logger. The stems differ only in case, which the Ordinal compare
        /// treats as no match — so the module is NOT renamed. Pins that deliberate rule.
        /// </summary>
        [TestMethod]
        public void RenameType_WhoseModuleStemDiffersOnlyByCase_DoesNotRenameTheModule()
        {
            RenameThenOpenInDesigner("Logger", "LoggerRenamed");
        }
    }
}
