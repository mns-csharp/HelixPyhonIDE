using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CMakeSolutionLoaderTests
    {
        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && Directory.GetFiles(d.FullName, "*.sln").Length == 0)
                d = d.Parent;
            Assert.IsNotNull(d, "could not locate repo root (the product .sln)");
            return d.FullName;
        }

        private static string FixtureFolder()
        {
            string folder = Path.Combine(RepoRoot(), "tests", "fixtures", "cuda",
                "CudaCppMultiPackageWorkspace");
            Assert.IsTrue(Directory.Exists(folder), "fixture folder not found: " + folder);
            return folder;
        }

        [TestMethod]
        public void ImportsRealFixtureIntoMultiProjectSolution()
        {
            var importer = new CMakeProjectImporter();
            CMakeImportResult result = importer.Import(FixtureFolder());
            Assert.IsTrue(result.Success, result.Error);

            SolutionModel solution = CMakeSolutionLoader.BuildSolution(result.Project);
            Assert.IsNotNull(solution);

            // One ProjectModel per CMake target, not one big lump for the whole folder. The seven
            // are this fixture's own add_library/add_executable targets; the vendored Catch2 tree
            // under my_unit_test_package declares many more and is not what this counts.
            Assert.IsTrue(solution.Projects.Count >= 7,
                "expected at least the lib_core/lib_utils/app_cli/app_server/debug_target/" +
                "exception_target/my_unit_test_package_tests targets, got " + solution.Projects.Count);

            ProjectModel libCore = solution.Projects.FirstOrDefault(p => p.Name == "lib_core");
            Assert.IsNotNull(libCore, "lib_core target should become its own ProjectModel");
            Assert.AreEqual(ProjectLanguage.Cpp, libCore.Language);
            Assert.AreEqual(ProjectKind.CudaLibrary, libCore.Kind);

            // Real types from lib_core's real source files, not a stub.
            Assert.IsTrue(libCore.Types.Any(t => t.Name == "Circle"), "Circle should be extracted into lib_core");
            Assert.IsTrue(libCore.Types.Any(t => t.Name == "Animal"), "Animal should be extracted into lib_core");
            Assert.IsTrue(libCore.Functions.Count >= 0);  // free functions, if any — just must not throw

            ProjectModel appCli = solution.Projects.FirstOrDefault(p => p.Name == "app_cli");
            Assert.IsNotNull(appCli, "app_cli executable target should become its own ProjectModel");
            Assert.AreEqual(ProjectKind.CudaExecutable, appCli.Kind);

            // Two targets sharing lib_core's directory (diagnostics/ bait files are excluded from
            // every CMake target, so lib_core's Types must come only from its real .cpp sources —
            // e.g. never a type from the deliberately-broken diagnostics_demo.cpp).
            Assert.IsFalse(libCore.Types.Any(t => t.SourceFilePath != null &&
                t.SourceFilePath.IndexOf("diagnostics", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void ProjectDirectoryResolvesToRealFolderDespiteSyntheticFilePath()
        {
            var importer = new CMakeProjectImporter();
            CMakeImportResult result = importer.Import(FixtureFolder());
            Assert.IsTrue(result.Success, result.Error);
            SolutionModel solution = CMakeSolutionLoader.BuildSolution(result.Project);

            ProjectModel libCore = solution.Projects.First(p => p.Name == "lib_core");
            Assert.IsTrue(Directory.Exists(libCore.ProjectDirectory),
                "ProjectDirectory must resolve to the real on-disk folder: " + libCore.ProjectDirectory);
        }
    }
}
