using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 319. Two document-level gaps in the File menu / shortcuts:
    ///  * Ctrl+W and Ctrl+F4 closed the whole top-level Editor tab — dismissing the editor and every
    ///    file open in it — instead of the active document.
    ///  * SaveAllModifiedEditorTabs existed but was reachable only from the close-prompt and the
    ///    crash handler; there was no Save All (or Close File) anywhere in the UI.
    /// </summary>
    [TestClass]
    public class FileMenuDocumentCommandsTests
    {
        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static bool SendCmdKey(MainForm form, Keys keys)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "ProcessCmdKey expected");
            object[] args = { new Message(), keys };
            return (bool)m.Invoke(form, args);
        }

        private static MainForm ShowForm()
        {
            var form = TestMainForm.CreateComposed();
            form.StartPosition = FormStartPosition.Manual;
            form.Size = new System.Drawing.Size(900, 600);
            form.ShowInTaskbar = false;
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
            return form;
        }

        private static string TempPy(string body)
        {
            string p = Path.Combine(Path.GetTempPath(), "helix_doc_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(p, body);
            return p;
        }

        private static void SelectEditorTab(MainForm form)
        {
            var editorTab = Field<TabPage>(form, "_editorTab");
            var main = Field<TabControl>(form, "_mainTabControl");
            if (main.TabPages.Contains(editorTab)) main.SelectedTab = editorTab;
            Application.DoEvents();
        }

        [TestMethod]
        public void CtrlW_ClosesTheActiveDocument_NotTheWholeEditorPanel()
        {
            string a = TempPy("a = 1\n"), b = TempPy("b = 2\n");
            try
            {
                using (MainForm form = ShowForm())
                {
                    form.OpenInEditor(a, -1, true);
                    form.OpenInEditor(b, -1, true);
                    Application.DoEvents();
                    SelectEditorTab(form);

                    var inner = Field<TabControl>(form, "_editorInnerTabControl");
                    var main = Field<TabControl>(form, "_mainTabControl");
                    var editorTab = Field<TabPage>(form, "_editorTab");
                    if (inner.TabPages.Count != 2)
                    {
                        Assert.Inconclusive("editor did not open two documents in this environment");
                        return;
                    }

                    bool handled = SendCmdKey(form, Keys.Control | Keys.W);

                    Assert.IsTrue(handled, "Ctrl+W should be consumed");
                    Assert.AreEqual(1, inner.TabPages.Count, "only the active document should close");
                    Assert.IsTrue(main.TabPages.Contains(editorTab),
                        "the Editor panel itself must stay open while documents remain");

                    form.Close();
                }
            }
            finally { try { File.Delete(a); File.Delete(b); } catch { } }
        }

        [TestMethod]
        public void CtrlW_WithNoDocumentOpen_StillClosesTheTopLevelTab()
        {
            using (MainForm form = ShowForm())
            {
                // The Editor is a solution-scoped tab, so with nothing loaded the solution gate
                // takes it off the strip and there is no panel for Ctrl+W to close — which used to
                // make this test bail as Inconclusive and assert nothing at all. Open a solution
                // first: that is also the only state in which a user can hit this case (Editor
                // panel showing, no document inside it).
                typeof(MainForm).GetField("_currentSolution", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();
                Application.DoEvents();

                SelectEditorTab(form);
                var main = Field<TabControl>(form, "_mainTabControl");
                var editorTab = Field<TabPage>(form, "_editorTab");
                Assert.IsTrue(main.TabPages.Contains(editorTab),
                    "with a solution open the Editor tab must be on the strip");
                main.SelectedTab = editorTab;

                SendCmdKey(form, Keys.Control | Keys.W);

                Assert.IsFalse(main.TabPages.Contains(editorTab),
                    "with no document to close, Ctrl+W should still close the panel");
                form.Close();
            }
        }

        [TestMethod]
        public void SaveAll_WritesEveryModifiedDocument()
        {
            string a = TempPy("a = 1\n"), b = TempPy("b = 2\n");
            try
            {
                using (MainForm form = ShowForm())
                {
                    form.OpenInEditor(a, -1, true);
                    form.OpenInEditor(b, -1, true);
                    Application.DoEvents();
                    SelectEditorTab(form);

                    var inner = Field<TabControl>(form, "_editorInnerTabControl");
                    if (inner.TabPages.Count != 2)
                    {
                        Assert.Inconclusive("editor did not open two documents in this environment");
                        return;
                    }

                    foreach (TabPage tab in inner.TabPages)
                    {
                        var etp = (HelixPythonNamespace.Editor.EditorTabPage)tab;
                        etp.Editor.CreateControl();
                        etp.Editor.Text = "# edited\n";
                    }
                    Application.DoEvents();

                    form.OnFileSaveAll(form, EventArgs.Empty);
                    Application.DoEvents();

                    StringAssert.Contains(File.ReadAllText(a), "# edited", "first document not saved");
                    StringAssert.Contains(File.ReadAllText(b), "# edited", "second document not saved");

                    form.Close();
                }
            }
            finally { try { File.Delete(a); File.Delete(b); } catch { } }
        }

        [TestMethod]
        public void SaveAll_WithNothingDirty_SaysSo_AndDoesNotThrow()
        {
            using (MainForm form = ShowForm())
            {
                form.OnFileSaveAll(form, EventArgs.Empty);
                var status = Field<ToolStripStatusLabel>(form, "_statusLabel");
                Assert.AreEqual("No unsaved changes", status.Text);
                form.Close();
            }
        }

        [TestMethod]
        public void ExternalOperationPreflight_WritesDirtyBufferBeforeToolchainReadsDisk()
        {
            string file = TempPy("value = 1\n");
            try
            {
                using (MainForm form = ShowForm())
                {
                    form.OpenInEditor(file, -1, true);
                    Application.DoEvents();
                    var inner = Field<TabControl>(form, "_editorInnerTabControl");
                    var tab = inner.TabPages[0] as HelixPythonNamespace.Editor.EditorTabPage;
                    Assert.IsNotNull(tab);
                    tab.Editor.Text = "value = 2  # unsaved build input\n";
                    Application.DoEvents();
                    Assert.IsTrue(tab.IsModified, "precondition: editor buffer must be dirty");

                    bool ready = form.SaveAllModifiedEditorTabsBeforeExternalOperation("Build");

                    Assert.IsTrue(ready);
                    Assert.IsFalse(tab.IsModified);
                    StringAssert.Contains(File.ReadAllText(file), "unsaved build input",
                        "the external toolchain must see the current editor buffer on disk");
                    form.Close();
                }
            }
            finally { try { File.Delete(file); } catch { } }
        }

        [TestMethod]
        public void ExternalOperationPreflight_CancelsWhenDocumentSaveIsVetoed()
        {
            string file = TempPy("value = 1\n");
            try
            {
                using (MainForm form = ShowForm())
                {
                    form.OpenInEditor(file, -1, true);
                    Application.DoEvents();
                    var inner = Field<TabControl>(form, "_editorInnerTabControl");
                    var tab = inner.TabPages[0] as HelixPythonNamespace.Editor.EditorTabPage;
                    Assert.IsNotNull(tab);
                    tab.Editor.Text = "value = 2  # must not reach disk\n";
                    tab.BeforeSave += (sender, args) => args.Cancel = true;
                    Application.DoEvents();

                    bool ready = form.SaveAllModifiedEditorTabsBeforeExternalOperation("Build");

                    Assert.IsFalse(ready, "a vetoed save must cancel the external operation");
                    Assert.IsTrue(tab.IsModified);
                    Assert.AreEqual("value = 1\n", File.ReadAllText(file));
                    var status = Field<ToolStripStatusLabel>(form, "_statusLabel");
                    StringAssert.Contains(status.Text, "cancelled: could not save");
                    form.Close();
                }
            }
            finally { try { File.Delete(file); } catch { } }
        }

        private static void OpenFileMenu(MainForm form)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "OnFileMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnFileMenuOpening expected");
            m.Invoke(form, new object[] { form, EventArgs.Empty });
        }

        // The File menu is workflow-scoped: commands that cannot apply are hidden, not greyed.
        // NOTE: assert Available, not Visible — ToolStripItem.Visible is false whenever the parent
        // drop-down is not on screen, so it says nothing about the item's own configured state.
        [TestMethod]
        public void WithNothingOpen_TheFileMenuShowsOnlyTheEntryCommands()
        {
            using (MainForm form = ShowForm())
            {
                OpenFileMenu(form);

                foreach (string n in new[] { "openEditorMenuItem", "closeSolutionMenuItem",
                                             "saveLayoutMenuItem", "exportMenuItem",
                                             "saveFileMenuItem", "saveAllFilesMenuItem", "closeFileMenuItem" })
                    Assert.IsFalse(Field<ToolStripMenuItem>(form, n).Available,
                        n + " cannot apply with no solution or document open");

                // The way in must never disappear.
                foreach (string n in new[] { "newProjectWizardMenuItem", "openSolutionMenuItem",
                                             "importDirectoryMenuItem", "exitMenuItem" })
                    Assert.IsTrue(Field<ToolStripMenuItem>(form, n).Available,
                        n + " is an entry point and must always be available");

                form.Close();
            }
        }

        [TestMethod]
        public void WithADocumentOpen_TheDocumentCommandsAppear()
        {
            string a = TempPy("a = 1\n");
            try
            {
                using (MainForm form = ShowForm())
                {
                    form.OpenInEditor(a, -1, true);
                    Application.DoEvents();

                    var inner = Field<TabControl>(form, "_editorInnerTabControl");
                    if (inner.TabPages.Count == 0) { Assert.Inconclusive("editor did not open a document"); return; }

                    OpenFileMenu(form);

                    foreach (string n in new[] { "saveFileMenuItem", "saveAllFilesMenuItem", "closeFileMenuItem" })
                        Assert.IsTrue(Field<ToolStripMenuItem>(form, n).Available,
                            n + " should appear once a document is open");

                    form.Close();
                }
            }
            finally { try { File.Delete(a); } catch { } }
        }

        [TestMethod]
        public void FileMenu_AdvertisesTheDocumentCommands()
        {
            using (var form = new MainForm())
            {
                var saveAll = Field<ToolStripMenuItem>(form, "saveAllFilesMenuItem");
                var closeFile = Field<ToolStripMenuItem>(form, "closeFileMenuItem");
                var exit = Field<ToolStripMenuItem>(form, "exitMenuItem");

                Assert.AreEqual("Save &All", saveAll.Text);
                Assert.AreEqual("Close &File", closeFile.Text);
                Assert.AreEqual("Alt+F4", exit.ShortcutKeyDisplayString,
                    "Exit should advertise the chord Windows handles for it");
            }
        }

        [TestMethod]
        public void AutosaveWhileTyping_DefersHiddenCanvasReconstruction()
        {
            string file = TempPy("class Example:\n    pass\n");
            try
            {
                using (MainForm form = ShowForm())
                {
                    form._currentSolution = new HelixPythonNamespace.Core.Model.SolutionModel(
                        Path.Combine(Path.GetDirectoryName(file), "pyproject.toml"), "Typing");
                    form.OpenInEditor(file, -1, true);
                    Application.DoEvents();

                    object coordinator = Field<object>(form, "_editorCoordinator");
                    MethodInfo saved = coordinator.GetType().GetMethod("OnEditorFileSaved");
                    Assert.IsNotNull(saved);
                    saved.Invoke(coordinator, new object[] { file });

                    FieldInfo pendingField = coordinator.GetType().GetField(
                        "_pendingCanvasRefreshFiles", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(pendingField);
                    object pending = pendingField.GetValue(coordinator);
                    int count = (int)pending.GetType().GetProperty("Count").GetValue(pending, null);
                    Assert.AreEqual(1, count,
                        "an editor autosave must queue the hidden diagram instead of rebuilding it on the typing thread");
                    var status = Field<ToolStripStatusLabel>(form, "_statusLabel");
                    Assert.AreEqual("Saved " + Path.GetFileName(file), status.Text);
                    form.Close();
                }
            }
            finally { try { File.Delete(file); } catch { } }
        }
    }
}
