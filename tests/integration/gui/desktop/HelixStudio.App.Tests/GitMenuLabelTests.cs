using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 211: the Git "commit all" menu item now opens a commit-message prompt instead of
    /// committing with a hard-coded string, so its label must read as a commit action and carry
    /// the "…" affordance that signals a dialog follows. Asserted at the intent level (contains
    /// "Commit", ends with "...") rather than pinning exact wording.
    /// </summary>
    [TestClass]
    public class GitMenuLabelTests
    {
        [TestMethod]
        public void CommitAllMenuItem_ReadsAsCommitAction_AndSignalsADialog()
        {
            using (var form = new MainForm())
            {
                FieldInfo f = typeof(MainForm).GetField(
                    "persistRepoMenuItem", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(f, "persistRepoMenuItem field expected on MainForm.");
                var item = (ToolStripMenuItem)f.GetValue(form);
                Assert.IsNotNull(item, "persistRepoMenuItem was null.");

                string text = item.Text ?? string.Empty;
                StringAssert.Contains(text, "Commit",
                    "The git commit menu item should read as a commit action. Was: " + text);
                StringAssert.EndsWith(text, "...",
                    "A menu item that opens a dialog should end with an ellipsis. Was: " + text);
            }
        }
    }
}
