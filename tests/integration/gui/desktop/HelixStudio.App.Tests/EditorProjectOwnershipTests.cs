using System.IO;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.Core.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class EditorProjectOwnershipTests
    {
        [TestMethod]
        public void NestedConsoleAppWinsOverAncestorLibraryDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-owner");
            var solution = new SolutionModel(Path.Combine(root, "solution.toml"), "solution");
            var library = new ProjectModel(Path.Combine(root, "pyproject.toml"), "library", solution);
            var app = new ProjectModel(Path.Combine(root, "apps", "console", "pyproject.toml"), "console", solution);
            solution.Projects.Add(library);
            solution.Projects.Add(app);

            ProjectModel owner = EditorCoordinator.FindOwningProject(solution.Projects,
                Path.Combine(root, "apps", "console", "main.py"));

            Assert.AreSame(app, owner, "the first ancestor project must not steal a nested project's file");
        }

        [TestMethod]
        public void TextualPrefixIsNotAPathOwner()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-owner");
            var solution = new SolutionModel(Path.Combine(root, "solution.toml"), "solution");
            var project = new ProjectModel(Path.Combine(root, "app", "pyproject.toml"), "app", solution);
            Assert.IsNull(EditorCoordinator.FindOwningProject(new[] { project },
                Path.Combine(root, "application", "main.py")));
        }
    }
}
