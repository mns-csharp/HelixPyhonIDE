using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Python;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// What the Solution Explorer's Dependencies node is built from: the imports a package's own
    /// source really makes, split into the three groups whose consequences differ — CPython's own
    /// library, packages installed from an index, and the other members of this solution.
    ///
    /// Driven against the real python-test-sample-oop fixture, whose lib_utils imports asyncio /
    /// functools / time / contextlib / typing (stdlib) and whose app packages import lib_core and
    /// lib_utils (solution members).
    /// </summary>
    [TestClass]
    public class PackageDependencyScannerTests
    {
        private static string _fixture;

        [ClassInitialize]
        public static void FindFixture(TestContext ctx)
        {
            string dir = Path.GetDirectoryName(new Uri(
                typeof(PackageDependencyScannerTests).Assembly.CodeBase).LocalPath);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, @"tests\fixtures\python\python-test-sample-oop");
                if (Directory.Exists(candidate)) { _fixture = candidate; return; }
                dir = Path.GetDirectoryName(dir);
            }
        }

        private static SolutionModel _loaded;

        private static SolutionModel LoadFixture()
        {
            if (_fixture == null) Assert.Inconclusive("python-test-sample-oop fixture not found");
            if (_loaded != null) return _loaded;   // one real load for the whole class
            SolutionLoadResult result = new SolutionService()
                .LoadAsync(Path.Combine(_fixture, "pyproject.toml")).GetAwaiter().GetResult();
            Assert.IsNotNull(result, "the fixture solution must load");
            Assert.IsNotNull(result.Solution, result.ErrorMessage);
            _loaded = result.Solution;
            return _loaded;
        }

        private static ProjectModel Project(SolutionModel s, string name)
        {
            ProjectModel p = s.Projects.FirstOrDefault(x => x.Name == name);
            Assert.IsNotNull(p, "the fixture must have a " + name + " package");
            return p;
        }

        // ---------------------------------------------------------------- classification

        [TestMethod]
        public void StandardLibraryImports_AreGroupedAsStandardLibrary()
        {
            ProjectModel libUtils = Project(LoadFixture(), "lib_utils");
            List<string> stdlib = libUtils.ModuleDependencies
                .Where(d => d.Origin == DependencyOrigin.StandardLibrary)
                .Select(d => d.ModuleName).ToList();

            CollectionAssert.Contains(stdlib, "asyncio");
            CollectionAssert.Contains(stdlib, "functools");
            CollectionAssert.Contains(stdlib, "typing");
            CollectionAssert.Contains(stdlib, "contextlib");
        }

        [TestMethod]
        public void ImportsOfOtherWorkspaceMembers_AreGroupedAsSolutionPackages()
        {
            SolutionModel solution = LoadFixture();
            var solutionDeps = new List<string>();
            foreach (ProjectModel p in solution.Projects)
                solutionDeps.AddRange(p.ModuleDependencies
                    .Where(d => d.Origin == DependencyOrigin.SolutionPackage)
                    .Select(d => d.ModuleName));

            Assert.IsTrue(solutionDeps.Count > 0,
                "the fixture's app packages import its library packages");
            Assert.IsTrue(solutionDeps.Any(m => m.StartsWith("lib_core", StringComparison.Ordinal)
                                             || m.StartsWith("lib_utils", StringComparison.Ordinal)),
                "a sibling member must not be mistaken for a third-party package; got: "
                + string.Join(", ", solutionDeps.Distinct().ToArray()));
        }

        [TestMethod]
        public void APackageIsNotADependencyOfItself()
        {
            ProjectModel libUtils = Project(LoadFixture(), "lib_utils");
            Assert.IsFalse(libUtils.ModuleDependencies.Any(
                d => d.ModuleName == "lib_utils" || d.ModuleName.StartsWith("lib_utils.", StringComparison.Ordinal)),
                "importing your own submodule is not a dependency");
        }

        [TestMethod]
        public void ImportedNames_AreListedPerModule()
        {
            ProjectModel libUtils = Project(LoadFixture(), "lib_utils");
            ModuleDependency typing = libUtils.ModuleDependencies
                .FirstOrDefault(d => d.ModuleName == "typing");
            Assert.IsNotNull(typing, "lib_utils does `from typing import …`");

            // async_ops.py: from typing import Any, AsyncIterator, Awaitable, Callable, …
            CollectionAssert.Contains(typing.ImportedNames, "Awaitable");
            CollectionAssert.Contains(typing.ImportedNames, "Callable");
            CollectionAssert.AreEqual(typing.ImportedNames.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                typing.ImportedNames, "names are sorted, and deduplicated across the package's files");
        }

        [TestMethod]
        public void APlainImport_ListsTheModuleWithNoSymbols()
        {
            ProjectModel libUtils = Project(LoadFixture(), "lib_utils");
            ModuleDependency asyncio = libUtils.ModuleDependencies.First(d => d.ModuleName == "asyncio");
            Assert.AreEqual(0, asyncio.ImportedNames.Count,
                "`import asyncio` binds the module itself, not names out of it");
        }

        [TestMethod]
        public void RelativeImports_AreNotReportedAsDependencies()
        {
            // `from . import x` / `from .sibling import y` are intra-package.
            var scanner = new PackageDependencyScanner("pkg");
            scanner.AddImportsFrom(Parse("from . import helper\nfrom .models import Thing\nimport os\n"));

            List<ModuleDependency> deps = scanner.Build(null);
            Assert.AreEqual(1, deps.Count, "only os is external");
            Assert.AreEqual("os", deps[0].ModuleName);
        }

        [TestMethod]
        public void AnUnknownPackage_FallsBackToThirdParty()
        {
            var scanner = new PackageDependencyScanner("pkg");
            scanner.AddImportsFrom(Parse("import some_package_nobody_has_heard_of\n"));

            List<ModuleDependency> deps = scanner.Build(null);
            Assert.AreEqual(DependencyOrigin.ThirdParty, deps[0].Origin,
                "an unrecognised module is installed from somewhere — it is not CPython's");
        }

        [TestMethod]
        public void ALocalPackageShadowingAPyPiName_IsClassifiedAsTheLocalOne()
        {
            var scanner = new PackageDependencyScanner("pkg");
            scanner.AddImportsFrom(Parse("import requests\n"));

            List<ModuleDependency> deps = scanner.Build(new[] { "requests" });
            Assert.AreEqual(DependencyOrigin.SolutionPackage, deps[0].Origin,
                "what the interpreter actually imports is the workspace member");
        }

        [TestMethod]
        public void StarImports_RecordTheModuleAndSayItIsAStarImport()
        {
            var scanner = new PackageDependencyScanner("pkg");
            scanner.AddImportsFrom(Parse("from os.path import *\n"));

            List<ModuleDependency> deps = scanner.Build(null);
            Assert.AreEqual("os.path", deps[0].ModuleName);
            CollectionAssert.Contains(deps[0].ImportedNames, "*");
            Assert.AreEqual(DependencyOrigin.StandardLibrary, deps[0].Origin, "os.path is stdlib via os");
        }

        // ---------------------------------------------------------------- resources

        [TestMethod]
        public void Resources_ListNonSourceFilesAndSkipTheManifestAndCaches()
        {
            if (_fixture == null) Assert.Inconclusive("fixture not found");
            string pkg = Path.Combine(Path.GetTempPath(), "helix-res-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(pkg, "data"));
            Directory.CreateDirectory(Path.Combine(pkg, "__pycache__"));
            File.WriteAllText(Path.Combine(pkg, "pyproject.toml"), "[project]");
            File.WriteAllText(Path.Combine(pkg, "__init__.py"), "");
            File.WriteAllText(Path.Combine(pkg, "schema.json"), "{}");
            File.WriteAllText(Path.Combine(pkg, "data", "animals.csv"), "a,b");
            File.WriteAllText(Path.Combine(pkg, "__pycache__", "junk.pyc"), "x");

            List<string> resources = PackageResourceScanner.Scan(pkg);

            CollectionAssert.AreEquivalent(new[] { "data/animals.csv", "schema.json" }, resources);
        }

        [TestMethod]
        public void APackageWithOnlySource_HasNoResources()
        {
            ProjectModel libUtils = Project(LoadFixture(), "lib_utils");
            Assert.AreEqual(0, libUtils.ResourceFiles.Count,
                "the fixture's packages are pure Python — the tree must show no Resources node at all");
        }

        // ---------------------------------------------------------------- environment

        [TestMethod]
        public void EveryPackage_GetsAnEnvironmentRecord()
        {
            foreach (ProjectModel p in LoadFixture().Projects)
            {
                Assert.IsNotNull(p.Environment, p.Name + " must have an environment record");
                if (!string.IsNullOrEmpty(p.Environment.RequiresPython))
                    StringAssert.StartsWith(p.Environment.RequiresPython, ">=");
            }
        }

        private static global::HelixPythonNamespace.Python.Syntax.ModuleSyntax Parse(string source)
        {
            return global::HelixPythonNamespace.Python.Python.ParseModule(source, "x.py").Module;
        }
    }
}
