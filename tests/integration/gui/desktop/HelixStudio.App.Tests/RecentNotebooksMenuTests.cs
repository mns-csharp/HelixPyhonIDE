using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class RecentNotebooksMenuTests
    {
        private static string NewNotebook(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-recent-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            NotebookWriter.WriteFile(NotebookFactory.CreateNew("3.12"), path);
            return path;
        }

        private static MainForm Shown()
        {
            MainForm form = TestMainForm.CreateComposed();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
            return form;
        }

        private static List<string> RecentPaths(MainForm form)
        {
            return form._recentFilesMenu.DropDownItems.Cast<ToolStripItem>()
                .Where(i => i.Tag is string).Select(i => (string)i.Tag).ToList();
        }

        [TestMethod]
        public void FileMenuHasNoSeparateRecentNotebooksMenu()
        {
            using (MainForm form = Shown())
            {
                ToolStripMenuItem file = form.MainMenuStrip.Items.Cast<ToolStripItem>()
                    .OfType<ToolStripMenuItem>().First(i => i.Text.Replace("&", "") == "File");
                Assert.IsFalse(file.DropDownItems.Cast<ToolStripItem>()
                    .Any(i => i.Text.Replace("&", "").Contains("Recent Notebook")));
            }
        }

        [TestMethod]
        public void OpeningNotebookAddsItToRecentSolutionsAndTheEntryReopensIt()
        {
            string path = NewNotebook("Research.ipynb");
            using (MainForm form = Shown())
            {
                form._recentFilesManager.Clear();
                form.RefreshRecentFilesMenu();
                form.OpenInEditor(path);
                TestMainForm.PumpUntilIdle(iterations: 8, delayMs: 20);

                CollectionAssert.Contains(RecentPaths(form), path);
                ToolStripMenuItem entry = form._recentFilesMenu.DropDownItems.Cast<ToolStripItem>()
                    .OfType<ToolStripMenuItem>().First(i => string.Equals(i.Tag as string, path, StringComparison.OrdinalIgnoreCase));
                form.OnFileCloseDocument(form, EventArgs.Empty);
                entry.PerformClick();
                TestMainForm.PumpUntilIdle(iterations: 8, delayMs: 20);
                Assert.IsInstanceOfType(form._editorInnerTabControl.SelectedTab,
                    typeof(HelixPythonNamespace.Editor.Notebook.NotebookTabPage));
            }
        }

        [TestMethod]
        public void WelcomeRecentNotebookReopensInNotebookEditor()
        {
            string path = NewNotebook("WelcomeResearch.ipynb");
            using (MainForm form = Shown())
            {
                form._recentFilesManager.Clear();
                form.OpenInEditor(path);
                TestMainForm.PumpUntilIdle(iterations: 8, delayMs: 20);
                form.OnFileCloseDocument(form, EventArgs.Empty);

                FieldInfo welcomeField = typeof(MainForm).GetField("_welcomePanel",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(welcomeField);
                object welcomePanel = welcomeField.GetValue(form);
                MethodInfo openRecent = welcomePanel.GetType().GetMethod("OpenRecent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(openRecent, "WelcomePanel recent-item route must remain testable");
                openRecent.Invoke(welcomePanel, new object[] { path });
                TestMainForm.PumpUntilIdle(iterations: 8, delayMs: 20);

                Assert.IsInstanceOfType(form._editorInnerTabControl.SelectedTab,
                    typeof(HelixPythonNamespace.Editor.Notebook.NotebookTabPage));
            }
        }
    }
}
