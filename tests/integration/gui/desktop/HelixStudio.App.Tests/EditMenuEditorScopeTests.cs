using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 318: the Edit menu was scoped to the designer canvas only, so with a file open in the
    /// Editor tab every command (Undo/Redo/Cut/Copy/Paste/Delete) sat greyed out — the menu was dead
    /// exactly where users spend most of their time. Edit commands now target the active text
    /// document while the Editor tab is on screen, and still target the canvas otherwise.
    /// </summary>
    [TestClass]
    public class EditMenuEditorScopeTests
    {
        private static readonly string[] StateDependent =
            { "undoMenuItem", "redoMenuItem", "cutMenuItem", "copyMenuItem", "pasteMenuItem", "deleteMenuItem" };

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static void OpenMenu(MainForm form)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "OnEditMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnEditMenuOpening expected");
            m.Invoke(form, new object[] { form, EventArgs.Empty });
        }

        /// <summary>Opens a real file in the form's editor and brings the Editor tab on screen.</summary>
        private static HelixPythonNamespace.Editor.PythonEditorControl OpenFile(MainForm form, string path)
        {
            form.OpenInEditor(path, -1, true);
            Application.DoEvents();

            var inner = Field<TabControl>(form, "_editorInnerTabControl");
            var editorTab = Field<TabPage>(form, "_editorTab");
            var main = Field<TabControl>(form, "_mainTabControl");
            if (main.TabPages.Contains(editorTab)) main.SelectedTab = editorTab;

            var page = inner.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage;
            if (page == null) return null;
            page.Editor.CreateControl();
            Application.DoEvents();
            return page.Editor;
        }

        [TestMethod]
        public void WithTextSelected_TheEditMenuIsLive_NotGreyedOut()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_edit_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "x = 1\ny = 2\nz = 3\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Size = new System.Drawing.Size(900, 600);
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    var editor = OpenFile(form, tmp);
                    if (editor == null || (editor.Text ?? "").Length == 0)
                    {
                        Assert.Inconclusive("Scintilla did not initialise in this environment.");
                        return;
                    }

                    Assert.AreSame(editor, form.ActiveTextEditor(),
                        "the Edit menu must resolve the on-screen document as its target");

                    editor.SelectionStart = 0;
                    editor.SelectionEnd = 5;
                    Application.DoEvents();

                    var items = new Dictionary<string, ToolStripMenuItem>();
                    foreach (string n in StateDependent)
                    {
                        var it = Field<ToolStripMenuItem>(form, n);
                        it.Enabled = false;      // start from the broken state
                        items[n] = it;
                    }

                    OpenMenu(form);

                    Assert.IsTrue(items["cutMenuItem"].Enabled, "Cut must be available with a text selection");
                    Assert.IsTrue(items["copyMenuItem"].Enabled, "Copy must be available with a text selection");
                    Assert.IsTrue(items["deleteMenuItem"].Enabled, "Delete must be available with a text selection");

                    form.Close();
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [TestMethod]
        public void WithNoSelection_CutCopyDeleteGreyOut_ButTheMenuStaysEditorScoped()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_edit_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "x = 1\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Size = new System.Drawing.Size(900, 600);
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    var editor = OpenFile(form, tmp);
                    if (editor == null || (editor.Text ?? "").Length == 0)
                    {
                        Assert.Inconclusive("Scintilla did not initialise in this environment.");
                        return;
                    }

                    editor.SelectionStart = 0;
                    editor.SelectionEnd = 0;
                    Application.DoEvents();

                    foreach (string n in StateDependent) Field<ToolStripMenuItem>(form, n).Enabled = true;
                    OpenMenu(form);

                    Assert.IsFalse(Field<ToolStripMenuItem>(form, "cutMenuItem").Enabled,
                        "Cut needs a selection");
                    Assert.IsFalse(Field<ToolStripMenuItem>(form, "copyMenuItem").Enabled,
                        "Copy needs a selection");

                    form.Close();
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [TestMethod]
        public void CopyAndCut_ActOnTheDocument_NotTheDiagram()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_edit_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "alpha\nbeta\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Size = new System.Drawing.Size(900, 600);
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    var editor = OpenFile(form, tmp);
                    if (editor == null || (editor.Text ?? "").Length == 0)
                    {
                        Assert.Inconclusive("Scintilla did not initialise in this environment.");
                        return;
                    }

                    editor.SelectionStart = 0;
                    editor.SelectionEnd = 5;              // "alpha"
                    form.OnEditCut(form, EventArgs.Empty);
                    Application.DoEvents();

                    StringAssert.StartsWith(editor.Text, "\n", "Cut should have removed the selected text");
                    Assert.IsFalse((editor.Text ?? "").StartsWith("alpha"));

                    form.OnEditUndo(form, EventArgs.Empty);
                    Application.DoEvents();
                    StringAssert.StartsWith(editor.Text, "alpha", "Undo should restore the document text");

                    form.Close();
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [TestMethod]
        public void WithNoEditorTabOnScreen_TheMenuFallsBackToTheCanvas()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                Assert.IsNull(form.ActiveTextEditor(),
                    "a bare form has no on-screen document, so the canvas must keep the Edit menu");

                foreach (string n in StateDependent) Field<ToolStripMenuItem>(form, n).Enabled = true;
                OpenMenu(form);

                foreach (string n in StateDependent)
                    Assert.IsFalse(Field<ToolStripMenuItem>(form, n).Enabled,
                        n + " should grey out when neither a document nor a canvas selection applies");
            }
        }
    }
}
