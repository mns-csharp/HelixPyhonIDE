using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class BuildErrorNavigationTests
    {
        [TestMethod]
        public void ActivateSelectedBuildError_UsesTaggedPathLineAndColumn()
        {
            string file = Path.Combine(Path.GetTempPath(), "helix-build-nav-" + Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(file, "first line\nsecond target line\nthird line\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();
                    form.PopulateEmbeddedBuildResults(new List<BuildResultItem>
                    {
                        new BuildResultItem
                        {
                            Severity = "error", Code = "CS0001", Message = "broken",
                            File = file, Line = 2, Column = 8
                        }
                    }, false);
                    form.ShowTab("buildResults");
                    Application.DoEvents();
                    form._buildErrorsListView.Focus();
                    form._buildErrorsListView.Items[0].Selected = true;
                    form._buildErrorsListView.Items[0].Focused = true;
                    Application.DoEvents();
                    Assert.AreEqual(1, form._buildErrorsListView.SelectedItems.Count,
                        "precondition: the visible Build Errors row must be selected");

                    var enter = new KeyEventArgs(Keys.Enter);
                    form.OnBuildErrorKeyDown(form._buildErrorsListView, enter);
                    Application.DoEvents();

                    Assert.IsTrue(enter.Handled);
                    Assert.IsTrue(enter.SuppressKeyPress, "Enter activation must not emit the default ListView beep");

                    EditorTabPage opened = null;
                    foreach (TabPage tab in form._editorInnerTabControl.TabPages)
                    {
                        var editorTab = tab as EditorTabPage;
                        if (editorTab != null && string.Equals(editorTab.FilePath, file, StringComparison.OrdinalIgnoreCase))
                        { opened = editorTab; break; }
                    }
                    Assert.IsNotNull(opened, "activating a diagnostic should open its tagged source file");
                    Assert.AreEqual(2, opened.CaretLine);
                    Assert.AreEqual(8, opened.CaretColumn);
                    form.Close();
                }
            }
            finally { try { File.Delete(file); } catch { } }
        }
    }
}
