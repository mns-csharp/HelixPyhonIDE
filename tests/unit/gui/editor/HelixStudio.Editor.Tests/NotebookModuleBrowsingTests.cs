using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Reading the code behind a registered module — and, when the user asks for it, changing it.
    ///
    /// <para>
    /// Registering a folder answers "can I import this". It says nothing about what the code does,
    /// and a user working through a book needs to open <c>tensor.py</c> and see how <c>device()</c>
    /// is written. The two openings are deliberately different: a library on the import path lives
    /// outside the notebook's folder and usually outside the solution, and this IDE's document tabs
    /// autosave on a 400 ms debounce — so a stray keystroke while reading somebody else's package
    /// would edit it with no prompt and nothing to say no to.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookModuleBrowsingTests
    {
        private string _temp;

        [TestInitialize]
        public void CreateTemp()
        {
            _temp = Path.Combine(Path.GetTempPath(), "helix_browse_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TestCleanup]
        public void RemoveTemp()
        {
            try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>`&lt;temp&gt;\code\cms\tensor.py`, and a notebook beside it in `&lt;temp&gt;\work`.</summary>
        private string MakePackage()
        {
            string package = Path.Combine(_temp, "code", "cms");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "__init__.py"), string.Empty);
            string module = Path.Combine(package, "tensor.py");
            File.WriteAllText(module, "import torch\n\ndef device():\n    return 'cpu'\n");
            return module;
        }

        private NotebookTabPage TabWithNotebook()
        {
            string folder = Path.Combine(_temp, "work");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "MyApp01.ipynb");
            NotebookWriter.WriteFile(NotebookFactory.CreateNew(), path);

            var tab = new NotebookTabPage();
            tab.LoadFile(path);
            return tab;
        }

        /// <summary>
        /// The same tab, on screen.
        ///
        /// <para>
        /// <c>PerformClick</c> is a no-op on an unshown form and an unrealized control reports
        /// designer-default geometry, so a test that drives a button has to put the thing in a
        /// window first — otherwise it goes green having run no product code at all.
        /// </para>
        /// </summary>
        private sealed class Shown : IDisposable
        {
            private readonly Form _form;
            public readonly NotebookTabPage Tab;

            public Shown(NotebookTabPage tab)
            {
                Tab = tab;
                var tabs = new TabControl { Dock = DockStyle.Fill };
                tabs.TabPages.Add(tab);
                _form = new Form { ClientSize = new System.Drawing.Size(1000, 700) };
                _form.Controls.Add(tabs);
                TestWindow.ShowOffScreen(_form);
                Application.DoEvents();
            }

            public void Dispose() { _form.Dispose(); }
        }

        private sealed class OpenCall
        {
            public string Path;
            public bool ReadOnly;
        }

        private static List<OpenCall> Record(NotebookTabPage tab)
        {
            var calls = new List<OpenCall>();
            tab.OpenFileRequested = (path, readOnly) => calls.Add(new OpenCall { Path = path, ReadOnly = readOnly });
            return calls;
        }

        // ------------------------------------------------------------------ which default

        [TestMethod]
        public void AFileInsideARegisteredModuleOpensReadOnlyByDefault()
        {
            string module = MakePackage();
            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFile(module);
                List<OpenCall> calls = Record(tab);

                tab.OpenBrowsedFile(module, null);          // null = a plain double-click

                Assert.AreEqual(1, calls.Count);
                Assert.IsTrue(calls[0].ReadOnly,
                    "somebody else's library must not be one stray keystroke from being edited");
            }
        }

        [TestMethod]
        public void TheUsersOwnFileBesideTheNotebookStillOpensForEditing()
        {
            string module = MakePackage();
            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFile(module);
                string mine = Path.Combine(Path.GetDirectoryName(tab.FilePath), "helpers.py");
                File.WriteAllText(mine, "x = 1\n");

                List<OpenCall> calls = Record(tab);
                tab.OpenBrowsedFile(mine, null);

                Assert.IsFalse(calls[0].ReadOnly,
                    "the read-only default is about whose file it is, not about being in the Files pane");
            }
        }

        [TestMethod]
        public void AskingExplicitlyOverridesEitherDefault()
        {
            string module = MakePackage();
            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFile(module);
                List<OpenCall> calls = Record(tab);

                tab.OpenBrowsedFile(module, false);         // "Open for editing" on a library file
                Assert.IsFalse(calls[0].ReadOnly, "the user asked for it writable and may mean it");

                string mine = Path.Combine(Path.GetDirectoryName(tab.FilePath), "helpers.py");
                File.WriteAllText(mine, "x = 1\n");
                tab.OpenBrowsedFile(mine, true);            // "Open read-only" on their own file
                Assert.IsTrue(calls[1].ReadOnly);
            }
        }

        /// <summary>
        /// The separator in the prefix test is load-bearing: without it a root of `…\code` would
        /// also claim `…\code_backup`, and a neighbouring folder's files would open read-only for
        /// no reason the user could see.
        /// </summary>
        [TestMethod]
        public void ASiblingFolderWithASharedPrefixIsNotPartOfTheModule()
        {
            string module = MakePackage();
            string decoy = Path.Combine(_temp, "code_backup");
            Directory.CreateDirectory(decoy);
            string decoyFile = Path.Combine(decoy, "tensor.py");
            File.WriteAllText(decoyFile, "# a different file entirely\n");

            using (NotebookTabPage tab = TabWithNotebook())
            {
                tab.RegisterModuleFile(module);

                Assert.IsTrue(tab.IsUnderAModuleSearchPath(module));
                Assert.IsFalse(tab.IsUnderAModuleSearchPath(decoyFile),
                    "code_backup is a different folder that merely starts the same way");
            }
        }

        [TestMethod]
        public void WithNoModulesRegisteredEverythingOpensForEditing()
        {
            using (NotebookTabPage tab = TabWithNotebook())
            {
                string mine = Path.Combine(Path.GetDirectoryName(tab.FilePath), "helpers.py");
                File.WriteAllText(mine, "x = 1\n");

                List<OpenCall> calls = Record(tab);
                tab.OpenBrowsedFile(mine, null);

                Assert.IsFalse(calls[0].ReadOnly);
            }
        }

        // ------------------------------------------------------------------ and it really is read-only

        /// <summary>
        /// The mode has to be enforced, not announced. A tab that says "(read-only)" and still
        /// accepts a paste is worse than one that never claimed anything.
        /// </summary>
        [TestMethod]
        public void AReadOnlyTabRefusesEditsAndSaysSoInItsTitle()
        {
            string module = MakePackage();
            using (var tab = new HelixPythonNamespace.Editor.EditorTabPage(module))
            {
                string loaded = tab.Editor.Text;
                Assert.IsTrue(loaded.Contains("def device():"), "the control: the file did load");

                tab.IsReadOnly = true;

                StringAssert.Contains(tab.Text, "(read-only)",
                    "a read-only tab that looks editable just makes typing seem broken");
                Assert.IsTrue(tab.Editor.ReadOnly);

                tab.Editor.InsertText(0, "# vandalism" + Environment.NewLine);
                tab.Editor.ReplaceSelection("more vandalism");
                tab.Editor.DeleteRange(0, 5);

                Assert.AreEqual(loaded, tab.Editor.Text, "the buffer accepted an edit it should have refused");
                Assert.IsFalse(tab.IsModified);
            }
        }

        /// <summary>
        /// The buffer refusing edits is not the same promise as the tab refusing to write. This is
        /// somebody else's library file, and the one outcome that must be impossible is the IDE
        /// modifying it — however the save is asked for.
        /// </summary>
        [TestMethod]
        public void AReadOnlyTabRefusesToSaveOverTheFileItIsReading()
        {
            string module = MakePackage();
            string before = File.ReadAllText(module);

            using (var tab = new HelixPythonNamespace.Editor.EditorTabPage(module))
            {
                tab.IsReadOnly = true;

                Assert.IsFalse(tab.SaveFile(), "a read-only tab reported that it had saved");
                Assert.AreEqual(before, File.ReadAllText(module), "the file on disk was rewritten");
            }
        }

        [TestMethod]
        public void AnEditableTabIsUnaffected()
        {
            string module = MakePackage();
            using (var tab = new HelixPythonNamespace.Editor.EditorTabPage(module))
            {
                string loaded = tab.Editor.Text;
                tab.Editor.InsertText(0, "# a real edit" + Environment.NewLine);

                Assert.AreNotEqual(loaded, tab.Editor.Text,
                    "the control: without read-only the same call must go through");
                Assert.IsFalse(tab.Text.Contains("(read-only)"));
            }
        }

        // ------------------------------------------------------------------ getting there

        /// <summary>
        /// Browsing hands the folder to the Files tree rather than growing a second tree in the
        /// Modules pane — and opens that pane, because a tree the user has to go and find is a
        /// command that appears to have done nothing.
        /// </summary>
        [TestMethod]
        public void BrowsingAModuleShowsItInTheFilesTreeAndOpensThatPane()
        {
            string module = MakePackage();
            using (var shown = new Shown(TabWithNotebook()))
            {
                NotebookTabPage tab = shown.Tab;
            
                tab.RegisterModuleFile(module);

                var modules = tab.Workspace.Pane("modules") as NotebookModulesPanel;
                var files = tab.Workspace.Pane("files") as NotebookFilesPanel;
                Assert.IsNotNull(modules);
                Assert.IsNotNull(files);

                // Open the pane first: a button on a hidden panel does not respond to
                // PerformClick, and this is what a user does anyway.
                tab.Workspace.ShowPane("modules");
                Application.DoEvents();
                modules.Refresh(tab.Document);
                SelectFirstRow(modules);
                Assert.AreEqual(Path.Combine(_temp, "code"), modules.SelectedRoot());

                RaiseBrowse(modules);

                Assert.AreEqual("files", tab.Workspace.OpenPane);
                TreeView tree = FirstTree(files);
                Assert.AreEqual("code", tree.Nodes[0].Text);
                Assert.AreEqual(Path.Combine(_temp, "code"), tree.Nodes[0].Tag as string);

                // And the package inside it is reachable — this is the point of browsing.
                bool sawPackage = false;
                foreach (TreeNode child in tree.Nodes[0].Nodes)
                    if (child.Text == "cms") sawPackage = true;
                Assert.IsTrue(sawPackage, "the registered root should show the package it makes importable");
            }
        }

        [TestMethod]
        public void BrowsingAFolderThatHasGoneSaysSoRatherThanShowingAnEmptyTree()
        {
            string module = MakePackage();
            using (var shown = new Shown(TabWithNotebook()))
            {
                NotebookTabPage tab = shown.Tab;
            
                tab.RegisterModuleFile(module);
                Directory.Delete(Path.Combine(_temp, "code"), true);

                var modules = tab.Workspace.Pane("modules") as NotebookModulesPanel;
                // Open the pane first: a button on a hidden panel does not respond to
                // PerformClick, and this is what a user does anyway.
                tab.Workspace.ShowPane("modules");
                Application.DoEvents();
                modules.Refresh(tab.Document);
                SelectFirstRow(modules);

                string reported = null;
                tab.StatusChanged += delegate (object sender, string message) { reported = message; };
                RaiseBrowse(modules);

                StringAssert.Contains(reported, "not there any more");
            }
        }

        private static void SelectFirstRow(NotebookModulesPanel modules)
        {
            ListView list = FirstList(modules);
            Assert.IsTrue(list.Items.Count > 0, "the pane should be listing the registered root");
            // ListView keeps its selection in the native control, so `Selected = true` is dropped
            // on the floor until a handle exists. Touching Handle forces one without a window.
            IntPtr forced = list.Handle;
            GC.KeepAlive(forced);
            list.Items[0].Selected = true;
        }

        /// <summary>Raise the pane's own Browse, through the button a user clicks.</summary>
        private static void RaiseBrowse(NotebookModulesPanel modules)
        {
            foreach (Button button in FindButtons(modules))
                if (button.Text.StartsWith("Browse", StringComparison.Ordinal)) { button.PerformClick(); return; }
            Assert.Fail("the Modules pane has no Browse button");
        }

        private static IEnumerable<Button> FindButtons(Control parent)
        {
            var found = new List<Button>();
            foreach (Control child in parent.Controls)
            {
                if (child is Button button) found.Add(button);
                found.AddRange(FindButtons(child));
            }
            return found;
        }

        private static ListView FirstList(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is ListView list) return list;
                ListView deeper = FirstList(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static TreeView FirstTree(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is TreeView tree) return tree;
                TreeView deeper = FirstTree(child);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
