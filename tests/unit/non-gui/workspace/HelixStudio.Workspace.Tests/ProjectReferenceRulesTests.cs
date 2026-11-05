using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class ProjectReferenceRulesTests
    {
        private SolutionModel _solution;
        private ProjectService _service;

        [TestInitialize]
        public void Initialize()
        {
            _solution = new SolutionModel(
                Path.Combine(Path.GetTempPath(), "reference-rules", "solution.toml"),
                "reference-rules");
            _service = new ProjectService();
        }

        private ProjectModel Project(string name, ProjectKind kind)
        {
            var project = new ProjectModel(
                Path.Combine(Path.GetTempPath(), "reference-rules", name, "project.toml"),
                name,
                _solution) { Kind = kind };
            _solution.Projects.Add(project);
            return project;
        }

        [TestMethod]
        public void ExecutableMayReferenceLibrary()
        {
            ProjectModel app = Project("app", ProjectKind.App);
            ProjectModel library = Project("library", ProjectKind.ClassLibrary);

            _service.AddProjectReference(app, library);

            CollectionAssert.Contains(app.ProjectReferences, library);
        }

        [TestMethod]
        public void LibraryMayReferenceLibrary()
        {
            ProjectModel first = Project("first", ProjectKind.ClassLibrary);
            ProjectModel second = Project("second", ProjectKind.ClassLibrary);

            _service.AddProjectReference(first, second);

            CollectionAssert.Contains(first.ProjectReferences, second);
        }

        [TestMethod]
        public void LibraryCannotReferenceExecutable()
        {
            ProjectModel library = Project("library", ProjectKind.ClassLibrary);
            ProjectModel app = Project("app", ProjectKind.App);

            InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(
                () => _service.AddProjectReference(library, app));

            StringAssert.StartsWith(error.Message, "PSM-43:");
        }

        [TestMethod]
        public void ExecutableCannotReferenceExecutable()
        {
            ProjectModel first = Project("first", ProjectKind.App);
            ProjectModel second = Project("second", ProjectKind.GuiApp);

            InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(
                () => _service.AddProjectReference(first, second));

            StringAssert.StartsWith(error.Message, "PSM-44:");
        }

        [TestMethod]
        public void CircularReferenceIsRejected()
        {
            ProjectModel first = Project("first", ProjectKind.ClassLibrary);
            ProjectModel second = Project("second", ProjectKind.ClassLibrary);
            _service.AddProjectReference(first, second);

            InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(
                () => _service.AddProjectReference(second, first));

            StringAssert.StartsWith(error.Message, "PSM-45:");
        }

        [TestMethod]
        public void BuildOrderPlacesDependenciesBeforeConsumers()
        {
            ProjectModel app = Project("app", ProjectKind.App);
            ProjectModel middle = Project("middle", ProjectKind.ClassLibrary);
            ProjectModel foundation = Project("foundation", ProjectKind.ClassLibrary);
            _service.AddProjectReference(app, middle);
            _service.AddProjectReference(middle, foundation);

            var order = ProjectService.GetBuildOrder(_solution).Select(x => x.Name).ToList();

            CollectionAssert.AreEqual(
                new[] { "foundation", "middle", "app" },
                order);
        }
    }
}
