using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The Solution Explorer's symbol tooltips are only as good as the model behind them: nothing
    /// used to populate <c>TypeModel.Documentation</c> / <c>MemberModel.Documentation</c>, and
    /// <c>FunctionModel</c> had no such field at all, so hovering a class in the tree had nothing
    /// to show. Driven against the real python-test-sample-oop fixture.
    /// </summary>
    [TestClass]
    public class PythonDocstringExtractionTests
    {
        private static string FixtureRoot()
        {
            string dir = Path.GetDirectoryName(new Uri(typeof(PythonDocstringExtractionTests).Assembly.CodeBase).LocalPath);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, @"tests\fixtures\python\python-test-sample-oop");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static ProjectModel ExtractFixtureFile(string relativePath)
        {
            string root = FixtureRoot();
            if (root == null) Assert.Inconclusive("python-test-sample-oop fixture not found");
            string file = Path.Combine(root, relativePath);
            if (!File.Exists(file)) Assert.Inconclusive("fixture file missing: " + relativePath);

            string temp = Path.Combine(Path.GetTempPath(), "helix_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string pyproject = Path.Combine(temp, "pyproject.toml");
            File.WriteAllText(pyproject, "[project]\nname = \"t\"\nrequires-python = \">=3.12\"\n");
            SolutionModel sln = new SolutionModel(pyproject, "t");
            ProjectModel project = new ProjectModel(pyproject, "t", sln);
            project.DefaultNamespace = "t";
            sln.Projects.Add(project);

            ParseResult parsed = Python.Python.ParseModule(File.ReadAllText(file), file);
            PythonTypeExtractor.ExtractInto(project, parsed.Module, file);
            return project;
        }

        // The exact case reported: hovering MainWindow in the tree must offer its docstring.
        [TestMethod]
        public void ClassDocstring_IsCarriedOnTheTypeModel()
        {
            ProjectModel p = ExtractFixtureFile(@"app_gui\main.py");
            TypeModel mainWindow = p.Types.FirstOrDefault(t => t.Name == "MainWindow");
            Assert.IsNotNull(mainWindow, "main.py declares MainWindow");
            Assert.AreEqual(
                "Top-level window: a notebook with a Shapes tab and an Animals tab.",
                mainWindow.Documentation);
        }

        [TestMethod]
        public void MethodDocstring_IsCarriedOnTheMemberModel()
        {
            ProjectModel p = ExtractFixtureFile(@"app_gui\main.py");
            TypeModel mainWindow = p.Types.First(t => t.Name == "MainWindow");
            MemberModel populate = mainWindow.Members.FirstOrDefault(m => m.Name == "_populate");
            Assert.IsNotNull(populate, "MainWindow declares _populate");
            Assert.AreEqual("Fill both panels with a small, representative sample.", populate.Documentation);
        }

        [TestMethod]
        public void ModuleLevelFunctionDocstring_IsCarriedOnTheFunctionModel()
        {
            ProjectModel p = ExtractFixtureFile(@"lib_utils\async_ops.py");
            FunctionModel retry = p.Functions.FirstOrDefault(f => f.Name == "retry_async");
            Assert.IsNotNull(retry, "async_ops.py declares retry_async");
            StringAssert.StartsWith(retry.Documentation, "Retry an async operation.");
            Assert.IsTrue(retry.IsAsync);
        }

        [TestMethod]
        public void UndocumentedSymbol_LeavesDocumentationNull()
        {
            ParseResult parsed = Python.Python.ParseModule("class Bare:\n    def go(self):\n        pass\n");
            string temp = Path.Combine(Path.GetTempPath(), "helix_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string pyproject = Path.Combine(temp, "pyproject.toml");
            File.WriteAllText(pyproject, "[project]\nname = \"t\"\n");
            SolutionModel sln = new SolutionModel(pyproject, "t");
            ProjectModel project = new ProjectModel(pyproject, "t", sln);
            sln.Projects.Add(project);

            PythonTypeExtractor.ExtractInto(project, parsed.Module, Path.Combine(temp, "bare.py"));

            TypeModel bare = project.Types.Single();
            Assert.IsNull(bare.Documentation, "no docstring means no tooltip, not an empty one");
            Assert.IsNull(bare.Members.Single(m => m.Name == "go").Documentation);
        }
    }
}
