using System;
using System.Drawing;
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
    /// Dependencies ▸ Solution Packages rows navigate on double-click, like every other row in the
    /// tree: the module row opens the module, and an imported-name row goes to that name's
    /// declaration line. Rows for the standard library and for installed packages stay inert —
    /// their source is not part of the solution, so there is nothing honest to open.
    /// </summary>
    [TestClass]
    public class DependencyNavigationTests
    {
        private const string Root = @"C:\ws";
        private static readonly string ErrorsPy = Path.Combine(Root, @"src\lib_core\errors.py");
        private static readonly string ShapesPy = Path.Combine(Root, @"src\lib_core\shapes.py");

        private static TreeView Tree(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        // lib_core declares StorageError (errors.py) and total_area (shapes.py); lib_utils imports
        // both, plus a standard-library module.
        private static SolutionModel BuildSolution()
        {
            var solution = new SolutionModel(Path.Combine(Root, "pyproject.toml"), "ws");

            var libCore = new ProjectModel(Path.Combine(Root, @"src\lib_core\pyproject.toml"), "lib_core", solution)
            {
                Kind = ProjectKind.ClassLibrary
            };
            libCore.Types.Add(new TypeModel("StorageError", "lib_core.errors", TypeKind.ConcreteClass, libCore)
            {
                SourceFilePath = ErrorsPy,
                SourceLineNumber = 12,
            });
            libCore.Types.Add(new TypeModel("ValidationError", "lib_core.errors", TypeKind.ConcreteClass, libCore)
            {
                SourceFilePath = ErrorsPy,
                SourceLineNumber = 30,
            });
            libCore.Functions.Add(new FunctionModel
            {
                Name = "total_area",
                SourceFilePath = ShapesPy,
                SourceLineNumber = 44,
                Project = libCore,
            });
            solution.Projects.Add(libCore);

            var libUtils = new ProjectModel(Path.Combine(Root, @"src\lib_utils\pyproject.toml"), "lib_utils", solution)
            {
                Kind = ProjectKind.ClassLibrary
            };
            var errors = new ModuleDependency("lib_core.errors", DependencyOrigin.SolutionPackage);
            errors.ImportedNames.Add("StorageError");
            errors.ImportedNames.Add("ValidationError");
            var shapes = new ModuleDependency("lib_core.shapes", DependencyOrigin.SolutionPackage);
            shapes.ImportedNames.Add("total_area");
            var asyncio = new ModuleDependency("asyncio", DependencyOrigin.StandardLibrary);
            libUtils.ModuleDependencies.Add(errors);
            libUtils.ModuleDependencies.Add(shapes);
            libUtils.ModuleDependencies.Add(asyncio);
            solution.Projects.Add(libUtils);

            return solution;
        }

        private static TreeNode FindNode(TreeView tree, string text)
        {
            foreach (TreeNode n in AllNodes(tree)) if (n.Text == text) return n;
            Assert.Fail("no node labelled \"" + text + "\"");
            return null;
        }

        private static System.Collections.Generic.IEnumerable<TreeNode> AllNodes(TreeView tree)
        {
            var stack = new System.Collections.Generic.Stack<TreeNode>();
            foreach (TreeNode r in tree.Nodes) stack.Push(r);
            while (stack.Count > 0)
            {
                TreeNode n = stack.Pop();
                yield return n;
                foreach (TreeNode c in n.Nodes) stack.Push(c);
            }
        }

        private static void DoubleClick(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo handler = typeof(ProjectTreePanel).GetMethod("OnTreeNodeDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler, "ProjectTreePanel.OnTreeNodeDoubleClick expected");
            handler.Invoke(panel, new object[]
            {
                null,
                new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, 0, 0),
            });
        }

        // ---------------------------------------------------------------- through the real tree

        [TestMethod]
        public void DoubleClickingAnImportedClass_GoesToItsDeclaration()
        {
            using (var panel = new ProjectTreePanel())
            {
                TypeModel navigated = null;
                panel.TypeDoubleClicked += (s, e) => navigated = e.TypeModel;
                panel.LoadSolution(BuildSolution());

                DoubleClick(panel, FindNode(Tree(panel), "StorageError"));

                Assert.IsNotNull(navigated, "the imported name must navigate somewhere");
                Assert.AreEqual("StorageError", navigated.Name);
                Assert.AreEqual(ErrorsPy, navigated.SourceFilePath);
                Assert.AreEqual(12, navigated.SourceLineNumber, "…and to its own line, not the file's top");
            }
        }

        [TestMethod]
        public void DoubleClickingAnImportedFunction_GoesToItsDeclaration()
        {
            using (var panel = new ProjectTreePanel())
            {
                FunctionModel navigated = null;
                panel.FunctionDoubleClicked += (s, e) => navigated = e.Function;
                panel.LoadSolution(BuildSolution());

                DoubleClick(panel, FindNode(Tree(panel), "total_area"));

                Assert.IsNotNull(navigated);
                Assert.AreEqual(ShapesPy, navigated.SourceFilePath);
                Assert.AreEqual(44, navigated.SourceLineNumber);
            }
        }

        [TestMethod]
        public void DoubleClickingTheModuleRow_OpensTheModule()
        {
            using (var panel = new ProjectTreePanel())
            {
                string opened = null;
                panel.FileDoubleClicked += (s, path) => opened = path;
                panel.LoadSolution(BuildSolution());

                DoubleClick(panel, FindNode(Tree(panel), "lib_core.errors"));

                Assert.AreEqual(ErrorsPy, opened);
            }
        }

        [TestMethod]
        public void AStandardLibraryRow_NavigatesNowhere()
        {
            using (var panel = new ProjectTreePanel())
            {
                bool anything = false;
                panel.FileDoubleClicked += delegate { anything = true; };
                panel.TypeDoubleClicked += delegate { anything = true; };
                panel.FunctionDoubleClicked += delegate { anything = true; };
                panel.LoadSolution(BuildSolution());

                DoubleClick(panel, FindNode(Tree(panel), "asyncio"));

                Assert.IsFalse(anything, "the IDE has no asyncio source in this solution to show");
            }
        }

        // ---------------------------------------------------------------- selection follows

        // The tree defers the selection move to the next message (the TreeView re-asserts the
        // clicked node when its own double-click handling finishes), so pump before asserting.
        private static void Pump()
        {
            for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
        }

        /// <summary>
        /// A Dependencies row is a REFERENCE to code that lives elsewhere in the tree. After
        /// jumping to the definition the highlight has to move to the file that was opened —
        /// leaving it on the reference points at the wrong place.
        /// </summary>
        [TestMethod]
        public void AfterNavigating_TheSelectionMovesToTheRealFileNode()
        {
            using (var host = new Form
            {
                ClientSize = new Size(400, 600),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
            })
            using (var panel = new ProjectTreePanel())
            {
                panel.Dock = DockStyle.Fill;
                host.Controls.Add(panel);
                TestWindow.ShowOffScreen(host);
                panel.LoadSolution(BuildSolution());

                TreeView tree = Tree(panel);
                TreeNode symbolRow = FindNode(tree, "StorageError");
                tree.SelectedNode = symbolRow;          // as the mouse-down would leave it
                DoubleClick(panel, symbolRow);
                Pump();

                Assert.IsNotNull(tree.SelectedNode, "something must stay selected");
                Assert.AreNotSame(symbolRow, tree.SelectedNode,
                    "the highlight must not be left on the dependency row");

                var file = tree.SelectedNode.Tag as ProjectTreePanel.FileGroup;
                Assert.IsNotNull(file, "the selection must land on a file row, got: "
                    + tree.SelectedNode.Text + " / " + (tree.SelectedNode.Tag ?? "(null)"));
                AssertSamePath(ErrorsPy, file.FilePath, "…the file the navigation opened");
            }
        }

        [TestMethod]
        public void AModuleRow_AlsoMovesTheSelectionToItsFile()
        {
            using (var host = new Form
            {
                ClientSize = new Size(400, 600),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
            })
            using (var panel = new ProjectTreePanel())
            {
                panel.Dock = DockStyle.Fill;
                host.Controls.Add(panel);
                TestWindow.ShowOffScreen(host);
                panel.LoadSolution(BuildSolution());

                TreeView tree = Tree(panel);
                DoubleClick(panel, FindNode(tree, "lib_core.shapes"));
                Pump();

                var file = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as ProjectTreePanel.FileGroup;
                Assert.IsNotNull(file, "a module row must select the module's file");
                AssertSamePath(ShapesPy, file.FilePath, "a module row selects its own file");
            }
        }

        [TestMethod]
        public void AnInertStandardLibraryRow_LeavesTheSelectionAlone()
        {
            using (var host = new Form
            {
                ClientSize = new Size(400, 600),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
            })
            using (var panel = new ProjectTreePanel())
            {
                panel.Dock = DockStyle.Fill;
                host.Controls.Add(panel);
                TestWindow.ShowOffScreen(host);
                panel.LoadSolution(BuildSolution());

                TreeView tree = Tree(panel);
                TreeNode asyncioRow = FindNode(tree, "asyncio");
                tree.SelectedNode = asyncioRow;
                DoubleClick(panel, asyncioRow);
                Pump();

                Assert.AreSame(asyncioRow, tree.SelectedNode,
                    "nothing was opened, so nothing should move");
            }
        }

        // File rows carry their path with '/' separators (ProjectTreePanel.NormalizeFileKey), so
        // compare the way the panel's own lookups do rather than as raw strings.
        private static void AssertSamePath(string expected, string actual, string message)
        {
            Assert.AreEqual(
                (expected ?? string.Empty).Replace('/', Path.DirectorySeparatorChar),
                (actual ?? string.Empty).Replace('/', Path.DirectorySeparatorChar),
                true,
                message);
        }

        // ---------------------------------------------------------------- the resolver itself

        [TestMethod]
        public void APackageModule_ResolvesThroughItsInitFile()
        {
            var solution = new SolutionModel(Path.Combine(Root, "pyproject.toml"), "ws");
            var project = new ProjectModel(Path.Combine(Root, @"src\lib_core\pyproject.toml"), "lib_core", solution);
            string initPy = Path.Combine(Root, @"src\lib_core\storage\__init__.py");
            project.Types.Add(new TypeModel("Store", "lib_core.storage", TypeKind.ConcreteClass, project)
            {
                SourceFilePath = initPy,
                SourceLineNumber = 5,
            });
            solution.Projects.Add(project);

            TypeModel type; FunctionModel fn; string file;
            Assert.IsTrue(ProjectTreePanel.TryResolveDependencyTarget(
                solution, "lib_core.storage", "Store", out type, out fn, out file));
            Assert.AreEqual(initPy, type.SourceFilePath, "a package resolves through its __init__.py");
        }

        [TestMethod]
        public void AModuleOutsideTheSolution_ResolvesToNothing()
        {
            TypeModel type; FunctionModel fn; string file;
            Assert.IsFalse(ProjectTreePanel.TryResolveDependencyTarget(
                BuildSolution(), "numpy.linalg", "norm", out type, out fn, out file));
            Assert.IsNull(file);
        }

        [TestMethod]
        public void AnImportedNameWithNoDeclaration_StillOpensItsModule()
        {
            // A module-level constant or a re-export has no TypeModel/FunctionModel of its own.
            TypeModel type; FunctionModel fn; string file;
            Assert.IsTrue(ProjectTreePanel.TryResolveDependencyTarget(
                BuildSolution(), "lib_core.errors", "DEFAULT_TIMEOUT", out type, out fn, out file));
            Assert.IsNull(type);
            Assert.IsNull(fn);
            Assert.AreEqual(ErrorsPy, file, "landing in the right file beats doing nothing");
        }

        [TestMethod]
        public void ASimilarlyNamedModuleInAnotherPackage_IsNotMistakenForIt()
        {
            SolutionModel solution = BuildSolution();
            // `errors.py` at a different package path must not answer for lib_core.errors.
            ProjectModel other = solution.Projects.First(p => p.Name == "lib_utils");
            other.Types.Add(new TypeModel("Unrelated", "lib_utils.errors", TypeKind.ConcreteClass, other)
            {
                SourceFilePath = Path.Combine(Root, @"src\lib_utils\errors.py"),
                SourceLineNumber = 3,
            });

            TypeModel type; FunctionModel fn; string file;
            Assert.IsTrue(ProjectTreePanel.TryResolveDependencyTarget(
                solution, "lib_core.errors", "StorageError", out type, out fn, out file));
            Assert.AreEqual(ErrorsPy, type.SourceFilePath, "the package directory is part of the match");
        }
    }
}
