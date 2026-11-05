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
    public class EndToEndTests
    {
        private static string FixtureDir()
        {
            // Walk up from the test assembly location to find the fixture under tests/.
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            DirectoryInfo d = new DirectoryInfo(baseDir);
            while (d != null)
            {
                string candidate = Path.Combine(d.FullName, "tests", "fixtures", "python", "python-test-sample-oop", "lib_core", "pyproject.toml");
                if (File.Exists(candidate)) return candidate;
                // Repo-root candidate
                string repoCandidate = Path.Combine(d.FullName, "tests", "fixtures", "python", "python-test-sample-oop", "lib_core", "pyproject.toml");
                if (File.Exists(repoCandidate)) return repoCandidate;
                d = d.Parent;
            }
            throw new FileNotFoundException("SamplePackage fixture not found in any ancestor directory.");
        }

        [TestMethod]
        public async Task LoadsSamplePackage()
        {
            string pyproject = FixtureDir();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual("lib_core", result.Solution.Name);
            Assert.AreEqual(1, result.Solution.Projects.Count);
        }

        [TestMethod]
        public async Task FindsExpectedTypes()
        {
            string pyproject = FixtureDir();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            var names = result.Solution.GetAllTypes().Select(t => t.Name).ToList();
            CollectionAssert.Contains(names, "Animal");
            CollectionAssert.Contains(names, "Dog");
            CollectionAssert.Contains(names, "Cat");
            CollectionAssert.Contains(names, "Shape");
            CollectionAssert.Contains(names, "Circle");
            CollectionAssert.Contains(names, "Square");
        }

        [TestMethod]
        public async Task ClassifiesInterfaceAndAbstract()
        {
            string pyproject = FixtureDir();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            TypeModel animal = result.Solution.GetAllTypes().First(t => t.Name == "Animal");
            TypeModel shape = result.Solution.GetAllTypes().First(t => t.Name == "Shape");
            Assert.AreEqual(TypeKind.AbstractClass, animal.TypeKind);
            Assert.AreEqual(TypeKind.Interface, shape.TypeKind);
        }

        [TestMethod]
        public async Task InheritanceResolvesToBinding()
        {
            string pyproject = FixtureDir();
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            TypeModel dog = result.Solution.GetAllTypes().First(t => t.Name == "Dog");
            Assert.AreEqual(1, dog.BaseTypes.Count);
            Assert.AreEqual("Animal", dog.BaseTypes[0].SimpleName);
            // After binding in SolutionService, the TypeReference should resolve to the actual Animal.
            Assert.IsNotNull(dog.BaseTypes[0].ResolvedType);
            Assert.AreEqual("Animal", dog.BaseTypes[0].ResolvedType.Name);
        }

        [TestMethod]
        public async Task AddTypeWritesPythonFile()
        {
            // Copy fixture to a temp location so we don't mutate the checked-in fixture.
            string fixture = Path.GetDirectoryName(FixtureDir());
            string tempRoot = Path.Combine(Path.GetTempPath(), "helix_e2e_" + System.Guid.NewGuid().ToString("N"));
            CopyDirectory(fixture, tempRoot);
            string pyproject = Path.Combine(tempRoot, "pyproject.toml");
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            ProjectModel project = result.Solution.Projects[0];
            TypeService types = new TypeService();
            TypeModel fish = types.AddClass(project, "Fish", "sample.animals");
            Assert.IsNotNull(fish.SourceFilePath);
            Assert.IsTrue(File.Exists(fish.SourceFilePath));
            string body = File.ReadAllText(fish.SourceFilePath);
            StringAssert.Contains(body, "class Fish:");
        }

        [TestMethod]
        public async Task AddMethodRegeneratesClassBody()
        {
            string fixture = Path.GetDirectoryName(FixtureDir());
            string tempRoot = Path.Combine(Path.GetTempPath(), "helix_e2e_" + System.Guid.NewGuid().ToString("N"));
            CopyDirectory(fixture, tempRoot);
            string pyproject = Path.Combine(tempRoot, "pyproject.toml");
            SolutionService svc = new SolutionService();
            SolutionLoadResult result = await svc.LoadAsync(pyproject);
            ProjectModel project = result.Solution.Projects[0];
            TypeService types = new TypeService();
            TypeModel fish = types.AddClass(project, "Fish", "sample.animals");
            MemberModel swim = new MemberModel("swim", MemberKind.Method) { ReturnType = "None" };
            types.AddMember(fish, swim);
            string body = File.ReadAllText(fish.SourceFilePath);
            StringAssert.Contains(body, "def swim(self) -> None:");
        }

        // Iter 264 (user bug: double-clicking main.py in the tree doesn't open it). The tree's file
        // node carries FileGroup.FilePath = NormalizeFileKey(symbol.SourceFilePath), and both the
        // double-click handler and EditorCoordinator.OpenInEditor bail on !File.Exists. So every
        // recorded source path for a root-level main.py console app must be File.Exists-able, and
        // main.py must be discovered as a symbol source (else it gets no node at all).
        [TestMethod]
        public async Task ConsoleApp_RootMainPy_HasOpenableSourcePath()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_console_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                    "[project]\nname = \"myapp\"\nversion = \"0.1.0\"\nrequires-python = \">=3.11\"\n");
                File.WriteAllText(Path.Combine(root, "main.py"),
                    "def main():\n    print(\"hello\")\n\n\nif __name__ == \"__main__\":\n    main()\n");

                SolutionLoadResult result = await new SolutionService().LoadAsync(Path.Combine(root, "pyproject.toml"));
                Assert.IsTrue(result.Success, result.ErrorMessage);
                ProjectModel project = result.Solution.Projects[0];

                var paths = new System.Collections.Generic.List<string>();
                foreach (TypeModel t in project.Types) if (!string.IsNullOrEmpty(t.SourceFilePath)) paths.Add(t.SourceFilePath);
                foreach (FunctionModel f in project.Functions) if (!string.IsNullOrEmpty(f.SourceFilePath)) paths.Add(f.SourceFilePath);

                // Every recorded path (as the tree normalizes it, '\' -> '/') must be openable.
                foreach (string p in paths)
                    Assert.IsTrue(File.Exists(p.Replace('\\', '/')),
                        "recorded source path is not File.Exists-able (double-click would no-op): " + p);

                bool mainFound = paths.Any(p => Path.GetFileName(p).Equals("main.py", System.StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(mainFound,
                    "main.py was not discovered as a symbol source, so it gets no tree node. Recorded: [" +
                    string.Join(", ", paths) + "]");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string f in Directory.GetFiles(sourceDir))
                File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), overwrite: true);
            foreach (string sub in Directory.GetDirectories(sourceDir))
                CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }
}
