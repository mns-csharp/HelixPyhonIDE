using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class ProjectPropertiesTests
    {
        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        [TestMethod]
        public void ProjectMenu_PropertiesRaisesSelectedProject()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var project = new ProjectModel(@"C:\ws\app\pyproject.toml", "app", solution);
            solution.Projects.Add(project);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                object selected = null;
                panel.PropertiesRequested += (sender, args) => selected = args.Item;
                TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(projectNode))
                {
                    ToolStripItem item = menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Properties");
                    item.PerformClick();
                }
                Assert.AreSame(project, selected);
            }
        }

        [TestMethod]
        public void DialogProvidesGeneralBuildAndDependencyPages()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var project = new ProjectModel(@"C:\ws\app\pyproject.toml", "app", solution);
            using (var dialog = new ProjectPropertiesDialog(project, new ProjectPropertyActions()))
            {
                CollectionAssert.AreEqual(
                    new[] { "General", "Build & Toolchain", "Dependencies" },
                    dialog.PageTitles.ToArray());
            }
        }
    }
}
