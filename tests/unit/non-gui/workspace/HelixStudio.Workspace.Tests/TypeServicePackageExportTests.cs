using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Tests
{
    [TestClass]
    public class TypeServicePackageExportTests
    {
        [TestMethod]
        public void AddClass_CreatesPackageMarkerAndExportsTheClassObject()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-type-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var solution = new SolutionModel(Path.Combine(root, "solution.toml"), "solution");
                var project = new ProjectModel(Path.Combine(root, "pyproject.toml"), "sample_pkg", solution)
                {
                    DefaultNamespace = "sample_pkg"
                };

                new TypeService().AddClass(project, "NewClass", project.DefaultNamespace);

                string init = Path.Combine(root, "sample_pkg", "__init__.py");
                Assert.IsTrue(File.Exists(init), "Add Class must create the package marker");
                Assert.AreEqual("from .NewClass import NewClass\n",
                    File.ReadAllText(init).Replace("\r\n", "\n"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void RenameAndRemoveClass_KeepPackageExportsTruthful()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-type-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var solution = new SolutionModel(Path.Combine(root, "solution.toml"), "solution");
                var project = new ProjectModel(Path.Combine(root, "pyproject.toml"), "sample_pkg", solution)
                {
                    DefaultNamespace = "sample_pkg"
                };
                solution.Projects.Add(project);
                var service = new TypeService();
                var type = service.AddClass(project, "OldName", project.DefaultNamespace);

                service.RenameType(solution, type, "NewName");
                string init = Path.Combine(root, "sample_pkg", "__init__.py");
                string renamed = File.ReadAllText(init);
                Assert.IsFalse(renamed.Contains("OldName"));
                StringAssert.Contains(renamed, "from .NewName import NewName");

                service.RemoveType(project, type);
                Assert.IsFalse(File.ReadAllText(init).Contains("NewName"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
