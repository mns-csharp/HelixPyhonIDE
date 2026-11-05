using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// §5.5/§5.6: double-clicking a row OPENS its element and ALSO toggles that row one level, so
    /// the immediate children come into view. The TreeView's own toggle-on-double-click fires at
    /// an unspecified point around NodeMouseDoubleClick, so it stays cancelled (the panel arms a
    /// suppression flag on the second MouseDown of a row double-click and cancels it in
    /// BeforeExpand/BeforeCollapse) and the handler performs the single, deliberate toggle.
    /// MouseUp disarms the flag so glyph clicks and keyboard navigation keep working.
    /// </summary>
    [TestClass]
    public class TreeDoubleClickExpandTests
    {
        private static void SetFlag(ProjectTreePanel panel, bool value)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField(
                "_suppressDoubleClickToggle", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_suppressDoubleClickToggle expected");
            f.SetValue(panel, value);
        }

        private static bool GetFlag(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField(
                "_suppressDoubleClickToggle", BindingFlags.Instance | BindingFlags.NonPublic);
            return (bool)f.GetValue(panel);
        }

        private static void Invoke(ProjectTreePanel panel, string method, params object[] args)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod(
                method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, method + " expected");
            m.Invoke(panel, args);
        }

        /// <summary>A folder row with one child folder that itself has a file under it.</summary>
        private static TreeNode PackageWithGrandchild()
        {
            var grandChild = new TreeNode("attention.py");
            var child = new TreeNode("layers");
            child.Nodes.Add(grandChild);
            var node = new TreeNode("model");
            node.Nodes.Add(child);
            return node;
        }

        private static void DoubleClickRow(ProjectTreePanel panel, TreeNode node)
        {
            // The second MouseDown of a row double-click arms the flag; the handler then runs.
            SetFlag(panel, true);
            Invoke(panel, "OnTreeNodeDoubleClick", panel,
                new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, 20, 20));
        }

        [TestMethod]
        public void DoubleClickingARow_RevealsItsImmediateChildren()
        {
            using (var panel = new ProjectTreePanel())
            {
                TreeNode node = PackageWithGrandchild();

                DoubleClickRow(panel, node);

                Assert.IsTrue(node.IsExpanded,
                    "a double-click on the row must open it so the immediate children are visible");
                Assert.IsFalse(node.Nodes[0].IsExpanded,
                    "one level only — the grandchildren stay as the user left them");
                Assert.IsTrue(GetFlag(panel),
                    "the flag must be re-armed, so a built-in toggle arriving later is still cancelled");
            }
        }

        [TestMethod]
        public void DoubleClickingAnOpenRow_KeepsItExpanded()
        {
            using (var panel = new ProjectTreePanel())
            {
                TreeNode node = PackageWithGrandchild();
                node.Expand();

                DoubleClickRow(panel, node);

                Assert.IsTrue(node.IsExpanded, "opening an item must not collapse its tree row");
            }
        }

        [TestMethod]
        public void DoubleClickingTheGlyph_LeavesTheRowAlone()
        {
            using (var panel = new ProjectTreePanel())
            {
                TreeNode node = PackageWithGrandchild();

                // MouseDown on the +/- glyph leaves the flag disarmed: the control has already
                // toggled twice on its own, and a third toggle here would fight it.
                SetFlag(panel, false);
                Invoke(panel, "OnTreeNodeDoubleClick", panel,
                    new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, 8, 20));

                Assert.IsFalse(node.IsExpanded, "glyph double-clicks are the control's business");
            }
        }

        [TestMethod]
        public void DoubleClickingALeaf_DoesNothing()
        {
            using (var panel = new ProjectTreePanel())
            {
                var leaf = new TreeNode("__init__.py");

                DoubleClickRow(panel, leaf);

                Assert.IsFalse(leaf.IsExpanded, "a row with no children has nothing to open");
            }
        }

        [TestMethod]
        public void ArmedFlag_CancelsTheBuiltInExpand_AndCollapse()
        {
            using (var panel = new ProjectTreePanel())
            {
                var node = new TreeNode("pkg");
                node.Nodes.Add(new TreeNode("child"));

                SetFlag(panel, true);
                var expand = new TreeViewCancelEventArgs(node, false, TreeViewAction.Expand);
                Invoke(panel, "OnTreeBeforeExpand", panel, expand);
                Assert.IsTrue(expand.Cancel,
                    "the control's own double-click toggle must not land — the handler owns the toggle");

                SetFlag(panel, true);
                var collapse = new TreeViewCancelEventArgs(node, false, TreeViewAction.Collapse);
                Invoke(panel, "OnTreeBeforeCollapse", panel, collapse);
                Assert.IsTrue(collapse.Cancel,
                    "likewise for collapse: two toggles per double-click cancel each other out");
            }
        }

        [TestMethod]
        public void DisarmedFlag_LeavesGlyphAndKeyboardTogglesAlone()
        {
            using (var panel = new ProjectTreePanel())
            {
                var node = new TreeNode("pkg");
                node.Nodes.Add(new TreeNode("child"));

                SetFlag(panel, false);
                var expand = new TreeViewCancelEventArgs(node, false, TreeViewAction.Expand);
                Invoke(panel, "OnTreeBeforeExpand", panel, expand);
                Assert.IsFalse(expand.Cancel, "glyph/keyboard expansion must still work (§5.6)");

                var collapse = new TreeViewCancelEventArgs(node, false, TreeViewAction.Collapse);
                Invoke(panel, "OnTreeBeforeCollapse", panel, collapse);
                Assert.IsFalse(collapse.Cancel, "glyph/keyboard collapse must still work (§5.6)");
            }
        }

        [TestMethod]
        public void MouseUp_DisarmsTheFlag()
        {
            using (var panel = new ProjectTreePanel())
            {
                SetFlag(panel, true);
                Invoke(panel, "OnTreeMouseUp", panel,
                    new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0));
                Assert.IsFalse(GetFlag(panel),
                    "a leaf double-click never reaches BeforeExpand, so MouseUp must disarm the flag");
            }
        }
    }
}
