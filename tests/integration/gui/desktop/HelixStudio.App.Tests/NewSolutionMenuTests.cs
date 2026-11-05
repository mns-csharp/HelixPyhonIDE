using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// File ▸ "New Solution..." — the explicit always-standalone creation entry.
    /// "New Project..." joins the OPEN solution (by design); this sibling item is
    /// the escape hatch that never does, so users don't nest solution inside
    /// solution just because one was open.
    /// </summary>
    [TestClass]
    public class NewSolutionMenuTests
    {
        /// <summary>
        /// Both creation commands now live under one File ▸ New, alongside the notebook. The claim
        /// this test always made — Solution sits directly under Project, so the escape hatch is
        /// found beside the thing it is an escape from — is unchanged; it is one level deeper.
        /// The old mnemonic note goes with the flat layout: inside New the children are
        /// &amp;Jupyter Notebook / &amp;Project / &amp;Solution, so &amp;S no longer collides with
        /// File's "&amp;Save" and the "Ne&amp;w Solution..." spelling is retired.
        /// </summary>
        [TestMethod]
        public void NewMenu_OffersNewSolution_RightAfterNewProject()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                Assert.IsNotNull(form.newSolutionMenuItem, "designer-built item must exist (live menu)");
                Assert.AreEqual("&Solution...", form.newSolutionMenuItem.Text);

                var newMenu = form.newSolutionMenuItem.OwnerItem as ToolStripMenuItem;
                Assert.IsNotNull(newMenu, "the item lives in a submenu");
                Assert.AreEqual("&New", newMenu.Text,
                    "creation is one command that asks what, not three siblings");

                var fileMenu = newMenu.OwnerItem as ToolStripMenuItem;
                Assert.IsNotNull(fileMenu, "and New itself hangs off a top-level menu");
                Assert.AreEqual("&File", fileMenu.Text);

                var texts = newMenu.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                int newProject = texts.IndexOf("&Project...");
                int newSolution = texts.IndexOf("&Solution...");
                Assert.IsTrue(newProject >= 0 && newSolution == newProject + 1,
                    "Solution... sits directly under Project...; got: " + string.Join(" | ", texts));

                CollectionAssert.Contains(texts, "&Jupyter Notebook...",
                    "the notebook is a creation command like the others, not a stray verb elsewhere");
            }
        }

        /// <summary>
        /// ONE Open, for both things this IDE can open. Two separate Open commands asked the user
        /// to classify their own file before the dialog would show it to them.
        /// </summary>
        [TestMethod]
        public void FileMenu_HasASingleOpenCommand()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var fileMenu = form.newMenuItem.OwnerItem as ToolStripMenuItem;
                Assert.IsNotNull(fileMenu, "New hangs off the File menu");

                var texts = fileMenu.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                CollectionAssert.Contains(texts, "&Open...");
                CollectionAssert.DoesNotContain(texts, "&Open Solution...",
                    "the solution-only Open is gone; got: " + string.Join(" | ", texts));
                Assert.AreEqual(1, texts.Count(t => t == "&Open..."), "exactly one Open");
            }
        }
    }
}
