using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.ImportEngine.Analysis;
using HelixPythonNamespace.ImportEngine.Execution;
using HelixPythonNamespace.ImportEngine.Planning;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.ImportEngine.Tests
{
    [TestClass]
    public class ImportPlanExecutorTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_iex_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void W(string relPath, string content = "")
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        private ImportPlan DefaultPlan(string solutionName = "demo")
        {
            ImportAnalysis analysis = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(analysis.Success, analysis.ErrorMessage);
            return ImportPlanFactory.CreateDefault(analysis, solutionName);
        }

        [TestMethod]
        public void DefaultPlan_DeduplicatesNames_AndPicksSoleExecutableStartup()
        {
            W(@"a\core\__init__.py");
            W(@"b\core\__init__.py");
            W(@"app\__init__.py");
            W(@"app\main.py", "def main():\n    pass\n");

            ImportPlan plan = DefaultPlan();
            var names = plan.SeparateProjects.Select(p => p.FinalName).ToList();
            Assert.AreEqual(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                            "duplicate names must be auto-suffixed");
            Assert.AreEqual("app", plan.StartupProjectName);
        }

        [TestMethod]
        public void Execute_InPlace_GeneratesLoadableWorkspace_WithStartup()
        {
            W(@"lib_one\__init__.py");
            W(@"lib_one\mod.py", "X = 1\n");  // constant-shaped: stays untouched by the OOP guard
            W(@"app\__init__.py");
            W(@"app\main.py", "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");

            ImportPlan plan = DefaultPlan();
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(2, result.ProjectCount);
            Assert.IsTrue(File.Exists(result.PyProjectTomlPath));

            // The startup flag landed in the app's member toml.
            string appToml = File.ReadAllText(Path.Combine(_root, "app", "pyproject.toml"));
            StringAssert.Contains(appToml, "startup");

            // The generated workspace loads through the production loader.
            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(result.PyProjectTomlPath);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            Assert.AreEqual(2, load.Result.Solution.Projects.Count);
            var startup = load.Result.Solution.Projects.FirstOrDefault(p => p.IsStartup);
            Assert.IsNotNull(startup, "startup project restored from TOML");
            Assert.AreEqual("app", startup.Name);
        }

        [TestMethod]
        public void Execute_Rename_MovesDirectoryAndUsesNewName()
        {
            W(@"oldname\__init__.py");
            W(@"oldname\mod.py", "X = 1\n");

            ImportPlan plan = DefaultPlan();
            plan.Packages.Single(p => p.Source.Name == "oldname").FinalName = "newname";
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "newname")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "oldname")));
            Assert.AreEqual(1, result.Moves.Count(m => m.Reason == "rename"));
            string rootToml = File.ReadAllText(result.PyProjectTomlPath);
            StringAssert.Contains(rootToml, "newname");
        }

        [TestMethod]
        public void Execute_Rename_RewritesImportStatementsAcrossTheTree()
        {
            W(@"oldname\__init__.py");
            W(@"oldname\mod.py", "x = 1\n");
            W(@"app\__init__.py");
            // RewriteModuleImports only rewrites the module path inside import
            // statements — not every bare reference to the name in expressions
            // (that would need full scope analysis to do safely), so the usage
            // below (`print(x, mod.x)`) deliberately avoids naming the package
            // directly outside an import statement.
            W(@"app\main.py",
              "import oldname\n" +
              "from oldname.mod import x\n" +
              "from oldname import mod\n" +
              "print(x, mod.x)\n");

            ImportPlan plan = DefaultPlan();
            // This test pins byte-exact import-statement rewriting only; the OOP guard
            // would reject the deliberately procedural fixture (module-level print).
            plan.ConvertToObjectOriented = false;
            plan.Packages.Single(p => p.Source.Name == "oldname").FinalName = "newname";
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);

            string mainSource = File.ReadAllText(Path.Combine(_root, "app", "main.py"));
            StringAssert.Contains(mainSource, "import newname");
            StringAssert.Contains(mainSource, "from newname.mod import x");
            StringAssert.Contains(mainSource, "from newname import mod");
            Assert.IsFalse(mainSource.Contains("oldname"), "every import statement's module path is rewritten");
            Assert.IsTrue(result.Notes.Any(n => n.Contains("oldname -> newname")),
                          "the rewrite is reported in the execution notes");
        }

        [TestMethod]
        public void Execute_NoRename_LeavesImportsUntouched()
        {
            W(@"pkg\__init__.py");
            W(@"app\__init__.py");
            W(@"app\main.py", "import pkg\nprint(pkg)\n");

            ImportPlan plan = DefaultPlan();
            plan.ConvertToObjectOriented = false;  // byte-for-byte assertion below
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);

            string mainSource = File.ReadAllText(Path.Combine(_root, "app", "main.py"));
            Assert.AreEqual("import pkg\nprint(pkg)\n", mainSource,
                            "no rename happened, so imports must be byte-for-byte unchanged");
        }

        [TestMethod]
        public void Execute_RearrangeToSrc_DoesNotRewriteImports()
        {
            // A src-layout move changes the package's PATH but not its importable
            // NAME, so `import alpha` must keep working unmodified.
            W(@"alpha\__init__.py");
            W(@"beta\__init__.py");
            W(@"beta\main.py", "import alpha\nprint(alpha)\n");

            ImportPlan plan = DefaultPlan();
            plan.ConvertToObjectOriented = false;  // byte-for-byte assertion below
            plan.Layout = ImportLayout.RearrangeToSrc;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);

            string mainSource = File.ReadAllText(Path.Combine(_root, "src", "beta", "main.py"));
            Assert.AreEqual("import alpha\nprint(alpha)\n", mainSource);
        }

        [TestMethod]
        public void Execute_RearrangeToSrc_MovesPackagesUnderSrc()
        {
            W(@"alpha\__init__.py");
            W(@"beta\__init__.py");

            ImportPlan plan = DefaultPlan();
            plan.Layout = ImportLayout.RearrangeToSrc;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "src", "alpha")));
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "src", "beta")));
            Assert.AreEqual(2, result.Moves.Count(m => m.Reason == "layout"));

            // Workspace still loads after the physical rearrangement.
            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(result.PyProjectTomlPath);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            Assert.AreEqual(2, load.Result.Solution.Projects.Count);
        }

        [TestMethod]
        public void Execute_RearrangeToSrc_RootIsThePackageItself_KeepsItInPlace()
        {
            // The source directory the user pointed the wizard at IS the package
            // (__init__.py sits directly at the root, no wrapper folder) — the
            // real-world shape that used to throw "Access to the path ... is
            // denied." because the executor tried to Directory.Move(root,
            // root\src\<name>), i.e. move the root into its own descendant.
            W(@"__init__.py");
            W(@"main.py", "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");

            ImportPlan plan = DefaultPlan();
            plan.Layout = ImportLayout.RearrangeToSrc;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "src")),
                           "nothing to move under src\\ when the root itself is the only package");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "main.py")), "source root left untouched");
            Assert.IsFalse(result.Moves.Any(m => m.Reason == "layout"));

            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(result.PyProjectTomlPath);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
        }

        [TestMethod]
        public void Execute_NeverOverwritesAnExistingTargetDirectory()
        {
            W(@"pkg\__init__.py");
            W(@"taken\occupied.txt", "here first");

            ImportPlan plan = DefaultPlan();
            plan.Packages.Single(p => p.Source.Name == "pkg").FinalName = "taken";
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "already exists");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "taken", "occupied.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "pkg")), "source untouched on failure");
        }

        [TestMethod]
        public void Execute_ExcludedAndPartOfParent_AreNotEmitted()
        {
            W(@"parent\__init__.py");
            W(@"parent\child\__init__.py");
            W(@"stray\__init__.py");

            ImportPlan plan = DefaultPlan();
            plan.Packages.Single(p => p.Source.Name == "child").Disposition = PackageDisposition.PartOfParent;
            plan.Packages.Single(p => p.Source.Name == "stray").Disposition = PackageDisposition.Excluded;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(1, result.ProjectCount);
            Assert.IsFalse(File.Exists(Path.Combine(_root, "stray", "pyproject.toml")),
                           "excluded package gets no member toml");
            Assert.IsFalse(File.Exists(Path.Combine(_root, "parent", "child", "pyproject.toml")),
                           "folded package gets no member toml");
            string rootToml = File.ReadAllText(result.PyProjectTomlPath);
            Assert.IsFalse(rootToml.Contains("stray"));
        }

        [TestMethod]
        public void Execute_DuplicateFinalNames_FailBeforeTouchingDisk()
        {
            W(@"a\core\__init__.py");
            W(@"b\core\__init__.py");

            ImportPlan plan = DefaultPlan();
            foreach (PackagePlan p in plan.Packages) p.FinalName = "core";  // undo the dedupe
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "share the name");
            Assert.IsFalse(File.Exists(Path.Combine(_root, "pyproject.toml")));
        }

        [TestMethod]
        public void Execute_ResolveEntryPoints_RenamesSurplusMains()
        {
            W(@"app\__init__.py");
            W(@"app\main.py", "def main():\n    pass\n");
            W(@"app\cli_main.py", "X = 1\n");  // entry-equivalent surplus (constant-shaped for the OOP guard)

            ImportPlan plan = DefaultPlan();
            plan.ResolveEntryPoints = true;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(result.Moves.Any(m => m.Reason == "entry-point"),
                          "surplus main module renamed");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "app", "main.py")), "canonical main kept");
            Assert.IsFalse(File.Exists(Path.Combine(_root, "app", "cli_main.py")), "surplus renamed away");
            Assert.IsNotNull(result.PostAudit);
        }

        [TestMethod]
        public void Execute_ResolveEntryPoints_MultipleMainFunctions_SplitsIntoMultipleExecutablePackages()
        {
            // Two files in the SAME package each declare a module-level def main() and share a
            // support module via a same-package relative import. A single package may only
            // declare one entry point (#3d) — the fix is to split each entry file into its OWN
            // executable package, with every non-entry file (here: shared.py, __init__.py)
            // extracted into one shared library package the app packages depend on. Every
            // caller-facing relative import must survive the move as a rewritten absolute one.
            W(@"app\__init__.py");
            W(@"app\shared.py", "def helper():\n    return 1\n");
            W(@"app\main.py",
              "from . import shared\n\ndef main():\n    return shared.helper()\n\nif __name__ == \"__main__\":\n    main()\n");
            W(@"app\other.py",
              "from . import shared\n\ndef main():\n    return shared.helper()\n\nif __name__ == \"__main__\":\n    main()\n");

            ImportPlan plan = DefaultPlan();
            plan.ResolveEntryPoints = true;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsNotNull(result.PostAudit);
            Assert.IsTrue(result.PostAudit.IsCompliant, "auto-fix must leave the solution compliant");
            Assert.IsFalse(result.Diagnostics.Any(d => d.Contains("Requires manual fix")));
            Assert.AreEqual(3, result.ProjectCount, "app_lib + main + other");

            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "app")), "the now-empty original package is removed");

            Assert.IsTrue(File.Exists(Path.Combine(_root, "app_lib", "shared.py")), "support module extracted to the shared lib");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "app_lib", "__init__.py")));

            Assert.IsTrue(File.Exists(Path.Combine(_root, "main", "main.py")), "original main.py kept its own name as a package");
            string mainSource = File.ReadAllText(Path.Combine(_root, "main", "main.py"));
            StringAssert.Contains(mainSource, "from app_lib import shared", "relative import rewritten to the new lib package");
            StringAssert.Contains(mainSource, "def main():");

            Assert.IsTrue(File.Exists(Path.Combine(_root, "other", "main.py")), "other.py renamed to main.py in its own package");
            string otherSource = File.ReadAllText(Path.Combine(_root, "other", "main.py"));
            StringAssert.Contains(otherSource, "from app_lib import shared");
            StringAssert.Contains(otherSource, "def main():");

            // The generated workspace loads through the production loader.
            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(result.PyProjectTomlPath);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            Assert.AreEqual(3, load.Result.Solution.Projects.Count);
        }

        [TestMethod]
        public void Execute_ResolveEntryPoints_SplitsPackage_WhenTheSourceRootItselfIsThePackage()
        {
            // The real-world shape that first crashed this whole area (root has __init__.py
            // directly, no wrapper folder — see RootIsThePackageItself_KeepsItInPlace above)
            // ALSO has to survive a split: the package being split away IS `root`, so the
            // "delete the now-empty original directory" cleanup step must never delete `root`
            // itself — every later phase (TOML generation) still needs it to exist.
            W(@"__init__.py");
            W(@"shared.py", "def helper():\n    return 1\n");
            W(@"main.py", "from . import shared\ndef main():\n    return shared.helper()\n");
            W(@"other.py", "from . import shared\ndef main():\n    return shared.helper()\n");

            ImportPlan plan = DefaultPlan();
            plan.ResolveEntryPoints = true;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(Directory.Exists(_root), "the working root itself must survive the split");
            Assert.IsTrue(File.Exists(result.PyProjectTomlPath));
            Assert.AreEqual(3, result.ProjectCount);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "main", "main.py")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "other", "main.py")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, Path.GetFileName(_root) + "_lib", "shared.py")));

            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(result.PyProjectTomlPath);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
        }

        [TestMethod]
        public void DefaultPlan_NestedPackage_DefaultsToPartOfParent()
        {
            // Mirrors a real repo shape that used to crash the executor: a "library"
            // package directory that itself contains further __init__.py-bearing
            // subdirectories, each independently detected as its own package.
            W(@"library\__init__.py");
            W(@"library\h_bond\__init__.py");
            W(@"library\deepbbq2_features\__init__.py");

            ImportPlan plan = DefaultPlan();
            Assert.AreEqual(PackageDisposition.SeparateProject,
                plan.Packages.Single(p => p.Source.Name == "library").Disposition,
                "the outermost package stays a separate project by default");
            Assert.AreEqual(PackageDisposition.PartOfParent,
                plan.Packages.Single(p => p.Source.Name == "h_bond").Disposition,
                "a package nested inside another detected package defaults to folded-in, not split out");
            Assert.AreEqual(PackageDisposition.PartOfParent,
                plan.Packages.Single(p => p.Source.Name == "deepbbq2_features").Disposition);
        }

        [TestMethod]
        public void Execute_RearrangeToSrc_NestedPackage_DefaultPlanSucceeds()
        {
            // Before the containment fix, this exact shape threw "Could not find a part
            // of the path" — library's move to src\library carried h_bond/
            // deepbbq2_features along with it, then the loop tried (and failed) to
            // separately move their now-nonexistent original paths.
            W(@"library\__init__.py");
            W(@"library\h_bond\__init__.py");
            W(@"library\deepbbq2_features\__init__.py");

            ImportPlan plan = DefaultPlan();
            plan.Layout = ImportLayout.RearrangeToSrc;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(1, result.ProjectCount, "only 'library' emits a project; the nested ones fold into it");

            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "src", "library")));
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "src", "library", "h_bond")),
                "the nested package rides along inside its container, never moved on its own");
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "src", "library", "deepbbq2_features")));
            Assert.IsFalse(File.Exists(Path.Combine(_root, "src", "library", "h_bond", "pyproject.toml")),
                "a folded package gets no member toml of its own");

            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(result.PyProjectTomlPath);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            Assert.AreEqual(1, load.Result.Solution.Projects.Count);
        }

        [TestMethod]
        public void Execute_RearrangeToSrc_UserOverridesNestedPackageToSeparateProject_StillSucceeds()
        {
            // The user can still explicitly split a folded-in package back out on the
            // Projects page. The executor's deepest-first move order must handle that
            // without crashing: the nested child moves to its own src\ entry BEFORE the
            // parent's move runs, so the parent never tries to drag an already-relocated
            // subdirectory, and neither move ever targets an already-vanished source.
            W(@"library\__init__.py");
            W(@"library\h_bond\__init__.py");

            ImportPlan plan = DefaultPlan();
            plan.Packages.Single(p => p.Source.Name == "h_bond").Disposition = PackageDisposition.SeparateProject;
            plan.Layout = ImportLayout.RearrangeToSrc;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(2, result.ProjectCount);

            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "src", "library")));
            Assert.IsTrue(Directory.Exists(Path.Combine(_root, "src", "h_bond")),
                "split out to its own top-level src entry");
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "src", "library", "h_bond")),
                "no longer nested inside library's copy");

            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(result.PyProjectTomlPath);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            Assert.AreEqual(2, load.Result.Solution.Projects.Count);
        }

        [TestMethod]
        public void Execute_VisualStudioFormat_WritesSlnAndPyproj()
        {
            W(@"pkg\__init__.py");

            ImportPlan plan = DefaultPlan("vsdemo");
            plan.TargetFormat = HelixPythonNamespace.Workspace.Python.ImportTargetFormat.VisualStudio;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(File.Exists(result.SlnPath));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "pkg", "pkg.pyproj")));
        }
    }
}
