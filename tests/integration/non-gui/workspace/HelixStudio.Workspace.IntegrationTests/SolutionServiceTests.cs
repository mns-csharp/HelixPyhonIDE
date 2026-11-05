using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class SolutionServiceTests
    {
        private static string BuildSamplePackage()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_sol_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string pyproject = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(pyproject,
                "[project]\nname = \"sample\"\nrequires-python = \">=3.12\"\ndependencies = []\n");
            Directory.CreateDirectory(Path.Combine(root, "src", "sample"));
            File.WriteAllText(Path.Combine(root, "src", "sample", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "src", "sample", "animals.py"),
                "from abc import ABC, abstractmethod\n\n" +
                "class Animal(ABC):\n" +
                "    @abstractmethod\n" +
                "    def speak(self) -> str:\n" +
                "        pass\n\n" +
                "class Dog(Animal):\n" +
                "    def speak(self) -> str:\n" +
                "        return 'woof'\n\n" +
                "class Cat(Animal):\n" +
                "    def speak(self) -> str:\n" +
                "        return 'meow'\n");
            File.WriteAllText(Path.Combine(root, "src", "sample", "shapes.py"),
                "from typing import Protocol\n\n" +
                "class Shape(Protocol):\n" +
                "    def area(self) -> float: ...\n\n" +
                "class Circle:\n" +
                "    def area(self) -> float:\n" +
                "        return 3.14\n");
            // Ignored directory should be skipped
            Directory.CreateDirectory(Path.Combine(root, ".venv"));
            File.WriteAllText(Path.Combine(root, ".venv", "garbage.py"), "class Ignored: pass\n");
            return pyproject;
        }

        [TestMethod]
        public async Task LoadsPyProjectAsSolution()
        {
            string pyproject = BuildSamplePackage();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            SolutionModel sln = result.Solution;
            Assert.AreEqual("sample", sln.Name);
            Assert.AreEqual(1, sln.Projects.Count);
            ProjectModel project = sln.Projects[0];
            Assert.AreEqual("3.12", project.PythonVersion);
            Assert.AreEqual(5, project.Types.Count);
        }

        // --- SRS §5.9: Exclude/Include from Project ---

        [TestMethod]
        public async Task ExcludedFile_IsSkipped_ButItsSiblingLoadsNormally()
        {
            string pyproject = BuildSamplePackage();
            File.AppendAllText(pyproject, "\n[tool.helixstudio]\nexcluded = [\"src/sample/shapes.py\"]\n");

            SolutionLoadResult result = await new SolutionService().LoadAsync(pyproject);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            ProjectModel project = result.Solution.Projects[0];

            Assert.IsFalse(project.Types.Any(t => t.Name == "Circle"), "excluded file's types must not load");
            Assert.IsFalse(project.Types.Any(t => t.Name == "Shape"));
            Assert.IsTrue(project.Types.Any(t => t.Name == "Dog"), "the sibling file still loads normally");
            CollectionAssert.AreEqual(new[] { "src/sample/shapes.py" }, project.ExcludedPaths.ToArray());
        }

        [TestMethod]
        public async Task ExcludedDirectory_SkipsEverythingBeneathIt()
        {
            string pyproject = BuildSamplePackage();
            string subDir = Path.Combine(Path.GetDirectoryName(pyproject), "src", "sample", "sub");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "hidden.py"), "class HiddenType:\n    pass\n");
            File.AppendAllText(pyproject, "\n[tool.helixstudio]\nexcluded = [\"src/sample/sub\"]\n");

            SolutionLoadResult result = await new SolutionService().LoadAsync(pyproject);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            ProjectModel project = result.Solution.Projects[0];

            Assert.IsFalse(project.Types.Any(t => t.Name == "HiddenType"),
                           "a directory exclusion covers every file beneath it");
            Assert.AreEqual(5, project.Types.Count, "the rest of the package is unaffected");
        }

        [TestMethod]
        public async Task NoExclusions_LeavesExcludedPathsEmpty()
        {
            string pyproject = BuildSamplePackage();
            SolutionLoadResult result = await new SolutionService().LoadAsync(pyproject);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(0, result.Solution.Projects[0].ExcludedPaths.Count);
        }

        // --- PSM-08/10: startup package model + TOML round-trip (iter 390) ---

        private static string BuildWorkspace(string appCliStartup, string appSrvStartup)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_ws_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"ws\"\nrequires-python = \">=3.12\"\ndependencies = []\n\n" +
                "[tool.uv.workspace]\nmembers = [\"src/lib\", \"src/app_cli\", \"src/app_srv\"]\n");
            void Member(string rel, string body)
            {
                string dir = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "pyproject.toml"), body);
            }
            Member("src/lib",
                "[project]\nname = \"lib\"\nrequires-python = \">=3.12\"\ndependencies = []\n\n" +
                "[tool.helixstudio]\nkind = \"library\"\n");
            Member("src/app_cli",
                "[project]\nname = \"app_cli\"\nrequires-python = \">=3.12\"\ndependencies = []\n\n" +
                "[tool.helixstudio]\nkind = \"app\"\n" + appCliStartup);
            Member("src/app_srv",
                "[project]\nname = \"app_srv\"\nrequires-python = \">=3.12\"\ndependencies = []\n\n" +
                "[tool.helixstudio]\nkind = \"app\"\n" + appSrvStartup);
            return Path.Combine(root, "pyproject.toml");
        }

        private static ProjectModel Proj(SolutionModel sln, string name)
        {
            return sln.Projects.First(p => p.Name == name);
        }

        [TestMethod]
        public async Task StartupFlagReadFromToml_AndIsExclusive()
        {
            string pyproject = BuildWorkspace("startup = true\n", "");
            SolutionModel sln = (await new SolutionService().LoadAsync(pyproject)).Solution;
            Assert.IsTrue(Proj(sln, "app_cli").IsStartup, "app_cli should be startup (marked in TOML)");
            Assert.IsFalse(Proj(sln, "app_srv").IsStartup);
            Assert.IsFalse(Proj(sln, "lib").IsStartup, "a library is never startup");
        }

        [TestMethod]
        public async Task MultipleExecutablesNoneMarked_NoStartup()
        {
            // PSM-08: with >1 executable and none marked, none is startup (user must choose).
            string pyproject = BuildWorkspace("", "");
            SolutionModel sln = (await new SolutionService().LoadAsync(pyproject)).Solution;
            Assert.IsFalse(sln.Projects.Any(p => p.IsStartup));
        }

        [TestMethod]
        public async Task LibraryMarkedStartup_IsIgnored()
        {
            // A non-executable package cannot be startup even if the TOML says so.
            string root = Path.Combine(Path.GetTempPath(), "helix_ws_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"ws\"\nrequires-python = \">=3.12\"\ndependencies = []\n\n" +
                "[tool.uv.workspace]\nmembers = [\"src/lib\", \"src/app\"]\n");
            string libDir = Path.Combine(root, "src", "lib"); Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "pyproject.toml"),
                "[project]\nname = \"lib\"\nrequires-python = \">=3.12\"\ndependencies = []\n\n[tool.helixstudio]\nkind = \"library\"\nstartup = true\n");
            string appDir = Path.Combine(root, "src", "app"); Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "pyproject.toml"),
                "[project]\nname = \"app\"\nrequires-python = \">=3.12\"\ndependencies = []\n\n[tool.helixstudio]\nkind = \"app\"\n");
            SolutionModel sln = (await new SolutionService().LoadAsync(Path.Combine(root, "pyproject.toml"))).Solution;
            Assert.IsFalse(Proj(sln, "lib").IsStartup, "library must not be startup");
            // Sole executable becomes the implicit startup.
            Assert.IsTrue(Proj(sln, "app").IsStartup, "sole executable is the implicit startup");
        }

        [TestMethod]
        public async Task SkipsIgnoredDirectories()
        {
            string pyproject = BuildSamplePackage();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            Assert.IsTrue(result.Solution.GetAllTypes().All(t => t.Name != "Ignored"));
        }

        [TestMethod]
        public async Task FindsProtocolAndAbc()
        {
            string pyproject = BuildSamplePackage();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            SolutionModel sln = result.Solution;
            Assert.AreEqual(TypeKind.Interface, sln.GetAllTypes().First(t => t.Name == "Shape").TypeKind);
            Assert.AreEqual(TypeKind.AbstractClass, sln.GetAllTypes().First(t => t.Name == "Animal").TypeKind);
        }

        [TestMethod]
        public void CreateNewScaffoldsPyProject()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_new_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            SolutionService svc = new SolutionService();
            SolutionModel sln = svc.CreateNew(dir, "hello");
            Assert.IsTrue(File.Exists(Path.Combine(dir, "pyproject.toml")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "hello", "__init__.py")));
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "src")));
            Assert.AreEqual("hello", sln.Name);
        }

        [TestMethod]
        public async Task ReloadPreservesResults()
        {
            string pyproject = BuildSamplePackage();
            SolutionService svc = new SolutionService();
            SolutionLoadResult first = await svc.LoadAsync(pyproject);
            SolutionLoadResult second = await svc.ReloadAsync(first.Solution);
            Assert.IsTrue(second.Success);
            Assert.AreEqual(first.Solution.GetAllTypes().Count(), second.Solution.GetAllTypes().Count());
        }

        [TestMethod]
        public async Task LoadingSlnResolvesToAdjacentPyProjectToml()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_sln_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            HelixPythonNamespace.Workspace.Python.PythonProjectScaffolder.CreateNewProject(
                dir, "companion", ProjectKind.ClassLibrary,
                HelixPythonNamespace.Workspace.Python.SolutionFormat.Sln);
            string slnPath = Path.Combine(dir, "companion.sln");
            Assert.IsTrue(File.Exists(slnPath));

            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(slnPath);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual("companion", result.Solution.Name);
            Assert.AreEqual(1, result.Solution.Projects.Count);
        }

        [TestMethod]
        public async Task LoadsSlnxWithoutPyProjectToml()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_slnx_load_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string name in new[] { "PythonApplication1", "PythonApplication2" })
                {
                    string projectDir = Path.Combine(root, name);
                    Directory.CreateDirectory(projectDir);
                    File.WriteAllText(Path.Combine(projectDir, name + ".pyproj"), "<Project />");
                }
                string slnx = Path.Combine(root, "sample.slnx");
                File.WriteAllText(slnx, "<Solution><Project Path=\"PythonApplication1/PythonApplication1.pyproj\" /><Project Path=\"PythonApplication2/PythonApplication2.pyproj\" /></Solution>");
                SolutionLoadResult result = await new SolutionService().LoadAsync(slnx);
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(2, result.Solution.Projects.Count);
                CollectionAssert.AreEquivalent(new[] { "PythonApplication1", "PythonApplication2" }, result.Solution.Projects.Select(p => p.Name).ToList());
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
