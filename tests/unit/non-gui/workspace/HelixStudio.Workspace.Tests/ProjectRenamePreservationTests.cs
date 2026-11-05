using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Tests
{
    [TestClass]
    public class ProjectRenamePreservationTests
    {
        [TestMethod]
        public void RenameRootProject_RefusesMetadataOnlyRenameWithoutChangingManifest()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-rename-preserve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string toml = Path.Combine(root, "pyproject.toml");
                File.WriteAllText(toml,
                    "[project]\nname = \"old\"\nversion = \"0.1.0\"\ndescription = \"keep me\"\n\n" +
                    "[tool.helixstudio]\nkind = \"app\"\nstartup = true\nreferences = [\"lib\"]\n" +
                    "project-order = [\"lib\", \"old\"]\n\n" +
                    "[tool.uv.workspace]\nmembers = [\"lib\"]\n\n" +
                    "[build-system]\nrequires = [\"setuptools>=61\"]\n");
                var solution = new SolutionModel(toml, "old");
                var project = new ProjectModel(toml, "old", solution);
                var lib = new ProjectModel(Path.Combine(root, "lib", "pyproject.toml"), "lib", solution);
                solution.Projects.Add(lib);
                solution.Projects.Add(project);

                Assert.ThrowsException<InvalidOperationException>(() =>
                    new ProjectService().RenameProject(solution, project, "new_name"));

                string text = File.ReadAllText(toml);
                StringAssert.Contains(text, "name = \"old\"");
                StringAssert.Contains(text, "description = \"keep me\"");
                StringAssert.Contains(text, "references = [\"lib\"]");
                StringAssert.Contains(text, "members = [\"lib\"]");
                StringAssert.Contains(text, "[build-system]");
                StringAssert.Contains(text, "project-order = [\"lib\", \"old\"]");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void RenameMemberProject_MovesProjectAndPackageAndUpdatesWorkspaceMetadata()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-member-rename-" + Guid.NewGuid().ToString("N"));
            string oldDir = Path.Combine(root, "old_lib");
            Directory.CreateDirectory(Path.Combine(oldDir, "old_lib"));
            try
            {
                string rootToml = Path.Combine(root, "pyproject.toml");
                string memberToml = Path.Combine(oldDir, "pyproject.toml");
                File.WriteAllText(rootToml,
                    "[project]\nname = \"app\"\n\n[tool.helixstudio]\nkind = \"app\"\n" +
                    "references = [\"old_lib\"]\nproject-order = [\"old_lib\", \"app\"]\n\n" +
                    "[tool.uv.workspace]\nmembers = [\"old_lib\"]\n");
                File.WriteAllText(memberToml,
                    "[project]\nname = \"old_lib\"\n\n[tool.helixstudio]\nkind = \"library\"\n");
                File.WriteAllText(Path.Combine(oldDir, "old_lib", "__init__.py"), "class Thing:\n    pass\n");
                File.WriteAllText(Path.Combine(root, "main.py"), "from old_lib import Thing\n");

                var solution = new SolutionModel(rootToml, "app");
                var member = new ProjectModel(memberToml, "old_lib", solution) { DefaultNamespace = "old_lib" };
                var app = new ProjectModel(rootToml, "app", solution);
                app.ProjectReferences.Add(member);
                solution.Projects.Add(member);
                solution.Projects.Add(app);

                new ProjectService().RenameProject(solution, member, "new_lib");

                string newDir = Path.Combine(root, "new_lib");
                Assert.IsFalse(Directory.Exists(oldDir));
                Assert.IsTrue(File.Exists(Path.Combine(newDir, "new_lib", "__init__.py")));
                StringAssert.Contains(File.ReadAllText(Path.Combine(newDir, "pyproject.toml")), "name = \"new_lib\"");
                StringAssert.Contains(File.ReadAllText(rootToml), "members = [\"new_lib\"]");
                StringAssert.Contains(File.ReadAllText(rootToml), "references = [\"new_lib\"]");
                StringAssert.Contains(File.ReadAllText(rootToml), "project-order = [\"new_lib\", \"app\"]");
                StringAssert.Contains(File.ReadAllText(Path.Combine(root, "main.py")), "from new_lib import Thing");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
