using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Tests
{
    [TestClass]
    public class ProjectOrderPersistenceTests
    {
        [TestMethod]
        public void MoveProject_PersistsRootAndMemberOrderAcrossReload()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-order-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                    "[project]\nname = \"app\"\nrequires-python = \">=3.11\"\n\n" +
                    "[tool.helixstudio]\nkind = \"app\"\nstartup = true\n\n" +
                    "[tool.uv.workspace]\nmembers = [\"lib10\", \"lib11\"]\n");
                WriteMember(root, "lib10");
                WriteMember(root, "lib11");

                var loader = new SolutionService();
                var loaded = loader.LoadAsync(Path.Combine(root, "pyproject.toml")).Result;
                Assert.IsTrue(loaded.Success, loaded.ErrorMessage);
                var service = new ProjectService();
                var app = loaded.Solution.Projects.Single(p => p.Name == "app");
                Assert.IsTrue(service.MoveProject(loaded.Solution, app, 1));
                Assert.IsTrue(service.MoveProject(loaded.Solution, app, 1));

                CollectionAssert.AreEqual(new[] { "lib10", "lib11", "app" },
                    loaded.Solution.Projects.Select(p => p.Name).ToArray());
                StringAssert.Contains(File.ReadAllText(Path.Combine(root, "pyproject.toml")),
                    "project-order = [\"lib10\", \"lib11\", \"app\"]");

                var reloaded = loader.LoadAsync(Path.Combine(root, "pyproject.toml")).Result;
                Assert.IsTrue(reloaded.Success, reloaded.ErrorMessage);
                CollectionAssert.AreEqual(new[] { "lib10", "lib11", "app" },
                    reloaded.Solution.Projects.Select(p => p.Name).ToArray());
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void WriteMember(string root, string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"),
                "[project]\nname = \"" + name + "\"\nrequires-python = \">=3.11\"\n\n" +
                "[tool.helixstudio]\nkind = \"library\"\n");
        }
    }
}
