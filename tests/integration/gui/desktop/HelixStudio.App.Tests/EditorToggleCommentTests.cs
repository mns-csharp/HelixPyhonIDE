using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 297: the editor had a ToggleComment() that was never bound to a shortcut and hardcoded
    /// "#" (wrong for CUDA/C++). It is now language-aware and wired to Ctrl+/.
    ///
    /// Both cases run against REAL fixture sources. The prefix check asks each language fixture for
    /// one of its own files instead of naming <c>a.py</c>/<c>a.cu</c> — paths that never
    /// existed. The round-trip comments real code in a real loaded solution, on a copy because it
    /// edits (see EditorMoveLineTests for why in-place editing was reverted).
    /// </summary>
    [TestClass]
    public class EditorToggleCommentTests
    {
        [TestMethod]
        public void LineCommentPrefix_IsLanguageAware()
        {
            AssertPrefix("#", ProjectLanguage.Python, ".py");
            AssertPrefix("//", ProjectLanguage.Cpp, ".cu", cuda: true);

            // The build script of a real C++ fixture, not the bare string "CMakeLists.txt".
            string cmake = Path.Combine(FixtureSolution.DirectoryFor(ProjectLanguage.Cpp, false),
                                        "CMakeLists.txt");
            Assert.IsTrue(File.Exists(cmake), "fixture CMakeLists.txt missing: " + cmake);
            Assert.AreEqual("#", PrefixFor(cmake));
        }

        private static void AssertPrefix(string expected, ProjectLanguage language, string ext,
                                         bool cuda = false)
        {
            string file = FixtureSolution.AnySourceFile(
                FixtureSolution.DirectoryFor(language, cuda), ext);
            Assert.AreEqual(expected, PrefixFor(file), ext + " (" + file + ")");
        }

        private static string PrefixFor(string realFilePath)
        {
            var editor = new PythonEditorControl();
            typeof(PythonEditorControl)
                .GetField("_filePath", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(editor, realFilePath);
            MethodInfo m = typeof(PythonEditorControl)
                .GetMethod("LineCommentPrefix", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "LineCommentPrefix expected");
            string result = (string)m.Invoke(editor, null);
            editor.Dispose();
            return result;
        }

        /// <summary>
        /// Comment out a few REAL consecutive statements of a real module, then put them back — the
        /// edit a user actually makes with Ctrl+/. An earlier version selected the WHOLE module and
        /// commented every line, which is not an edit anyone performs and looks indistinguishable
        /// from defacing the fixture.
        /// </summary>
        [TestMethod]
        public void ToggleComment_CommentsThenUncomments_RealStatementsInALoadedSolution()
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

                int first = -1;
                for (int i = 1; i < ed.Lines.Count - 4; i++)
                {
                    if (IsIndentedCode(ed, i) && IsIndentedCode(ed, i + 1) && IsIndentedCode(ed, i + 2))
                    { first = i; break; }
                }
                Assert.IsTrue(first > 0, "could not find three consecutive indented statements in " + module);
                int last = first + 2;

                string[] targets = { LineText(ed, first), LineText(ed, first + 1), LineText(ed, last) };

                ed.SelectionStart = ed.Lines[first].Position;
                ed.SelectionEnd = ed.Lines[last].Position + ed.Lines[last].Text.Length;
                ed.ToggleComment();

                string[] after = ed.Text.Replace("\r", "").Split('\n');
                Assert.AreEqual(original.Replace("\r", "").Split('\n').Length, after.Length,
                    "commenting must not add or drop lines");
                for (int i = 0; i < 3; i++)
                    StringAssert.Contains(after[first + i], "#",
                        "the selected statement should be commented: " + targets[i]);

                ed.SelectionStart = ed.Lines[first].Position;
                ed.SelectionEnd = ed.Lines[last].Position + ed.Lines[last].Text.Length;
                ed.ToggleComment();

                Assert.AreEqual(original, ed.Text,
                    "a comment/uncomment round-trip must restore the module byte for byte");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        private static bool IsIndentedCode(PythonEditorControl ed, int index)
        {
            string t = LineText(ed, index);
            return t.Trim().Length > 0 && (t.StartsWith("    ") || t.StartsWith("\t"));
        }

        private static string LineText(PythonEditorControl ed, int index)
        {
            return (ed.Lines[index].Text ?? string.Empty).TrimEnd('\r', '\n');
        }
    }
}
