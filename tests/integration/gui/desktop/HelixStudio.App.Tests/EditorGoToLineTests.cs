using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 301: the editor had GoToLine(int) but no way to invoke it. Ctrl+G now prompts for a line
    /// and jumps there. Guards the parse/jump helper (valid moves the caret; junk/empty is ignored).
    ///
    /// Driven on a REAL fixture solution opened through File ▸ Open Solution, jumping around one of
    /// that solution's own modules. This used to write a five-line scratch file ("one\ntwo\nthree…")
    /// into %TEMP% and open it in a bare Form with no solution behind it — which tested a
    /// TabControl, not the IDE.
    ///
    /// Read-only: it never edits the module, so it opens the committed fixture directly.
    /// </summary>
    [TestClass]
    public class EditorGoToLineTests
    {
        [TestMethod]
        public void ApplyGoToLine_MovesCaret_ForValidNumber_IgnoresJunk()
        {
            string root = FixtureSolution.DirectoryFor(ProjectLanguage.Python, cuda: false);
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                FixtureSolution.OpenSolution(form, root);

                string module = FixtureSolution.AnySourceFile(root, ".py");
                EditorTabPage tab = FixtureSolution.OpenFile(form, module);

                // The EDITOR's line count, not File.ReadAllLines: a file ending in a newline has a
                // trailing empty line the editor counts and ReadAllLines does not, so clamping to
                // the ReadAllLines total would fail the caret for a perfectly legal last line.
                int lines = tab.Editor.Text.Replace("\r", "").Split('\n').Length;
                Assert.IsTrue(lines >= 5, "this probe needs a real module of a few lines; got " + lines);

                Apply(tab, "3");
                Assert.AreEqual(3, tab.CaretLine, "a valid line number should move the caret there");

                Apply(tab, "notanumber");
                Assert.AreEqual(3, tab.CaretLine, "junk input should leave the caret where it was");

                Apply(tab, "");
                Assert.AreEqual(3, tab.CaretLine, "empty input should leave the caret where it was");

                // A number past the end of a REAL file must not throw or land somewhere absurd —
                // a scratch file of five identical lines could never have exercised this.
                Apply(tab, (lines + 500).ToString());
                Assert.IsTrue(tab.CaretLine >= 1 && tab.CaretLine <= lines,
                    "a line past EOF must clamp inside the file, got " + tab.CaretLine +
                    " for a " + lines + "-line module");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        private static void Apply(EditorTabPage tab, string input)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "ApplyGoToLine", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.ApplyGoToLine expected");
            m.Invoke(null, new object[] { tab, input });
        }
    }
}
