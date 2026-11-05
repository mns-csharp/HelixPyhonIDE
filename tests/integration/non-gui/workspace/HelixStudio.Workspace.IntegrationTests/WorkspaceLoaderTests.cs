using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Python;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class WorkspaceLoaderTests
    {
        private static string FindFixture()
        {
            DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null)
            {
                string p1 = Path.Combine(d.FullName, "Fixtures", "Workspace", "pyproject.toml");
                if (File.Exists(p1)) return p1;
                string p2 = Path.Combine(d.FullName, "tests", "fixtures", "python", "python-test-sample-oop", "pyproject.toml");
                if (File.Exists(p2)) return p2;
                d = d.Parent;
            }
            throw new FileNotFoundException("Workspace fixture not found.");
        }

        [TestMethod]
        public async Task Loads_MultipleProjectsPerMember()
        {
            string rootToml = FindFixture();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(rootToml);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual("python-test-sample", result.Solution.Name);
            Assert.AreEqual(6, result.Solution.Projects.Count, "Workspace should load each declared member.");
            var names = result.Solution.Projects.Select(p => p.Name).OrderBy(n => n).ToList();
            CollectionAssert.AreEqual(new[] { "app_cli", "app_gui", "app_server", "lib_core", "lib_utils", "my_unit_test_package" }, names);
        }

        [TestMethod]
        public async Task EachMemberScansOnlyItsOwnSubtree()
        {
            string rootToml = FindFixture();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(rootToml);
            ProjectModel core = result.Solution.Projects.First(p => p.Name == "lib_core");
            ProjectModel gui = result.Solution.Projects.First(p => p.Name == "app_gui");
            Assert.IsTrue(core.Types.Any(t => t.Name == "Animal"));
            Assert.IsTrue(core.Types.Any(t => t.Name == "Shape"));
            Assert.IsFalse(gui.Types.Any(t => t.Name == "Animal"));
            Assert.IsFalse(gui.Types.Any(t => t.Name == "Shape"));
        }

        [TestMethod]
        public async Task ClassifiesWorkspaceMemberTypesCorrectly()
        {
            string rootToml = FindFixture();
            SolutionLoadResult result = await new SolutionService().LoadAsync(rootToml);
            TypeModel animal = result.Solution.GetAllTypes().First(t => t.Name == "Animal");
            TypeModel shape = result.Solution.GetAllTypes().First(t => t.Name == "Shape");
            Assert.AreEqual(TypeKind.AbstractClass, animal.TypeKind);
            Assert.AreEqual(TypeKind.Interface, shape.TypeKind);
        }

        [TestMethod]
        public async Task BackwardsCompat_SingleProjectPyProjectStillLoads()
        {
            // A member project without [tool.uv.workspace] must still load as one project.
            DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string fixturePath = null;
            while (d != null)
            {
                string p = Path.Combine(d.FullName, "tests", "fixtures", "python", "python-test-sample-oop", "lib_core", "pyproject.toml");
                if (File.Exists(p)) { fixturePath = p; break; }
                string p2 = Path.Combine(d.FullName, "tests", "fixtures", "python", "python-test-sample-oop", "lib_core", "pyproject.toml");
                if (File.Exists(p2)) { fixturePath = p2; break; }
                d = d.Parent;
            }
            Assert.IsNotNull(fixturePath);
            SolutionLoadResult result = await new SolutionService().LoadAsync(fixturePath);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, result.Solution.Projects.Count, "Non-workspace pyproject.toml must stay single-project.");
            var names = result.Solution.GetAllTypes().Select(t => t.Name).ToList();
            foreach (string expected in new[] { "Animal", "Dog", "Cat", "Shape", "Circle", "Square" })
                CollectionAssert.Contains(names, expected);
        }
    }
}
