using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.UI.Tests
{
    /// <summary>
    /// File commands registered on the Solution Explorer (spec 001: an add-in's
    /// <c>RegisterExplorerFileCommand</c>). Shown on a file node only where they apply, run with
    /// the node's path, and a command whose predicate throws cannot break the menu.
    /// </summary>
    [TestClass]
    public class ExplorerFileCommandTests
    {
        private static string WriteTemp(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-filecmd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, "x = 1\r\n");
            return path;
        }

        private static ContextMenuStrip MenuFor(ProjectTreePanel panel, string path)
        {
            var solution = new SolutionModel(path, "fixture");
            var project = new ProjectModel(path, "pkg", solution);
            var node = new TreeNode(Path.GetFileName(path))
            {
                Tag = new ProjectTreePanel.FileGroup { Project = project, FilePath = path },
            };
            return panel.BuildContextMenuForNode(node);
        }

        private static ToolStripItem Find(ContextMenuStrip menu, string text)
        {
            return menu.Items.OfType<ToolStripItem>().FirstOrDefault(i => i.Text == text);
        }

        [TestMethod]
        public void ACommand_AppearsWhereItApplies_AndRunsWithThePath()
        {
            string py = WriteTemp("a.py");
            string txt = WriteTemp("a.txt");
            using (var panel = new ProjectTreePanel())
            {
                string ran = null;
                var command = new ProjectTreePanel.FileCommand
                {
                    Caption = "Do Thing",
                    AppliesTo = p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase),
                    Run = p => ran = p,
                };
                panel.AddFileCommand(command);
                panel.AddFileCommand(command);   // idempotent

                using (ContextMenuStrip menu = MenuFor(panel, txt))
                    Assert.IsNull(Find(menu, "Do Thing"));

                using (ContextMenuStrip menu = MenuFor(panel, py))
                {
                    Assert.AreEqual(1, menu.Items.OfType<ToolStripItem>().Count(i => i.Text == "Do Thing"));
                    Find(menu, "Do Thing").PerformClick();
                }
                Assert.AreEqual(py, ran);

                panel.RemoveFileCommand(command);
                using (ContextMenuStrip menu = MenuFor(panel, py))
                    Assert.IsNull(Find(menu, "Do Thing"), "a removed command is gone");
            }
        }

        [TestMethod]
        public void ACommandWhosePredicateThrows_IsLeftOut_AndTheMenuStillOpens()
        {
            string py = WriteTemp("b.py");
            using (var panel = new ProjectTreePanel())
            {
                panel.AddFileCommand(new ProjectTreePanel.FileCommand
                {
                    Caption = "Broken",
                    AppliesTo = p => { throw new InvalidOperationException("bad add-in"); },
                });
                using (ContextMenuStrip menu = MenuFor(panel, py))
                {
                    Assert.IsNull(Find(menu, "Broken"));
                    Assert.IsNotNull(Find(menu, "Open"), "the built-in verbs are still there");
                    Assert.IsNotNull(Find(menu, "Open in Class Designer Canvas"), "and the other built-in verb");
                }
            }
        }

        /// <summary>A command that applies but cannot run on this file is shown disabled with the
        /// reason as its tooltip, rather than hidden.</summary>
        [TestMethod]
        public void ACommandWithADisabledReason_IsShownDisabled_WithTheReason()
        {
            string py = WriteTemp("c.py");
            string other = WriteTemp("d.py");
            using (var panel = new ProjectTreePanel())
            {
                panel.AddFileCommand(new ProjectTreePanel.FileCommand
                {
                    Caption = "Picky",
                    AppliesTo = p => true,
                    DisabledReason = p => p == py ? "This file defines no widget." : null,
                });
                using (ContextMenuStrip menu = MenuFor(panel, py))
                {
                    ToolStripItem item = Find(menu, "Picky");
                    Assert.IsNotNull(item, "a disabled command is still on the menu");
                    Assert.IsFalse(item.Enabled);
                    Assert.AreEqual("This file defines no widget.", item.ToolTipText);
                }
                using (ContextMenuStrip menu = MenuFor(panel, other))
                    Assert.IsTrue(Find(menu, "Picky").Enabled, "a null reason means enabled");
            }
        }
    }
}
