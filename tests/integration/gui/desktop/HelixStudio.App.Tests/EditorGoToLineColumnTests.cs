using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 366 (GUI audit of the Compile-panel double-click workflow): double-clicking a diagnostic
    /// jumped to its LINE but parked the caret at column 1, ignoring the diagnostic's column. A
    /// professional IDE lands the caret on the exact flagged token.
    ///
    /// Pinned against a REAL module of a loaded fixture solution, targeting the first real token of
    /// a genuinely indented statement — the shape a diagnostic actually points at. The old version
    /// wrote a three-line "def f(d)" scratch file to %TEMP% and hard-coded line 3 / column 5.
    ///
    /// Read-only: it never edits the module, so it opens the committed fixture directly.
    /// </summary>
    [TestClass]
    public class EditorGoToLineColumnTests
    {
        [TestMethod]
        public void GoToLineColumn_PlacesCaretAtTheGivenColumn_AndClampsOverruns()
        {
            string root = FixtureSolution.DirectoryFor(ProjectLanguage.Python, cuda: false);
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                FixtureSolution.OpenSolution(form, root);

                string module = FixtureSolution.AnySourceFile(root, ".py");
                EditorTabPage tab = FixtureSolution.OpenFile(form, module);
                var ed = tab.Editor;

                // A real indented statement; its first non-space character is where a diagnostic
                // for that statement would point.
                int line = -1, column = -1;
                for (int i = 1; i < ed.Lines.Count - 1; i++)
                {
                    string t = (ed.Lines[i].Text ?? string.Empty).TrimEnd('\r', '\n');
                    if (t.Trim().Length == 0) continue;
                    int indent = t.Length - t.TrimStart(' ', '\t').Length;
                    if (indent > 0) { line = i + 1; column = indent + 1; break; }   // 1-based
                }
                Assert.IsTrue(line > 0, "could not find an indented statement in " + module);

                ed.GoToLineColumn(line, column);
                Assert.AreEqual(line, tab.CaretLine, "caret should be on the diagnostic's line");
                Assert.AreEqual(column, tab.CaretColumn,
                    "caret should sit on the first real token, at column " + column);

                // A column past the line's end clamps onto that line, never spilling to the next.
                ed.GoToLineColumn(line, 999);
                Assert.AreEqual(line, tab.CaretLine, "an over-long column must not jump to the next line");

                // column <= 1 behaves like GoToLine (caret at column 1).
                ed.GoToLineColumn(line, 1);
                Assert.AreEqual(line, tab.CaretLine);
                Assert.AreEqual(1, tab.CaretColumn);
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }
    }
}
