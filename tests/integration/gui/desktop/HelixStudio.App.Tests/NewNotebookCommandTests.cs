using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.Notebooks;
using HelixPythonNamespace.Workspace.Templates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class NewNotebookCommandTests
    {
        [TestMethod]
        public void FileMenuDoesNotOfferStandaloneNotebookCreation()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                ToolStripMenuItem file = form.MainMenuStrip.Items.Cast<ToolStripItem>()
                    .OfType<ToolStripMenuItem>().First(i => i.Text.Replace("&", "") == "File");
                Assert.IsFalse(file.DropDownItems.Cast<ToolStripItem>()
                    .Any(i => i.Text.Replace("&", "").StartsWith("New Jupyter Notebook")));
            }
        }

        [TestMethod]
        public void NewProjectAndNewSolutionWizardsOfferAValidNotebookSolution()
        {
            ProjectTemplate template = TemplateCatalog.BuiltIn().ById("py.notebook");
            Assert.IsNotNull(template, "the shared project/solution wizard catalog needs a notebook template");

            string parent = NewTempDirectory();
            var options = new GenerationOptions { TargetRoot = parent };
            options.Variables["ProjectName"] = "Research";
            options.Variables["Description"] = "Notebook solution";
            options.Variables["PythonVersion"] = "3.12";
            GenerationResult generated = ProjectGenerator.Generate(template, options);

            Assert.IsTrue(generated.Success, generated.Error);
            string root = Path.Combine(parent, "Research");
            Assert.IsTrue(File.Exists(Path.Combine(root, "pyproject.toml")));
            NotebookReadResult reopened = NotebookReader.ReadFile(Path.Combine(root, "Research.ipynb"));
            Assert.IsTrue(reopened.Succeeded, reopened.Error);
            Assert.AreEqual(1, reopened.Document.Cells.Count);
            Assert.IsTrue(reopened.Document.IsPython);
        }

        [TestMethod]
        public void NotebookTemplateIdentifiesItsGeneratedDocumentForThePostLoadOpen()
        {
            ProjectTemplate template = TemplateCatalog.BuiltIn().ById("py.notebook");
            string parent = NewTempDirectory();
            var options = new GenerationOptions { TargetRoot = parent };
            options.Variables["ProjectName"] = "VisibleNotebook";
            options.Variables["Description"] = "Notebook solution";
            options.Variables["PythonVersion"] = "3.12";
            GenerationResult generated = ProjectGenerator.Generate(template, options);
            Assert.IsTrue(generated.Success, generated.Error);

            string root = Path.Combine(parent, "VisibleNotebook");
            Assert.AreEqual(Path.Combine(root, "VisibleNotebook.ipynb"),
                MainForm.CreatedTemplateDocument(root, "py.notebook"));
            Assert.IsNull(MainForm.CreatedTemplateDocument(root, "py.classlib"));
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "helix-newnb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
