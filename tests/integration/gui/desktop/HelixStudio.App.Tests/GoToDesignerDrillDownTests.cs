using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Projection;
using HelixPythonNamespace.UI.Rendering;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// "Open in Class Designer Canvas" / "Go to Designer" on a Solution Explorer class must drill the
    /// SAME shared <see cref="NavigationViewState"/> the canvas double-click hierarchy uses down to
    /// that type's own project + namespace -- not leave the navigation level stuck wherever it was
    /// (e.g. the solution motif) while a disconnected flat view gets swapped onto the canvas. A
    /// stale level desyncs the breadcrumb, the level-aware canvas context menu, and the
    /// Interfaces/Abstract/General category tabs from what is actually on screen.
    /// </summary>
    [TestClass]
    public class GoToDesignerDrillDownTests
    {
        private static ProjectModel BuildProjectWithTwoNamespaces(SolutionModel sln, out TypeModel widget)
        {
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);

            // A second namespace so the fix must land on the RIGHT one, not just "whatever is first".
            var other = new TypeModel("Other", "pkg.other", TypeKind.ConcreteClass, proj)
            {
                SourceFilePath = @"C:\ws\src\pkg\other\other.py"
            };
            proj.Types.Add(other);

            widget = new TypeModel("Widget", "pkg.widgets", TypeKind.ConcreteClass, proj)
            {
                SourceFilePath = @"C:\ws\src\pkg\widgets\widget.py"
            };
            proj.Types.Add(widget);

            return proj;
        }

        [TestMethod]
        public void GoToDesigner_FromSolutionMotif_DrillsDownToTheType()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Size = new System.Drawing.Size(900, 600);
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
                TypeModel widget;
                ProjectModel proj = BuildProjectWithTwoNamespaces(sln, out widget);

                // Wire the canvas the same way the real "open solution" path
                // (SolutionCoordinator) does, landing on the solution-level motif -- a single
                // solution rectangle, no project checked visible -- exactly the starting state
                // the bug report describes.
                form._currentSolution = sln;
                form._layoutStore = new JsonLayoutMetadataStore(sln.FilePath);
                form._projectionBuilder = new SolutionProjectionBuilder(
                    form._canvas.SceneGraph, form._layoutStore, new GridAutoLayoutStrategy());
                form._projectionBuilder.SetVisibleProjects(new List<string>());
                form._navigationState = new NavigationViewState(sln.Name);
                form._projectionBuilder.BuildSolutionLevel(sln);

                Assert.AreEqual(NavigationLevel.Solution, form._navigationState.Level,
                    "precondition: canvas is showing the solution motif");
                Assert.IsNull(form._projectionBuilder.FindVisual(widget.FullyQualifiedName),
                    "precondition: the type has no visual yet at solution level");

                form.GoToDesigner(widget);
                Application.DoEvents();

                Assert.AreEqual(NavigationLevel.Type, form._navigationState.Level,
                    "the shared navigation state must drill all the way to the type, not stay at Solution");
                Assert.AreEqual(proj.Name, form._navigationState.ProjectName);
                Assert.AreEqual("pkg.widgets", form._navigationState.NamespaceName);

                NodeVisual visual = form._projectionBuilder.FindVisual(widget.FullyQualifiedName);
                Assert.IsNotNull(visual, "the class must actually be rendered on the canvas after navigating");

                Assert.IsTrue(form._navigationState.GetBreadcrumbDisplay().EndsWith("pkg.widgets"),
                    "the breadcrumb must reflect where the canvas actually landed, not the old level");

                form.Close();
            }
        }
    }
}
