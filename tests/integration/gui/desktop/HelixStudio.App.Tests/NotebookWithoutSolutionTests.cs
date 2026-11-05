using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// A notebook is how somebody starts, not something a project delivers — so neither creating
    /// one nor opening one may require a solution.
    ///
    /// <para>
    /// Opening was already solution-free but effectively invisible: the Open File in Editor filter
    /// offered <c>.py</c>/<c>.pyi</c> and "All Files", so reaching a notebook meant knowing to
    /// switch to a filter that lists every file on the disk. Creating one was not solution-free at
    /// all — the only route was the Solution Explorer's "Add Jupyter Notebook..." verb on a project
    /// node, and the wizard's notebook template builds an entire solution. A scratch notebook
    /// therefore began by inventing a <c>pyproject.toml</c> to hang it on.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookWithoutSolutionTests
    {
        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        /// <summary>
        /// Realized and shown, because an unshown form composes nothing: menu items report
        /// designer defaults and a click is a no-op, so a test against one proves nothing.
        /// </summary>
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

        private static ToolStripMenuItem FindItem(ToolStripItemCollection items, string text)
        {
            foreach (ToolStripItem item in items)
            {
                var menuItem = item as ToolStripMenuItem;
                if (menuItem == null) continue;
                if (string.Equals(menuItem.Text, text, StringComparison.Ordinal)) return menuItem;
            }
            return null;
        }

        private static ToolStripMenuItem FileMenu(MainForm form)
        {
            Assert.IsNotNull(form.MainMenuStrip, "MainForm should have a menu strip.");
            var file = FindItem(form.MainMenuStrip.Items, "&File");
            Assert.IsNotNull(file, "the File menu should exist");
            return file;
        }

        /// <summary>
        /// The notebook is one of the three things File ▸ New creates. It used to be a fourth
        /// sibling at the top of File; unifying creation moved it one level down, which changes
        /// where it is and not whether it works without a solution — the claim being made here.
        /// </summary>
        [TestMethod]
        public void TheFileMenuOffersANewNotebookCommandWithNoSolutionOpen()
        {
            using (MainForm form = ShowForm())
            {
                Assert.IsNull(Field<HelixPythonNamespace.Core.Model.SolutionModel>(form, "_currentSolution"),
                    "precondition: this test is about the no-solution case");

                var newMenu = FindItem(FileMenu(form).DropDownItems, "&New");
                Assert.IsNotNull(newMenu, "creation is one command that asks what");

                var item = FindItem(newMenu.DropDownItems, "&Jupyter Notebook...");
                Assert.IsNotNull(item,
                    "File must offer a notebook command of its own — the Solution Explorer verb "
                    + "needs a project node, which is the thing a user without a solution has not got.");
                Assert.IsTrue(item.Enabled,
                    "it must be usable with no solution open; that is the entire point of it.");
            }
        }

        /// <summary>
        /// The other half, through the real production route: a notebook written anywhere on disk,
        /// opened by the same <c>OpenInEditor</c> call the menu and the tree both use, with no
        /// solution loaded — and landing on the notebook surface rather than as a wall of JSON.
        /// </summary>
        [TestMethod]
        public void AnExistingNotebookOpensOnItsOwnSurfaceWithNoSolutionOpen()
        {
            string directory = Path.Combine(Path.GetTempPath(), "helix_nb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string notebook = Path.Combine(directory, "scratch.ipynb");
            NotebookWriter.WriteFile(NotebookFactory.CreateNew(null), notebook);

            try
            {
                using (MainForm form = ShowForm())
                {
                    Assert.IsNull(Field<HelixPythonNamespace.Core.Model.SolutionModel>(form, "_currentSolution"),
                        "precondition: no solution is open");

                    form.OpenInEditor(notebook);
                    Application.DoEvents();

                    var inner = Field<TabControl>(form, "_editorInnerTabControl");
                    HelixPythonNamespace.Editor.Notebook.NotebookTabPage opened = null;
                    foreach (TabPage page in inner.TabPages)
                    {
                        var notebookTab = page as HelixPythonNamespace.Editor.Notebook.NotebookTabPage;
                        if (notebookTab != null) opened = notebookTab;
                    }

                    Assert.IsNotNull(opened,
                        "the notebook must open on the notebook surface, with no solution involved.");
                }
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// A notebook written with no project to ask is still a VALID nbformat 4.5 file — the
        /// factory omits <c>language_info.version</c> rather than guessing an interpreter this
        /// notebook has never been run against, which is what Jupyter does when it does not know.
        /// </summary>
        [TestMethod]
        public void ANotebookCreatedWithNoProjectIsStillValidAndCarriesNoInventedVersion()
        {
            string path = Path.Combine(Path.GetTempPath(), "helix_nb_" + Guid.NewGuid().ToString("N") + ".ipynb");
            try
            {
                NotebookWriter.WriteFile(NotebookFactory.CreateNew(null), path);
                string json = File.ReadAllText(path);

                StringAssert.Contains(json, "\"nbformat\": 4");
                StringAssert.Contains(json, "\"cells\"");
                Assert.IsFalse(json.Contains("\"version\": \"\""),
                    "an empty version string is a guess wearing a fact's clothes; the key is omitted instead.");

                NotebookReadResult reread = NotebookReader.ReadFile(path);
                Assert.IsTrue(reread.Succeeded, "what we wrote must read back as a notebook: " + reread.Error);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            }
        }
    }
}
