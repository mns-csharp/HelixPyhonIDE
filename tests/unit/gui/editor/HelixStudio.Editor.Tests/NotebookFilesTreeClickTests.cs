using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Double-clicking a folder in the Files tree must not open a file.
    ///
    /// <para>
    /// Reported: double-clicking the <c>cms</c> folder opened <c>restraint.py</c>. A double-click
    /// on a folder also collapses it, and collapsing a folder with fifty children pulls everything
    /// below it up the screen — so by the time <c>NodeMouseDoubleClick</c> hit-tested the pointer,
    /// a completely different node was underneath it. The file that opened was chosen by where the
    /// tree happened to reflow, not by anything the user clicked.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookFilesTreeClickTests
    {
        private string _root;

        [TestInitialize]
        public void CreateTree()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_tree_" + Guid.NewGuid().ToString("N"));
            string package = Path.Combine(_root, "cms");
            Directory.CreateDirectory(package);
            // Enough children that collapsing the folder moves everything below it a long way.
            foreach (string name in new[] { "align", "band", "chain", "energy", "frame",
                                            "grad", "hist", "layer", "restraint", "tensor" })
                File.WriteAllText(Path.Combine(package, name + ".py"), "x = 1\n");
            Directory.CreateDirectory(Path.Combine(_root, "zz_after"));
            File.WriteAllText(Path.Combine(_root, "zz_after", "later.py"), "y = 2\n");
        }

        [TestCleanup]
        public void RemoveTree()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static TreeView TreeOf(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is TreeView tree) return tree;
                TreeView deeper = TreeOf(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static void Raise(Control target, string handler, MouseEventArgs args)
        {
            MethodInfo method = null;
            for (Type type = target.GetType(); type != null && method == null; type = type.BaseType)
                method = type.GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic,
                                        null, new[] { typeof(MouseEventArgs) }, null);
            Assert.IsNotNull(method, handler + " not found");
            method.Invoke(target, new object[] { args });
        }

        /// <summary>
        /// The defect, stated as the contract it broke: the double-click must act on the node the
        /// PRESS began on, not on whatever WinForms hit-tests when the event is finally delivered.
        ///
        /// <para>
        /// Collapsing a folder does not move the folder itself, so the first attempt at this test
        /// passed with the fix reverted and proved nothing. What actually moved the tree was the
        /// SCROLL CLAMP: the tree was scrolled down, collapsing the folder removed fifty rows of
        /// content, the scroll offset clamped to the new shorter extent, and everything — including
        /// the folder — slid under the stationary pointer. Rather than re-stage that, this drives
        /// the exact end state it produces: a double-click delivered with a different node than the
        /// one pressed. That is the input the handler actually received when it opened
        /// <c>restraint.py</c>.
        /// </para>
        /// </summary>
        [TestMethod]
        public void ADoubleClickActsOnTheNodeThePressBeganOnNotTheOneUnderneathAfterwards()
        {
            var panel = new NotebookFilesPanel();
            var opened = new List<string>();
            panel.FileActivated += delegate (object sender, NotebookFileOpenRequest request)
            {
                opened.Add(request.Path);
            };

            using (var form = new Form { ClientSize = new Size(400, 600) })
            {
                panel.Dock = DockStyle.Fill;
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                panel.BrowseTo(_root);
                Application.DoEvents();

                TreeView tree = TreeOf(panel);
                TreeNode folder = tree.Nodes[0].Nodes[0];          // <root>\cms
                Assert.AreEqual("cms", folder.Text);
                folder.Expand();
                Application.DoEvents();

                TreeNode strayFile = null;
                foreach (TreeNode child in folder.Nodes)
                    if (child.Text == "restraint.py") strayFile = child;
                Assert.IsNotNull(strayFile, "the fixture should contain restraint.py");

                Point onFolder = new Point(folder.Bounds.Left + 4, folder.Bounds.Top + 2);

                // First click of the pair — the only moment guaranteed to precede the collapse.
                Raise(tree, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, onFolder.X, onFolder.Y, 0));

                folder.Collapse();
                Application.DoEvents();

                // The double-click, delivered naming a node the user never pressed. This is what
                // WinForms handed the handler after the scroll clamp moved the tree.
                RaiseNodeDoubleClick(tree, strayFile, onFolder);

                CollectionAssert.AreEqual(new string[0], opened.ToArray(),
                    "double-clicking a folder opened: "
                    + (opened.Count == 0 ? "(nothing)" : string.Join(", ", opened.ToArray())));
            }
        }

        /// <summary>The control: double-clicking an actual file still opens that file.</summary>
        [TestMethod]
        public void DoubleClickingAFileStillOpensThatFile()
        {
            var panel = new NotebookFilesPanel();
            var opened = new List<string>();
            panel.FileActivated += delegate (object sender, NotebookFileOpenRequest request)
            {
                opened.Add(request.Path);
            };

            using (var form = new Form { ClientSize = new Size(400, 600) })
            {
                panel.Dock = DockStyle.Fill;
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                panel.BrowseTo(_root);
                Application.DoEvents();

                TreeView tree = TreeOf(panel);
                TreeNode folder = tree.Nodes[0].Nodes[0];
                folder.Expand();
                Application.DoEvents();

                TreeNode file = null;
                foreach (TreeNode child in folder.Nodes)
                    if (child.Text == "tensor.py") file = child;
                Assert.IsNotNull(file, "the fixture should contain tensor.py");

                var at = new Point(file.Bounds.Left + 4, file.Bounds.Top + 2);
                Raise(tree, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0));
                RaiseNodeDoubleClick(tree, file, at);

                Assert.AreEqual(1, opened.Count, "the file the user clicked should have opened");
                StringAssert.EndsWith(opened[0], "tensor.py");
            }
        }

        /// <summary>Raise the tree's own NodeMouseDoubleClick, the way WinForms does.</summary>
        private static void RaiseNodeDoubleClick(TreeView tree, TreeNode node, Point at)
        {
            MethodInfo method = typeof(TreeView).GetMethod("OnNodeMouseDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "OnNodeMouseDoubleClick not found");
            method.Invoke(tree, new object[]
            {
                new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, at.X, at.Y),
            });
        }
    }
}
