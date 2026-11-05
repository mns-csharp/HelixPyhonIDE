using System;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// While a notebook is on screen the shell's menu bar is hidden on purpose
    /// (<c>EditorCoordinator.SyncShellMenusForNotebookMode</c>), so that this Colab-shaped bar is
    /// the only one showing. That makes the notebook's own File menu the ONLY File menu the user
    /// has — and it offered Save, Save a copy and Revert.
    ///
    /// <para>
    /// So a notebook was a dead end. A shell command for "New Jupyter Notebook" is no use to
    /// somebody looking at a notebook, because the menu holding it is not on screen; making a
    /// second notebook meant closing the first one to get the shell menu back. These pin the two
    /// entries that fix it, and that both route somewhere.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookFileMenuRoutesTests
    {
        private static ToolStripMenuItem Find(ToolStripItemCollection items, string text)
        {
            foreach (ToolStripItem item in items)
            {
                var menuItem = item as ToolStripMenuItem;
                if (menuItem == null) continue;
                if (string.Equals(menuItem.Text, text, StringComparison.Ordinal)) return menuItem;
            }
            return null;
        }

        private static ToolStripMenuItem FileMenu(NotebookTabPage tab)
        {
            MenuStrip bar = tab.Workspace.Menu;
            Assert.IsNotNull(bar, "the notebook carries its own menu bar");
            var file = Find(bar.Items, "&File");
            Assert.IsNotNull(file, "it has a File menu");
            return file;
        }

        [TestMethod]
        public void TheNotebooksOwnFileMenuOffersNewAndOpen()
        {
            using (var tab = new NotebookTabPage())
            {
                ToolStripItemCollection items = FileMenu(tab).DropDownItems;

                Assert.IsNotNull(Find(items, "New notebook"),
                    "with the shell menu hidden, this is the only place a new notebook can be asked for.");
                Assert.IsNotNull(Find(items, "Open notebook…"),
                    "and the only place a different one can be opened from.");
            }
        }

        /// <summary>
        /// This menu's own contract is that every entry does something. An unwired callback would
        /// leave two items that look live and are not, which is worse than their absence.
        /// </summary>
        [TestMethod]
        public void BothEntriesInvokeTheHostCallbacks()
        {
            using (var tab = new NotebookTabPage())
            {
                int created = 0, opened = 0;
                tab.NewNotebookRequested = delegate { created++; };
                tab.OpenNotebookRequested = delegate { opened++; };

                ToolStripItemCollection items = FileMenu(tab).DropDownItems;
                Find(items, "New notebook").PerformClick();
                Find(items, "Open notebook…").PerformClick();

                Assert.AreEqual(1, created, "New notebook must reach the shell");
                Assert.AreEqual(1, opened, "Open notebook must reach the shell");
            }
        }

        /// <summary>
        /// With no host wired, the items must SAY so rather than swallow the click. A menu entry
        /// that silently does nothing is the failure mode this menu was written to avoid.
        /// </summary>
        [TestMethod]
        public void WithNoHostWiredTheUserIsToldRatherThanIgnored()
        {
            using (var tab = new NotebookTabPage())
            {
                string status = null;
                tab.StatusChanged += delegate (object sender, string message) { status = message; };

                Find(FileMenu(tab).DropDownItems, "New notebook").PerformClick();

                Assert.IsNotNull(status, "an unroutable command must report itself");
                StringAssert.Contains(status, "not available");
            }
        }
    }
}
