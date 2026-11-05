using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 449: Duplicate Line (Ctrl+D, VS parity) duplicates the caret's line when nothing is
    /// selected.
    ///
    /// Duplicates a REAL indented statement of a real module, rather than the middle of an
    /// "aaa/bbb/ccc" scratch file written to %TEMP%. Duplicating an indented line is the case that
    /// can actually break; three unindented lines cannot show it.
    ///
    /// This test EDITS code, so it runs on a copy of the fixture — see EditorMoveLineTests for why
    /// in-place editing of the committed fixture was tried and reverted.
    /// </summary>
    [TestClass]
    public class EditorDuplicateLineTests
    {
        [TestMethod]
        public void DuplicateSelection_DuplicatesTheCaretLine_InARealModule()
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
                Assert.IsTrue((ed.Text ?? string.Empty).Length > 0, "the fixture module must have loaded");

                // A real INDENTED statement — the interesting case for duplication.
                int idx = -1;
                for (int i = 1; i < ed.Lines.Count - 1; i++)
                {
                    string t = LineText(ed, i);
                    if (t.Trim().Length > 0 && (t.StartsWith("    ") || t.StartsWith("\t"))) { idx = i; break; }
                }
                Assert.IsTrue(idx > 0, "could not find an indented line in " + module);

                string target = LineText(ed, idx);
                int before = ed.Lines.Count;

                ed.GotoPosition(ed.Lines[idx].Position);
                ed.DuplicateSelection();

                Assert.AreEqual(before + 1, ed.Lines.Count, "exactly one line should have been added");
                Assert.AreEqual(target, LineText(ed, idx), "the original line is unchanged");
                Assert.AreEqual(target, LineText(ed, idx + 1),
                    "the duplicate sits directly below and keeps the original indentation");
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
