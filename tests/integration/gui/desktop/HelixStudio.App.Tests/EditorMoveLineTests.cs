using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 298: move-line up/down (Alt+Up / Alt+Down, VS Code parity) was missing. Guards that
    /// MoveLinesUp/Down reorder the caret's line.
    ///
    /// Reorders two REAL adjacent statements of a real module. This used to write "aaa\nbbb\nccc"
    /// to %TEMP% and open it in a bare Form: three lines of equal length with no indentation, the
    /// one shape where moving a line cannot go wrong. Real Python lines carry indentation, so a
    /// move that mangles leading whitespace shows up here.
    ///
    /// This test EDITS code, so it runs on a copy of the fixture. Editing the committed fixture in
    /// place was tried and reverted: the IDE's file watcher is live on the open solution's
    /// directory, and rewriting files underneath it destabilised the run.
    /// </summary>
    [TestClass]
    public class EditorMoveLineTests
    {
        [TestMethod]
        public void MoveLinesUp_ThenDown_ReordersAndRestores_InARealModule()
        {
            string root = FixtureSolution.CopyToTemp("python", "python-test-sample-oop");
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                FixtureSolution.OpenSolution(form, root);

                string module = Path.Combine(root, "lib_utils", "geometry.py");
                Assert.IsTrue(File.Exists(module), "fixture module missing: " + module);
                EditorTabPage tab = FixtureSolution.OpenFile(form, module);

                var ed = tab.Editor;
                string original = ed.Text;
                Assert.IsTrue(original.Length > 0, "the fixture module must have loaded");

                // Two adjacent, non-blank, distinct real lines from the module itself.
                int idx = -1;
                for (int i = 1; i < ed.Lines.Count - 2; i++)
                {
                    string a = LineText(ed, i), b = LineText(ed, i + 1);
                    if (a.Trim().Length > 0 && b.Trim().Length > 0 && a != b) { idx = i; break; }
                }
                Assert.IsTrue(idx > 0, "could not find two adjacent distinct lines in " + module);

                string upper = LineText(ed, idx);
                string lower = LineText(ed, idx + 1);

                ed.GotoPosition(ed.Lines[idx + 1].Position);
                ed.MoveLinesUp();

                Assert.AreEqual(lower, LineText(ed, idx),
                    "the moved line should now sit above its old neighbour");
                Assert.AreEqual(upper, LineText(ed, idx + 1),
                    "the neighbour should have shifted down, with its indentation intact");

                ed.MoveLinesDown();
                Assert.AreEqual(original, ed.Text,
                    "up-then-down must restore the module byte for byte, indentation included");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        private static string LineText(PythonEditorControl ed, int index)
        {
            return (ed.Lines[index].Text ?? string.Empty).TrimEnd('\r', '\n');
        }
    }
}
