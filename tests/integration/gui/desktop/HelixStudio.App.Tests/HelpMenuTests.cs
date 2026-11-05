using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 260: the Help menu only had Welcome + About. It now also offers Documentation and Report
    /// an Issue (both open the GitHub project). Guards that the entries are present and About stays last.
    /// </summary>
    [TestClass]
    public class HelpMenuTests
    {
        [TestMethod]
        public void HelpMenu_HasDocumentationAndReportIssue_WithAboutLast()
        {
            using (var form = new MainForm())
            {
                typeof(MainForm).GetMethod("EnsureHelpMenuItems", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);

                var help = (ToolStripMenuItem)typeof(MainForm)
                    .GetField("helpMenuItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                Assert.IsNotNull(help, "helpMenuItem expected");

                var texts = new List<string>();
                foreach (ToolStripItem it in help.DropDownItems)
                    if (it is ToolStripMenuItem mi) texts.Add((mi.Text ?? "").Replace("&", ""));

                CollectionAssert.Contains(texts, "Documentation");
                CollectionAssert.Contains(texts, "Report an Issue...");
                Assert.AreEqual("About " + MainForm.ProductName + "...", texts[texts.Count - 1],
                    "About should remain the last item in the Help menu");
            }
        }
    }
}
