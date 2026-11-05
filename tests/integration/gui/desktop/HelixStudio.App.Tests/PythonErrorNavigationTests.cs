using System;
using System.IO;
using System.Windows.Forms;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// A failed Python run must land in the Build Errors list as double-clickable rows, so the user
    /// reaches the offending file and line from the error instead of reading the path out of flat
    /// Output text.
    ///
    /// The navigation half already existed (the list's DoubleClick/KeyDown handlers, pinned by
    /// <see cref="BuildErrorNavigationTests"/>); what was missing was anything turning interpreter
    /// output into located rows at all — Run cleared the list and never filled it.
    /// </summary>
    [TestClass]
    public class PythonErrorNavigationTests
    {
        // Shapes captured from a real CPython 3.11 run; {0} is the script path.
        private const string TracebackTemplate =
            "Traceback (most recent call last):\n" +
            "  File \"{0}\", line 7, in <module>\n" +
            "    main()\n" +
            "  File \"{0}\", line 2, in inner\n" +
            "    return open(p)\n" +
            "           ^^^^^^^\n" +
            "FileNotFoundError: [Errno 2] No such file or directory: 'nope.txt'\n";

        // Left on disk deliberately: this suite never deletes a directory a form may still touch.
        private static string TempPyFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_pyerr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "run.py");
            File.WriteAllText(f,
                "def inner(p):\n    return open(p)\n\ndef main():\n    return inner(\"nope.txt\")\n\nmain()\n");
            return f;
        }

        private static BuildResultItem Row(MainForm form, int index)
        {
            return form._buildErrorsListView.Items[index].Tag as BuildResultItem;
        }

        [TestMethod]
        public void FailedRun_PopulatesTheErrorsListWithFileAndLine()
        {
            string script = TempPyFile();
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form.PublishPythonErrors(string.Format(TracebackTemplate, script));
                Application.DoEvents();

                Assert.AreEqual(2, form._buildErrorsListView.Items.Count, "one row per traceback frame");

                BuildResultItem primary = Row(form, 0);
                Assert.IsNotNull(primary);
                Assert.AreEqual("error", primary.Severity);
                Assert.AreEqual("FileNotFoundError", primary.Code);
                StringAssert.Contains(primary.Message, "No such file or directory");
                Assert.AreEqual(script, primary.File);
                Assert.AreEqual(2, primary.Line,
                    "the FIRST row must be the innermost frame — the line the error happened on, "
                    + "not the outermost frame CPython prints first");
                form.Close();
            }
        }

        [TestMethod]
        public void OnlyTheInnermostFrameCountsAsAnError_TheRestAreContext()
        {
            string script = TempPyFile();
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                form.PublishPythonErrors(string.Format(TracebackTemplate, script));
                Application.DoEvents();

                BuildResultItem second = Row(form, 1);
                Assert.AreEqual("info", second.Severity,
                    "N frames are ONE failure — counting each as an error would report "
                    + "'2 errors' for a single exception");
                Assert.AreEqual("frame", second.Code);
                Assert.AreEqual(7, second.Line);
                StringAssert.Contains(second.Message, "<module>");
                form.Close();
            }
        }

        [TestMethod]
        public void SyntaxError_ProducesASingleNavigableRow()
        {
            string script = TempPyFile();
            string output =
                "  File \"" + script + "\", line 2\n" +
                "    111abc\n" +
                "      ^\n" +
                "SyntaxError: invalid decimal literal\n";

            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                form.PublishPythonErrors(output);
                Application.DoEvents();

                Assert.AreEqual(1, form._buildErrorsListView.Items.Count);
                BuildResultItem row = Row(form, 0);
                Assert.AreEqual("SyntaxError", row.Code);
                Assert.AreEqual(script, row.File);
                Assert.AreEqual(2, row.Line);
                Assert.AreEqual(3, row.Column);
                form.Close();
            }
        }

        [TestMethod]
        public void ActivatingAPythonErrorRow_OpensThatFileAtThatLine()
        {
            string script = TempPyFile();
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                form.PublishPythonErrors(string.Format(TracebackTemplate, script));
                form.ShowTab("buildResults");
                Application.DoEvents();

                form._buildErrorsListView.Focus();
                form._buildErrorsListView.Items[0].Selected = true;
                form._buildErrorsListView.Items[0].Focused = true;
                Application.DoEvents();

                // The same handler the list's DoubleClick event invokes.
                var enter = new KeyEventArgs(Keys.Enter);
                form.OnBuildErrorKeyDown(form._buildErrorsListView, enter);
                Application.DoEvents();

                EditorTabPage opened = null;
                foreach (TabPage tab in form._editorInnerTabControl.TabPages)
                {
                    var editorTab = tab as EditorTabPage;
                    if (editorTab != null &&
                        string.Equals(editorTab.FilePath, script, StringComparison.OrdinalIgnoreCase))
                    { opened = editorTab; break; }
                }

                Assert.IsNotNull(opened, "activating the error must open the file it points at");
                Assert.AreEqual(2, opened.CaretLine, "and land on the exact line");
                form.Close();
            }
        }

        [TestMethod]
        public void SuccessfulRunOutput_LeavesTheListEmpty()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                form.PublishPythonErrors("Prepared 7575 sample workspace training rows in out\\rows.tsv\n");
                Application.DoEvents();

                Assert.AreEqual(0, form._buildErrorsListView.Items.Count,
                    "output with no traceback must not manufacture rows");
                form.Close();
            }
        }
    }
}
