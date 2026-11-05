using System;
using System.IO;
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
    /// User directive: double-clicking a source element (class/interface/ABC/function/property/
    /// method/constructor/variable/attribute/...) in the Solution Explorer must always open it in
    /// the Editor, never the Class Designer Canvas — the canvas is reached only through the
    /// context menu ("Open in Class Designer Canvas" / "Go to Designer"). Functions and members
    /// already behaved this way (OnProjectTreeFunctionDoubleClicked); this covers the type
    /// (class/interface/ABC/...) row, which previously forced a switch to the Designer Canvas tab.
    /// </summary>
    [TestClass]
    public class ProjectTreeTypeDoubleClickOpensEditorTests
    {
        private static TreeView Tree(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        private static TreeNode FindTypeNode(TreeView tree, string name)
        {
            TreeNode found = null;
            void Walk(TreeNode n) { if (n.Tag is TypeModel t && t.Name == name) found = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        private static void DoubleClick(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("OnTreeNodeDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(panel, new object[]
            {
                null, new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, 0, 0)
            });
        }

        [TestMethod]
        public void DoubleClickingAClassRow_OpensTheEditor_NotTheDesignerCanvas()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_typedbl_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "class Widget:\n    pass\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Size = new System.Drawing.Size(900, 600);
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
                    var proj = new ProjectModel(@"C:\ws\pyproject.toml", "ws", sln);
                    sln.Projects.Add(proj);
                    var widget = new TypeModel("Widget", "ws", TypeKind.ConcreteClass, proj)
                    {
                        SourceFilePath = tmp,
                        SourceLineNumber = 1,
                    };
                    proj.Types.Add(widget);
                    form._projectTreePanel.LoadSolution(sln);
                    form._currentSolution = sln;
                    form.UpdateMenuWorkflowVisibility(); // brings the solution-scoped Designer Canvas tab onto the strip

                    TabPage designerTab = (TabPage)typeof(MainForm)
                        .GetField("_graphicalDesignerTab", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(form);

                    TreeNode typeNode = FindTypeNode(Tree(form._projectTreePanel), "Widget");
                    Assert.IsNotNull(typeNode, "the Widget class node should exist in the tree");

                    DoubleClick(form._projectTreePanel, typeNode);
                    Application.DoEvents();

                    Assert.AreEqual(tmp, form.ActiveDocumentPath(),
                        "double-clicking the class must open its source file in the Editor");
                    Assert.AreEqual(form._editorTab, form._mainTabControl.SelectedTab,
                        "double-click must activate the Editor tab");
                    Assert.AreNotEqual(designerTab, form._mainTabControl.SelectedTab,
                        "double-click must NOT switch to the Class Designer Canvas tab");

                    form.Close();
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }
}
