using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.UI.Projection;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Diagnostic probe for a user-reported regression: after ReloadOpenEditorTabsAfterEdit
    /// (added to fix "renamed class doesn't show in the open editor tab") was introduced, renaming
    /// a class left the editor scrolled to the TOP of the file instead of where the user had been
    /// looking — EditorTabPage.LoadFile always resets the caret to line 1, same as opening the file
    /// fresh, and nothing restored the caret afterward.
    ///
    /// Driven on a COPY of the real <c>python-test-sample-oop</c> fixture (the rename mutates
    /// source, so it must not touch the committed fixture). The target is that fixture's real
    /// <c>Matrix2x3</c> in <c>lib_utils/geometry.py</c>, which sits ~200 lines into a genuine module —
    /// so "the caret went back to line 1" is unambiguous. This used to fabricate a throwaway project
    /// with a <c>shapes.py</c> of thirty "# filler line N" comments, which tested a file no user
    /// will ever have and put a visible IDE window on the developer's desktop.
    /// </summary>
    [TestClass]
    public class Diag_RenameOpenEditorTabKeepsCaretPosition
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            System.Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");
            _root = FixtureSolution.CopyToTemp("python", "python-test-sample-oop");
        }

        [TestCleanup]
        public void Cleanup()
        {
            System.Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");   // restore the SAFE default, never null
            // The copy is deliberately left under %TEMP% — see FixtureSolution.CopyToTemp.
        }

        [TestMethod]
        public void RenameType_WithOpenEditorTab_KeepsCaretNearTheDeclaration_NotAtTopOfFile()
        {
            string modulePath = Path.Combine(_root, "lib_utils", "geometry.py");
            Assert.IsTrue(File.Exists(modulePath), "fixture module missing: " + modulePath);

            // The fixture's own layout decides the line — no hard-coded number to rot.
            int classLine = FixtureSolution.LineOf(modulePath, "class Matrix2x3");
            Assert.IsTrue(classLine > 20,
                "this probe needs a class well below line 1 to prove the caret was restored; got " + classLine);

            var load = new SolutionService().LoadAsync(Path.Combine(_root, "pyproject.toml"));
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);

            TypeModel type = null;
            foreach (ProjectModel p in load.Result.Solution.Projects)
            {
                type = p.Types.Find(t => t.Name == "Matrix2x3");
                if (type != null) break;
            }
            Assert.IsNotNull(type, "the fixture's Matrix2x3 must be found in the loaded solution");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                form._currentSolution = load.Result.Solution;
                form._layoutStore = new JsonLayoutMetadataStore(load.Result.Solution.FilePath);
                form._layoutStore.Load();

                // Open the file and land the caret on the class declaration, the way a user would
                // after navigating there (tree double-click, Go To Definition, …).
                form.OpenInEditor(modulePath, classLine);
                TestMainForm.PumpUntilIdle(iterations: 5, delayMs: 10);

                form.OnTreeRename(form, new TreeRenameEventArgs(null, type, "Matrix2x3Renamed"));
                TestMainForm.PumpUntilIdle();

                string fullPath = Path.GetFullPath(modulePath);
                Assert.IsTrue(form._openEditorTabs.ContainsKey(fullPath),
                    "the tab must still be open under the same path (no file move for this rename)");
                var tab = form._openEditorTabs[fullPath] as HelixPythonNamespace.Editor.EditorTabPage;
                Assert.IsNotNull(tab, "the open tab must be an EditorTabPage");

                StringAssert.Contains(tab.Editor.Text, "class Matrix2x3Renamed",
                    "the reloaded buffer must show the renamed class");
                Assert.AreNotEqual(1, tab.CaretLine,
                    "the caret must not have been reset to the top of the file after the rename-triggered reload");
                Assert.AreEqual(classLine, tab.CaretLine,
                    "the caret should land back on the class declaration line, same as before the rename");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }
    }
}
