using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Phase 6 of the Solution Explorer CRUD-coverage sweep: before this, MemberModel nodes
    /// (methods/properties/fields/constructors nested under a class) had ZERO rename or delete —
    /// no menu item, no F2/Del key handling anywhere (confirmed by direct code audit before this
    /// work started). Menu/key wiring is tested at the tree level (no dialog involved for either
    /// gesture's entry point); member RENAME itself is also covered end-to-end through MainForm
    /// since — unlike Delete's confirmation MessageBox — F2 inline rename never blocks on a modal
    /// dialog, so the real OnTreeRename handler and real file I/O can be exercised directly.
    /// </summary>
    [TestClass]
    public class MemberRenameDeleteCommandsTests
    {
        private static ProjectTreePanel LoadType(out TypeModel type, out MemberModel method, out MemberModel ctor)
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Widget", "pkg", TypeKind.ConcreteClass, proj)
            {
                SourceFilePath = @"C:\ws\src\pkg\widget.py",
                SourceLineNumber = 1,
            };
            var m = new MemberModel("render", MemberKind.Method) { SourceLineNumber = 2 };
            var c = new MemberModel("__init__", MemberKind.Constructor) { SourceLineNumber = 5 };
            t.Members.Add(m);
            t.Members.Add(c);
            proj.Types.Add(t);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            type = t;
            method = m;
            ctor = c;
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        private static TreeNode FindMemberNode(TreeView tree, string name)
        {
            TreeNode found = null;
            void Walk(TreeNode n) { if (n.Tag is MemberModel m && m.Name == name) found = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        private static void PressKey(ProjectTreePanel panel, Keys key)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("OnTreeKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(panel, new object[] { panel, new KeyEventArgs(key) });
        }

        // -------------------------------------------------------------- menu / key wiring

        [TestMethod]
        public void MethodNode_Menu_OffersEnabledRename_AndDelete()
        {
            using (var panel = LoadType(out _, out _, out _))
            {
                TreeNode node = FindMemberNode(Tree(panel), "render");
                Assert.IsNotNull(node);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(node))
                {
                    ToolStripMenuItem rename = (ToolStripMenuItem)menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Rename");
                    Assert.IsTrue(rename.Enabled, "a method's Rename must be enabled");
                    Assert.AreEqual("F2", rename.ShortcutKeyDisplayString);

                    ToolStripMenuItem delete = (ToolStripMenuItem)menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Delete");
                    Assert.AreEqual("Del", delete.ShortcutKeyDisplayString);
                }
            }
        }

        [TestMethod]
        public void ConstructorNode_Menu_OffersDisabledRename_ButEnabledDelete()
        {
            using (var panel = LoadType(out _, out _, out _))
            {
                TreeNode node = FindMemberNode(Tree(panel), "__init__");
                Assert.IsNotNull(node);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(node))
                {
                    ToolStripMenuItem rename = (ToolStripMenuItem)menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Rename");
                    Assert.IsFalse(rename.Enabled, "the constructor's Rename must be disabled, never hidden");

                    ToolStripMenuItem delete = (ToolStripMenuItem)menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Delete");
                    Assert.IsTrue(delete.Enabled, "deleting __init__ is legal Python (falls back to the default ctor)");
                }
            }
        }

        [TestMethod]
        public void MethodNode_DeleteKey_RaisesDeleteRequested_WithTheMember()
        {
            using (var panel = LoadType(out _, out MemberModel method, out _))
            {
                TreeNode node = FindMemberNode(Tree(panel), "render");
                Tree(panel).SelectedNode = node;

                object deleted = null;
                panel.DeleteRequested += (s, e) => deleted = e.Item;
                PressKey(panel, Keys.Delete);

                Assert.AreSame(method, deleted);
            }
        }

        [TestMethod]
        public void ConstructorNode_F2_DoesNotBeginRename()
        {
            using (var panel = LoadType(out _, out _, out _))
            {
                TreeNode node = FindMemberNode(Tree(panel), "__init__");
                Tree(panel).SelectedNode = node;

                PressKey(panel, Keys.F2);

                Assert.IsFalse(Tree(panel).LabelEdit, "F2 on the constructor must be a no-op, not start inline editing");
            }
        }

        [TestMethod]
        public void MethodNode_F2_BeginsInlineRename()
        {
            using (var panel = LoadType(out _, out _, out _))
            {
                TreeNode node = FindMemberNode(Tree(panel), "render");
                Tree(panel).SelectedNode = node;

                PressKey(panel, Keys.F2);

                Assert.IsTrue(Tree(panel).LabelEdit, "F2 on a method must start inline editing");
            }
        }

        // -------------------------------------------------------------- end-to-end rename (no dialog)

        [TestMethod]
        public void OnTreeRename_MethodMember_RewritesTheDeclarationAndItsSelfCallSites()
        {
            // Deliberately NOT deleted afterward. A successful member rename calls MainForm.
            // ReloadSolutionAsync, an `async void` fire-and-forget method with no awaitable handle —
            // pumping the message loop for a fixed duration (TestMainForm.PumpUntilIdle) reduces but
            // does NOT guarantee the continuation has actually finished (real disk I/O can outlast
            // any fixed budget), and all tests in this assembly share one process/UI thread, so an
            // un-finished continuation can fire during a LATER, unrelated test's DoEvents() call. The
            // only UNCONDITIONALLY safe fix is to never make the directory disappear at all — leaving
            // this harmless temp folder under %TEMP% is a small, permanent price for a real,
            // repeatedly-reproduced live disruption  not being possible even in principle.
            string dir = Path.Combine(Path.GetTempPath(), "helix_memberrename_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "widget.py");
            File.WriteAllText(file,
                "class Widget:\n" +
                "    def render(self):\n" +
                "        return 'r'\n" +
                "\n" +
                "    def refresh(self):\n" +
                "        return self.render()\n");
            {
                var sln = new SolutionModel(Path.Combine(dir, "pyproject.toml"), "ws");
                var proj = new ProjectModel(Path.Combine(dir, "pyproject.toml"), "ws", sln);
                sln.Projects.Add(proj);
                var type = new TypeModel("Widget", "ws", TypeKind.ConcreteClass, proj) { SourceFilePath = file };
                var method = new MemberModel("render", MemberKind.Method);
                type.Members.Add(method);
                proj.Types.Add(type);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form._currentSolution = sln;
                    form._projectTreePanel.LoadSolution(sln);

                    FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
                    var tree = (TreeView)f.GetValue(form._projectTreePanel);
                    TreeNode memberNode = FindMemberNode(tree, "render");
                    Assert.IsNotNull(memberNode, "the render member node should exist");
                    Assert.IsInstanceOfType(memberNode.Parent.Tag, typeof(TypeModel), "member node's parent must be the owning type");

                    form.OnTreeRename(form, new TreeRenameEventArgs(memberNode, method, "draw"));

                    string updated = File.ReadAllText(file);
                    StringAssert.Contains(updated, "def draw(self):");
                    StringAssert.Contains(updated, "return self.draw()");
                    Assert.IsFalse(updated.Contains("render"), "the old name must not survive anywhere in the class");

                    // Give the ReloadSolutionAsync continuation this rename triggered a real chance
                    // to finish while the (never-deleted) directory it reads from is still valid.
                    TestMainForm.PumpUntilIdle();

                    form.Close();
                }
            }
        }

        [TestMethod]
        public void OnTreeRename_ConstructorMember_IsRefused_FileUntouched()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_memberrename_ctor_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "widget.py");
            const string original = "class Widget:\n    def __init__(self):\n        pass\n";
            File.WriteAllText(file, original);
            try
            {
                var sln = new SolutionModel(Path.Combine(dir, "pyproject.toml"), "ws");
                var proj = new ProjectModel(Path.Combine(dir, "pyproject.toml"), "ws", sln);
                sln.Projects.Add(proj);
                var type = new TypeModel("Widget", "ws", TypeKind.ConcreteClass, proj) { SourceFilePath = file };
                var ctor = new MemberModel("__init__", MemberKind.Constructor);
                type.Members.Add(ctor);
                proj.Types.Add(type);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form._currentSolution = sln;
                    form._projectTreePanel.LoadSolution(sln);

                    FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
                    var tree = (TreeView)f.GetValue(form._projectTreePanel);
                    TreeNode memberNode = FindMemberNode(tree, "__init__");
                    Assert.IsNotNull(memberNode);

                    form.OnTreeRename(form, new TreeRenameEventArgs(memberNode, ctor, "setup"));

                    Assert.AreEqual(original, File.ReadAllText(file),
                        "the file must be completely untouched when the handler refuses a constructor rename");

                    form.Close();
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
