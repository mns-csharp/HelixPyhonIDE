using System;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Hovering a class / method / function row in the Solution Explorer shows its documentation.
    /// The tip is built by ProjectTreePanel.SymbolToolTipFor from the node's Tag; this pins the
    /// text it produces per symbol kind (the on-screen plumbing — ToolTip.Show on MouseMove — is
    /// exercised by the GUI run, not here).
    /// </summary>
    [TestClass]
    public class ProjectTreeSymbolToolTipTests
    {
        private static TreeNode NodeFor(object tag)
        {
            TreeNode n = new TreeNode("label");
            n.Tag = tag;
            return n;
        }

        private static TypeModel MakeType(string name, string doc)
        {
            SolutionModel sln = new SolutionModel(@"C:\s\pyproject.toml", "s");
            ProjectModel proj = new ProjectModel(@"C:\s\pyproject.toml", "app_gui", sln);
            TypeModel t = new TypeModel(name, "app_gui.main", TypeKind.ConcreteClass, proj);
            t.Documentation = doc;
            return t;
        }

        [TestMethod]
        public void ClassNode_ShowsKindFqnAndDocstring()
        {
            TypeModel t = MakeType("MainWindow",
                "Top-level window: a notebook with a Shapes tab and an Animals tab.");

            string tip = ProjectTreePanel.SymbolToolTipFor(NodeFor(t));

            StringAssert.StartsWith(tip, "ConcreteClass app_gui.main.MainWindow");
            StringAssert.Contains(tip, "Top-level window: a notebook with a Shapes tab and an Animals tab.");
        }

        [TestMethod]
        public void ClassNodeWithoutDocstring_StillNamesTheType()
        {
            string tip = ProjectTreePanel.SymbolToolTipFor(NodeFor(MakeType("Bare", null)));
            Assert.AreEqual("ConcreteClass app_gui.main.Bare", tip);
        }

        [TestMethod]
        public void MethodNode_ShowsSignatureAndDocstring()
        {
            MemberModel m = new MemberModel("acquire", MemberKind.Method);
            m.IsAsync = true;
            m.ReturnType = "None";
            m.Documentation = "Block until a permit is available.";

            string tip = ProjectTreePanel.SymbolToolTipFor(NodeFor(m));

            StringAssert.StartsWith(tip, "async acquire() -> None");
            StringAssert.Contains(tip, "Block until a permit is available.");
        }

        [TestMethod]
        public void MethodNode_IncludesParameterNamesAndTypes()
        {
            MemberModel m = new MemberModel("set_shapes", MemberKind.Method);
            m.Parameters.Add(new MethodParameterModel("shapes", "list"));
            m.Parameters.Add(new MethodParameterModel("clear", ""));

            string tip = ProjectTreePanel.SymbolToolTipFor(NodeFor(m));

            Assert.AreEqual("set_shapes(shapes: list, clear)", tip);
        }

        [TestMethod]
        public void FunctionNode_ShowsDefLineAndDocstring()
        {
            SolutionModel sln = new SolutionModel(@"C:\s\pyproject.toml", "s");
            ProjectModel proj = new ProjectModel(@"C:\s\pyproject.toml", "lib_utils", sln);
            FunctionModel fn = new FunctionModel
            {
                Name = "retry_async",
                Namespace = "lib_utils.async_ops",
                IsAsync = true,
                Documentation = "Retry an async operation.",
                Project = proj,
            };

            string tip = ProjectTreePanel.SymbolToolTipFor(NodeFor(fn));

            StringAssert.StartsWith(tip, "async def lib_utils.async_ops.retry_async");
            StringAssert.Contains(tip, "Retry an async operation.");
        }

        [TestMethod]
        public void NonSymbolRow_HasNoToolTip()
        {
            Assert.IsNull(ProjectTreePanel.SymbolToolTipFor(NodeFor(null)));
            Assert.IsNull(ProjectTreePanel.SymbolToolTipFor(NodeFor("Resources")));
            Assert.IsNull(ProjectTreePanel.SymbolToolTipFor(null));
        }

        // A module-length docstring must not turn into a full-screen tooltip.
        [TestMethod]
        public void LongDocstring_IsClipped()
        {
            string doc = string.Join("\n", new string[40]).Replace("\n", "x\n") + new string('y', 2000);
            TypeModel t = MakeType("Big", doc);

            string tip = ProjectTreePanel.SymbolToolTipFor(NodeFor(t));

            Assert.IsTrue(tip.Length < 1000, "tooltip length was " + tip.Length);
            StringAssert.EndsWith(tip, "…");
        }
    }
}
