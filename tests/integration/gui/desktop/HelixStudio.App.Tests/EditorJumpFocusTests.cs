using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Regression for "double-clicked the failed test with line 53, the file popped open but
    /// no blinking cursor on that line": the editor paints its caret only while focused
    /// (RenderState.ShowCaret = Focused), and jumps that start in another panel's ListView
    /// (test rows, Problems, Find hits) left focus in that list. OpenInEditor with
    /// activate=true must land the caret on the requested line AND focus the editor.
    /// </summary>
    [TestClass]
    public class EditorJumpFocusTests
    {
        [TestMethod]
        public void OpenInEditor_WithLine_PlacesCaretAndFocusesTheEditor()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_jumpfocus_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "test_sample.py");
            var lines = new string[20];
            for (int i = 0; i < lines.Length; i++) lines[i] = "x" + i + " = " + i;
            File.WriteAllLines(file, lines);

            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form.OpenInEditor(file, 7);
                    Application.DoEvents();

                    var etp = form._editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage;
                    Assert.IsNotNull(etp, "the file opened in an editor tab");
                    Assert.AreEqual(6, etp.Editor.CurrentLine, "caret is on the requested line (0-based 6 = line 7)");
                    Assert.IsTrue(etp.Editor.ContainsFocus,
                        "the editor must be focused after a line jump — an unfocused editor draws no caret, " +
                        "so the user sees no cursor on the target line");
                    Assert.AreEqual(2, etp.Editor.CaretWidth,
                        "IDE editors use a 2px caret — 1px is easy to lose on a large monitor");

                    form.Close();
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
