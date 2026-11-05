using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App.Coordinators;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public sealed class WorkbenchPadRegistryTests
    {
        [TestMethod]
        public void ContributedResultPad_UsesDeclaredSolutionLifetime()
        {
            using (var form = TestMainForm.CreateComposed())
            using (var page = new TabPage("Extension Results"))
            {
                var field = typeof(MainForm).GetField("_panelCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
                var coordinator = (PanelCoordinator)field.GetValue(form);
                coordinator.RegisterTopLevelTab("extension.results", page, true, true);

                var descriptor = coordinator.RegisteredPads.Single(x => x.Key == "extension.results");
                Assert.IsTrue(descriptor.SolutionScoped);
                Assert.IsTrue(descriptor.ResultScoped);
                coordinator.ShowTab("extension.results");
                coordinator.HideResultTabs();
                Assert.IsFalse(form._mainTabControl.TabPages.Contains(page));
            }
        }
    }
}
