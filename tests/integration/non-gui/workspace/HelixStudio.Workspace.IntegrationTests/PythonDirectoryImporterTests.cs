using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PythonDirectoryImporterTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "imp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WritePy(string dir, string name, string body = "")
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), body);
        }

        [TestMethod]
        public void ReturnsErrorWhenSourceMissing()
        {
            PythonDirectoryImporter imp = new PythonDirectoryImporter();
            ImportResult r = imp.Import(new ImportOptions { SourceRoot = "C:\\nope\\nonexistent\\dir" });
            Assert.IsFalse(r.Success);
            StringAssert.Contains(r.ErrorMessage, "does not exist");
        }

        [TestMethod]
        public void DetectsPackageWithInit()
        {
            string root = TempDir();
            string pkg = Path.Combine(root, "mypkg");
            WritePy(pkg, "__init__.py");
            WritePy(pkg, "module.py", "def f(): pass\n");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions { SourceRoot = root, SolutionName = "x" });
            Assert.IsTrue(r.Success, r.ErrorMessage);
            Assert.AreEqual(1, r.ProjectCount);
            Assert.AreEqual("mypkg", r.Packages[0].Name);
            CollectionAssert.AreEquivalent(new[] { "__init__.py", "module.py" }, r.Packages[0].PyFiles);
        }

        [TestMethod]
        public void DetectsNestedPackagesAsSeparateProjects()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "outer"), "__init__.py");
            WritePy(Path.Combine(root, "outer"), "a.py");
            WritePy(Path.Combine(root, "outer", "inner"), "__init__.py");
            WritePy(Path.Combine(root, "outer", "inner"), "b.py");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions { SourceRoot = root, SolutionName = "x" });
            Assert.IsTrue(r.Success);
            Assert.AreEqual(2, r.ProjectCount);
            CollectionAssert.AreEquivalent(new[] { "outer", "inner" }, r.Packages.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void SynthesizesRootProjectForLooseScripts()
        {
            string root = TempDir();
            WritePy(root, "script.py");
            WritePy(root, "util.py");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions { SourceRoot = root, SolutionName = "tools" });
            Assert.IsTrue(r.Success);
            Assert.AreEqual(1, r.ProjectCount);
            Assert.IsTrue(r.Packages[0].IsSynthesized);
            Assert.IsTrue(r.Diagnostics.Any(d => d.IndexOf("Synthesized", StringComparison.Ordinal) >= 0));
        }

        [TestMethod]
        public void IgnoresStandardDirectories()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "pkg"), "__init__.py");
            WritePy(Path.Combine(root, ".venv"), "__init__.py");
            WritePy(Path.Combine(root, "__pycache__"), "a.py");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions { SourceRoot = root, SolutionName = "x" });
            Assert.IsTrue(r.Success);
            Assert.AreEqual(1, r.ProjectCount);
            Assert.AreEqual("pkg", r.Packages[0].Name);
        }

        [TestMethod]
        public void WritesSlnAndPyProjFiles()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "core"), "__init__.py");
            WritePy(Path.Combine(root, "core"), "m.py");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions
            {
                SourceRoot = root,
                SolutionName = "proj",
                TargetFormat = ImportTargetFormat.VisualStudio
            });
            Assert.IsTrue(r.Success);
            Assert.IsTrue(File.Exists(r.SlnPath));
            Assert.IsTrue(File.Exists(Path.Combine(root, "core", "core.pyproj")));
        }

        [TestMethod]
        public void WritesPyProjectTomlWhenAbsent()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "pkg"), "__init__.py");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions { SourceRoot = root, SolutionName = "proj" });
            Assert.IsTrue(r.Success);
            Assert.IsTrue(File.Exists(r.PyProjectTomlPath));
        }

        [TestMethod]
        public void PreservesExistingPyProjectToml_VisualStudioTarget()
        {
            // VisualStudio-target imports must not touch an existing root pyproject.toml.
            // (TOML-target imports additively merge — covered separately.)
            string root = TempDir();
            WritePy(Path.Combine(root, "pkg"), "__init__.py");
            string pyproject = Path.Combine(root, "pyproject.toml");
            string original = "[project]\nname = \"handcrafted\"\nrequires-python = \">=3.10\"\n# preserve me\n";
            File.WriteAllText(pyproject, original);

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions
            {
                SourceRoot = root,
                SolutionName = "proj",
                TargetFormat = ImportTargetFormat.VisualStudio
            });
            Assert.IsTrue(r.Success);
            Assert.AreEqual(original, File.ReadAllText(pyproject));
            Assert.IsTrue(r.Diagnostics.Any(d => d.IndexOf("preserved", StringComparison.Ordinal) >= 0));
        }

        [TestMethod]
        public void RejectsExistingSlnWithoutOverwrite()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "pkg"), "__init__.py");
            File.WriteAllText(Path.Combine(root, "proj.sln"), "existing");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions
            {
                SourceRoot = root,
                SolutionName = "proj",
                TargetFormat = ImportTargetFormat.VisualStudio
            });
            Assert.IsFalse(r.Success);
            StringAssert.Contains(r.ErrorMessage, "already exists");
        }

        [TestMethod]
        public void AllowsOverwriteWhenRequested()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "pkg"), "__init__.py");
            File.WriteAllText(Path.Combine(root, "proj.sln"), "existing");

            ImportResult r = new PythonDirectoryImporter().Import(
                new ImportOptions
                {
                    SourceRoot = root,
                    SolutionName = "proj",
                    OverwriteExisting = true,
                    TargetFormat = ImportTargetFormat.VisualStudio
                });
            Assert.IsTrue(r.Success, r.ErrorMessage);
        }

        [TestMethod]
        public void DefaultTargetFormatIsTomlWorkspace()
        {
            Assert.AreEqual(ImportTargetFormat.TomlWorkspace, new ImportOptions().TargetFormat);
        }

        [TestMethod]
        public async Task TomlWorkspaceEndToEnd_ProducesLoadableWorkspace()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "pkg_a"), "__init__.py");
            WritePy(Path.Combine(root, "pkg_a"), "a.py", "class A:\n    pass\n");
            WritePy(Path.Combine(root, "pkg_b"), "__init__.py");
            WritePy(Path.Combine(root, "pkg_b"), "b.py", "class B:\n    pass\n");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions
            {
                SourceRoot = root,
                SolutionName = "ws",
                TargetFormat = ImportTargetFormat.TomlWorkspace,
                ConflictResolver = new SilentPyProjectConflictResolver(PyProjectConflictResolution.Overwrite)
            });
            Assert.IsTrue(r.Success, r.ErrorMessage);

            PyProjectMetadata meta = PyProjectReader.Read(r.PyProjectTomlPath);
            CollectionAssert.AreEquivalent(new[] { "pkg_a", "pkg_b" }, meta.WorkspaceMembers);
            Assert.IsTrue(meta.IsWorkspace);

            Assert.IsTrue(File.Exists(Path.Combine(root, "pkg_a", "pyproject.toml")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "pkg_b", "pyproject.toml")));

            SolutionLoadResult load = await new SolutionService().LoadAsync(r.PyProjectTomlPath);
            Assert.IsTrue(load.Success);
            Assert.AreEqual(2, load.Solution.Projects.Count);
        }

        [TestMethod]
        public void BothFormats_EmitSlnAndWorkspaceTogether()
        {
            string root = TempDir();
            WritePy(Path.Combine(root, "pkg_a"), "__init__.py");

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions
            {
                SourceRoot = root,
                SolutionName = "ws",
                TargetFormat = ImportTargetFormat.Both,
                ConflictResolver = new SilentPyProjectConflictResolver(PyProjectConflictResolution.Overwrite)
            });
            Assert.IsTrue(r.Success, r.ErrorMessage);
            Assert.IsTrue(File.Exists(r.SlnPath), "sln must exist when TargetFormat=Both");
            Assert.IsTrue(File.Exists(Path.Combine(root, "pkg_a", "pkg_a.pyproj")), "pyproj must exist when TargetFormat=Both");
            Assert.IsTrue(File.Exists(r.PyProjectTomlPath));
            PyProjectMetadata meta = PyProjectReader.Read(r.PyProjectTomlPath);
            Assert.IsTrue(meta.IsWorkspace);
        }

        [TestMethod]
        public void TomlWorkspace_KeepResolutionPreservesExistingMemberToml()
        {
            string root = TempDir();
            string pkgDir = Path.Combine(root, "pkg_a");
            WritePy(pkgDir, "__init__.py");
            string memberToml = Path.Combine(pkgDir, "pyproject.toml");
            string original = "[project]\nname = \"handcrafted_alpha\"\n# sentinel\n";
            File.WriteAllText(memberToml, original);

            ImportResult r = new PythonDirectoryImporter().Import(new ImportOptions
            {
                SourceRoot = root,
                SolutionName = "ws",
                TargetFormat = ImportTargetFormat.TomlWorkspace,
                ConflictResolver = new SilentPyProjectConflictResolver(PyProjectConflictResolution.Keep)
            });
            Assert.IsTrue(r.Success);
            Assert.AreEqual(original, File.ReadAllText(memberToml), "Keep must leave member file untouched.");
            Assert.IsTrue(r.ConflictsResolved.Any(s => s.Contains("kept")));
        }

        [TestMethod]
        public async Task SamplePackageRoundTripsThroughSolutionService()
        {
            // Import the checked-in SamplePackage fixture into a temp copy, then verify
            // the generated pyproject.toml loads via SolutionService and still yields
            // the expected number of types.
            string fixtureRoot = Path.GetDirectoryName(FindFixturePyProject());
            string temp = TempDir();
            CopyDirectory(fixtureRoot, temp);

            ImportResult r = new PythonDirectoryImporter().Import(
                new ImportOptions { SourceRoot = temp, SolutionName = "sample", OverwriteExisting = true });
            Assert.IsTrue(r.Success, r.ErrorMessage);

            SolutionLoadResult load = await new SolutionService().LoadAsync(r.PyProjectTomlPath);
            Assert.IsTrue(load.Success, load.ErrorMessage);
            // SamplePackage ships with Animal, Dog, Cat, Shape, Circle, Square — 6 top-level classes.
            var names = load.Solution.GetAllTypes().Select(t => t.Name).ToList();
            foreach (string expected in new[] { "Animal", "Dog", "Cat", "Shape", "Circle", "Square" })
                CollectionAssert.Contains(names, expected);
        }

        [TestMethod]
        public void AnalysisOnly_ClassifiesAndDoesNotModifyFilesystem()
        {
            string root = TempDir();
            WritePy(root, "console_main.py",
                "def main():\n    return 0\n\nif __name__ == \"__main__\":\n    raise SystemExit(main())\n");
            string output = Path.Combine(root, "generated", "workspace");
            string[] before = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

            ImportResult result = new PythonDirectoryImporter().Import(new ImportOptions
            {
                SourceRoot = root,
                OutputRoot = output,
                SolutionName = "console",
                AnalysisOnly = true
            });

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(result.IsAnalysisOnly);
            Assert.AreEqual("Single executable project", result.RepositoryClassification);
            Assert.AreEqual(1, result.ProjectCount);
            Assert.IsFalse(Directory.Exists(output), "Analysis must not create OutputRoot.");
            CollectionAssert.AreEqual(
                before,
                Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }

        private static string FindFixturePyProject()
        {
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            DirectoryInfo d = new DirectoryInfo(baseDir);
            while (d != null)
            {
                string p1 = Path.Combine(d.FullName, "tests", "fixtures", "python", "python-test-sample-oop", "lib_core", "pyproject.toml");
                if (File.Exists(p1)) return p1;
                string p2 = Path.Combine(d.FullName, "tests", "fixtures", "python", "python-test-sample-oop", "lib_core", "pyproject.toml");
                if (File.Exists(p2)) return p2;
                d = d.Parent;
            }
            throw new FileNotFoundException("SamplePackage fixture not found.");
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
            foreach (string sub in Directory.GetDirectories(src))
                CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
        }
    }
}
