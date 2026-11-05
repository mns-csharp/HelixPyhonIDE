using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.VirtualCompiler.Imports;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Compile-time import resolution (PY5002/PY5003/PY5004).
    ///
    /// The anchor case is the one that shipped broken: a library project laid out as
    /// <c>projects/sample_common/sample_common/__init__.py</c>, with only the parent
    /// <c>projects/</c> on the search path. Real CPython binds the outer, <c>__init__</c>-free
    /// <c>projects/sample_common/</c> folder as a PEP 420 namespace package and raises
    /// <c>cannot import name '…' from 'sample_common' (unknown location)</c>. Every assertion about
    /// resolution order and messages here was checked against CPython 3.11 on that same layout.
    /// </summary>
    [TestClass]
    public class ImportResolutionTests
    {
        private string _root;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // ---- helpers ----

        private string Dir(params string[] parts)
        {
            string p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(p);
            return p;
        }

        private string File_(string contents, params string[] parts)
        {
            string p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            System.IO.File.WriteAllText(p, contents);
            return p;
        }

        // Reproduces the shipped sample_workspace layout: an outer project folder with a pyproject.toml
        // and no __init__.py, holding the real package one level down.
        private void MakeSampleWorkspaceOopLayout()
        {
            File_("", "projects", "sample_common", "pyproject.toml");
            File_("class TsvPreparationWorkflow:\n    pass\n",
                "projects", "sample_common", "sample_common", "datasets.py");
            File_("class WorkflowApplication:\n    pass\n",
                "projects", "sample_common", "sample_common", "applications.py");
            File_("from .applications import WorkflowApplication\n" +
                  "from .datasets import TsvPreparationWorkflow\n" +
                  "__all__ = [\"TsvPreparationWorkflow\", \"WorkflowApplication\"]\n",
                "projects", "sample_common", "sample_common", "__init__.py");
            File_("", "projects", "prepare_sample_tsv", "pyproject.toml");
        }

        private const string MainSource =
            "from sample_common import TsvPreparationWorkflow, WorkflowApplication\n" +
            "\n" +
            "def main() -> int:\n" +
            "    return 0\n";

        private static List<CompileDiagnostic> ImportDiagnostics(CompileResult r)
        {
            return r.Diagnostics.Where(d => d.Code.StartsWith("PY50", StringComparison.Ordinal)
                                            && d.Code != VirtualCompilerDiagnosticCodes.SymUnusedImport)
                    .ToList();
        }

        private CompileResult CompileWith(string source, string filePath, PythonSearchPath path)
        {
            CompileOptions options = new CompileOptions
            {
                // Isolate the import pass: the other checkers are exercised by their own suites and
                // would otherwise contribute unrelated diagnostics to these assertions.
                EnableTypeChecker = false,
                EnableCompileValidator = false,
                SearchPath = path,
            };
            return VirtualCompiler.CompileSource(source, filePath, options);
        }

        // ---- the regression this whole pass exists for ----

        [TestMethod]
        public void NamespacePackageShadowingRealPackage_IsReportedAtCompileTime()
        {
            MakeSampleWorkspaceOopLayout();
            string entry = File_(MainSource, "projects", "prepare_sample_tsv", "main.py");

            // The old launcher path model: parent-of-project-dir only.
            PythonSearchPath brokenPath = PythonSearchPath.FromRoots(new[]
            {
                Path.Combine(_root, "projects", "prepare_sample_tsv"),
                Path.Combine(_root, "projects"),
            });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(MainSource, entry, brokenPath));

            Assert.AreEqual(2, diags.Count,
                "expected one diagnostic per imported name; got: "
                + string.Join(" | ", diags.Select(d => d.Message)));
            Assert.IsTrue(diags.All(d => d.Code == VirtualCompilerDiagnosticCodes.ImportNameNotFound));
            // Byte-for-byte the message CPython prints — that equality is the point of the pass.
            Assert.AreEqual(
                "cannot import name 'TsvPreparationWorkflow' from 'sample_common' (unknown location)",
                diags[0].Message);
            Assert.AreEqual(
                "cannot import name 'WorkflowApplication' from 'sample_common' (unknown location)",
                diags[1].Message);
        }

        [TestMethod]
        public void CorrectedSearchPath_ResolvesTheRealPackage_NoDiagnostics()
        {
            MakeSampleWorkspaceOopLayout();
            string entry = File_(MainSource, "projects", "prepare_sample_tsv", "main.py");

            PythonSearchPath fixedPath = PythonSearchPath.ForRun(entry, new[]
            {
                Path.Combine(_root, "projects", "prepare_sample_tsv"),
                Path.Combine(_root, "projects", "sample_common"),
            });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(MainSource, entry, fixedPath));
            Assert.AreEqual(0, diags.Count,
                "correct path must resolve cleanly; got: "
                + string.Join(" | ", diags.Select(d => d.Message)));
        }

        [TestMethod]
        public void ImportRootsForProjects_AddsEachProjectDirectory()
        {
            MakeSampleWorkspaceOopLayout();
            string common = Path.Combine(_root, "projects", "sample_common");
            string app = Path.Combine(_root, "projects", "prepare_sample_tsv");

            List<string> roots = PythonSearchPath.ImportRootsForProjects(new[] { app, common }).ToList();

            CollectionAssert.Contains(roots, Path.GetFullPath(common),
                "the library project's own directory must be a root, or its package is unreachable");
            CollectionAssert.Contains(roots, Path.GetFullPath(app));
            // The old parent entry is KEPT — the fix is additive, so nothing that resolved before
            // stops resolving. Correctness comes from the project directory also being present.
            CollectionAssert.Contains(roots, Path.GetFullPath(Path.Combine(_root, "projects")));
            Assert.IsTrue(roots.IndexOf(Path.GetFullPath(common))
                          < roots.IndexOf(Path.GetFullPath(Path.Combine(_root, "projects"))),
                "project directories are emitted before parents so a printed PYTHONPATH reads intelligibly");
        }

        [TestMethod]
        public void ImportRootsForProjects_KeepsSrcLayoutPackageImportableByFolderName()
        {
            // The canonical src-layout the parent entry was originally added for.
            File_("", "ws", "src", "mypkg", "__init__.py");
            string proj = Path.Combine(_root, "ws", "src", "mypkg");

            var resolver = new ModuleResolver(
                PythonSearchPath.FromRoots(PythonSearchPath.ImportRootsForProjects(new[] { proj })));

            Assert.AreEqual(ModuleKind.RegularPackage, resolver.Resolve("mypkg").Kind,
                "a project that IS a package must stay importable under its own folder name");
        }

        [TestMethod]
        public void ImportRootsForProjects_KeepsCrossProjectNamespacePackagesResolvable()
        {
            // A project directory with no __init__.py that a sibling imports as a PEP 420
            // namespace package — only the parent entry makes this work, so it must survive.
            File_("VALUE = 1\n", "apps", "shared_ns", "helper.py");
            string proj = Path.Combine(_root, "apps", "shared_ns");

            var resolver = new ModuleResolver(
                PythonSearchPath.FromRoots(PythonSearchPath.ImportRootsForProjects(new[] { proj })));

            Assert.AreEqual(ModuleKind.Module, resolver.Resolve("shared_ns.helper").Kind);
        }

        // ---- resolver semantics ----

        [TestMethod]
        public void RegularPackage_BeatsNamespacePortionFoundEarlierOnThePath()
        {
            MakeSampleWorkspaceOopLayout();
            // projects/ (yields the namespace portion) deliberately FIRST.
            var resolver = new ModuleResolver(PythonSearchPath.FromRoots(new[]
            {
                Path.Combine(_root, "projects"),
                Path.Combine(_root, "projects", "sample_common"),
            }));

            ResolvedModule m = resolver.Resolve("sample_common");

            Assert.AreEqual(ModuleKind.RegularPackage, m.Kind,
                "CPython's PathFinder records namespace portions and keeps scanning — a real "
                + "package later on the path wins");
            Assert.AreEqual(
                Path.Combine(_root, "projects", "sample_common", "sample_common", "__init__.py"),
                m.Location);
        }

        [TestMethod]
        public void NamespacePackage_HasUnknownLocationAndNoNames()
        {
            MakeSampleWorkspaceOopLayout();
            var resolver = new ModuleResolver(PythonSearchPath.FromRoots(new[]
            {
                Path.Combine(_root, "projects"),
            }));

            ResolvedModule m = resolver.Resolve("sample_common");

            Assert.AreEqual(ModuleKind.NamespacePackage, m.Kind);
            Assert.IsNull(m.Location);
            Assert.AreEqual("unknown location", m.LocationDisplay);
            Assert.AreEqual(0, resolver.ExportsOf(m).Names.Count);
            Assert.IsFalse(resolver.ExportsOf(m).Opaque);
        }

        [TestMethod]
        public void MissingModule_ReportsTheFirstMissingLevel()
        {
            string entry = File_("import nowhere.deeper.still\n", "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            List<CompileDiagnostic> diags = ImportDiagnostics(
                CompileWith("import nowhere.deeper.still\n", entry, path));

            Assert.AreEqual(1, diags.Count);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.ImportModuleNotFound, diags[0].Code);
            Assert.AreEqual("No module named 'nowhere'", diags[0].Message,
                "CPython names the first level that could not be found, not the full dotted path");
        }

        [TestMethod]
        public void SubmoduleNotImportedByInit_IsStillImportable()
        {
            // CPython's _handle_fromlist imports pkg.sub when the attribute is absent.
            File_("", "app", "pkg", "__init__.py");
            File_("VALUE = 1\n", "app", "pkg", "sub.py");
            const string src = "from pkg import sub\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count);
        }

        [TestMethod]
        public void NameMissingFromRealPackage_ReportsTheBackingFileAsOrigin()
        {
            File_("ONLY = 1\n", "app", "pkg", "__init__.py");
            const string src = "from pkg import MISSING\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(src, entry, path));

            Assert.AreEqual(1, diags.Count);
            Assert.AreEqual(
                "cannot import name 'MISSING' from 'pkg' ("
                + Path.Combine(_root, "app", "pkg", "__init__.py") + ")",
                diags[0].Message);
        }

        [TestMethod]
        public void RelativeImport_ResolvesAgainstTheImportersOwnPosition()
        {
            File_("", "app", "pkg", "__init__.py");
            File_("HELPER = 1\n", "app", "pkg", "helper.py");
            const string src = "from .helper import HELPER\nfrom .helper import ABSENT\n";
            string entry = File_(src, "app", "pkg", "consumer.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(src, entry, path));

            Assert.AreEqual(1, diags.Count, "only the absent name should be reported");
            Assert.AreEqual("cannot import name 'ABSENT' from 'pkg.helper' ("
                + Path.Combine(_root, "app", "pkg", "helper.py") + ")", diags[0].Message);
        }

        [TestMethod]
        public void RelativeImport_BeyondTopLevel_IsReported()
        {
            File_("", "app", "pkg", "__init__.py");
            const string src = "from ... import something\n";
            string entry = File_(src, "app", "pkg", "mod.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(src, entry, path));

            Assert.AreEqual(1, diags.Count);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.ImportRelativeBeyondTopLevel, diags[0].Code);
            Assert.AreEqual("attempted relative import beyond top-level package", diags[0].Message);
        }

        // ---- the conservatism that keeps this pass usable on real code ----

        [TestMethod]
        public void StdlibAndThirdPartyImports_AreNeverReported()
        {
            const string src =
                "import os\n" +
                "import numpy as np\n" +
                "from pathlib import Path\n" +
                "from argparse import ArgumentParser, Namespace\n" +
                "from torch.nn import Module\n" +
                "from numpy import this_name_does_not_exist\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(src, entry, path));
            Assert.AreEqual(0, diags.Count,
                "site-packages is invisible to the compiler — guessing about it is the largest "
                + "false-positive class there is; got: " + string.Join(" | ", diags.Select(d => d.Message)));
        }

        [TestMethod]
        public void StructuralBiologyAndSequenceImports_AreNeverReported()
        {
            // The domain this product is pitched at. An unlisted top-level module is a PY5002
            // ERROR, and PY5002 gates Compile/Run/Debug — so an unregistered `import biotite`
            // does not merely fail to help, it refuses to run correct code. Casing matters:
            // KnownExternalModules is Ordinal, so py3Dmol and pyBigWig must match exactly.
            string[] modules =
            {
                "biotite", "gemmi", "pdbfixer", "parmed", "pytraj", "freesasa", "biopandas",
                "openbabel", "pymol", "chimerax", "simtk", "nglview", "py3Dmol",
                "pysam", "pyfaidx", "pyBigWig", "cyvcf2", "HTSeq", "gffutils", "skbio", "ete3",
                "scanpy", "anndata",
            };

            string src = string.Join("", modules.Select(m => "import " + m + "\n"))
                + "import biotite.structure.io.pdbx as pdbx\n"
                + "from simtk.openmm import app\n"
                + "from openbabel import openbabel as ob\n"
                + "from Bio.PDB import PDBParser\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(src, entry, path));
            Assert.AreEqual(0, diags.Count,
                "a structural-biology or bioinformatics first file must compile; got: "
                + string.Join(" | ", diags.Select(d => d.Message)));
        }

        [TestMethod]
        public void TryGuardedOptionalImport_IsNotReported()
        {
            const string src =
                "try:\n" +
                "    import ujson as json_impl\n" +
                "except ImportError:\n" +
                "    import json as json_impl\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count);
        }

        [TestMethod]
        public void DeferredImportInsideFunction_IsNotReported()
        {
            // A function-body import may run after a sys.path tweak, so it is out of scope.
            const string src =
                "def load():\n" +
                "    import definitely_not_a_module\n" +
                "    return definitely_not_a_module\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count);
        }

        [TestMethod]
        public void StarImportedModule_IsTreatedAsOpaque()
        {
            File_("from os import *\n", "app", "pkg", "__init__.py");
            const string src = "from pkg import getcwd\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count,
                "a star import means the compiler cannot enumerate the module's names");
        }

        [TestMethod]
        public void ModuleLevelGetattr_IsTreatedAsOpaque()
        {
            File_("def __getattr__(name):\n    return 1\n", "app", "pkg", "__init__.py");
            const string src = "from pkg import anything_at_all\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count,
                "PEP 562 lets a module synthesize any attribute on demand");
        }

        [TestMethod]
        public void SiblingModuleImport_IsResolvedThroughTheImportersOwnDirectory()
        {
            // sys.path[0] is the running script's directory, and pytest prepends a test file's
            // directory for the same reason — neither is ever in PYTHONPATH.
            File_("class HarmonicEngine:\n    pass\n", "suite", "test_certify.py");
            const string src = "from test_certify import HarmonicEngine\n";
            string entry = File_(src, "suite", "test_testing_api.py");
            // Deliberately a path that does NOT contain the suite directory.
            var path = PythonSearchPath.FromRoots(new[] { _root });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count);
        }

        [TestMethod]
        public void SiblingImportOfAName_ThatDoesNotExist_IsStillReported()
        {
            File_("class Present:\n    pass\n", "suite", "helper.py");
            const string src = "from helper import Absent\n";
            string entry = File_(src, "suite", "user.py");
            var path = PythonSearchPath.FromRoots(new[] { _root });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(src, entry, path));
            Assert.AreEqual(1, diags.Count, "resolving through the importer's directory must not "
                + "disable name checking against the module it found");
            Assert.AreEqual("cannot import name 'Absent' from 'helper' ("
                + Path.Combine(_root, "suite", "helper.py") + ")", diags[0].Message);
        }

        [TestMethod]
        public void FileThatMutatesSysPath_IsNotJudged()
        {
            const string src =
                "import sys\n" +
                "sys.path.insert(0, \"/somewhere/else\")\n" +
                "import a_module_only_on_that_path\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count);
        }

        [TestMethod]
        public void FileDownstreamOfASysPathBootstrap_IsNotJudged()
        {
            // The pervasive research-code shape: a package __init__ imports a _paths module whose
            // only job is to insert directories, so everything downstream imports names from
            // locations the static path model cannot know.
            File_("import sys\nsys.path.insert(0, \"/elsewhere\")\n", "app", "pkg", "_paths.py");
            File_("from . import _paths\n", "app", "pkg", "__init__.py");
            const string src = "import pkg\nimport something_only_pkg_made_visible\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count,
                "the bootstrap must be followed through the package __init__");
        }

        [TestMethod]
        public void SysPathInsideAStringOrComment_DoesNotSuppressChecking()
        {
            const string src =
                "\"\"\"This module does not touch sys.path at all.\"\"\"\n" +
                "# nor does this comment about sys.path\n" +
                "import definitely_not_a_module\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            List<CompileDiagnostic> diags = ImportDiagnostics(CompileWith(src, entry, path));
            Assert.AreEqual(1, diags.Count, "suppression is detected on lexed tokens, not raw text");
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.ImportModuleNotFound, diags[0].Code);
        }

        [TestMethod]
        public void SrcLayoutProject_CanImportItsOwnPackage()
        {
            File_("", "ws", "proj", "pyproject.toml");
            File_("VALUE = 1\n", "ws", "proj", "src", "mypkg", "__init__.py");

            var resolver = new ModuleResolver(PythonSearchPath.FromRoots(
                PythonSearchPath.ImportRootsForProjects(new[] { Path.Combine(_root, "ws", "proj") })));

            Assert.AreEqual(ModuleKind.RegularPackage, resolver.Resolve("mypkg").Kind,
                "PEP 621 src-layout: <project>/src is what an editable install puts on the path");
        }

        [TestMethod]
        public void PackageChainFixesTheModuleName_EvenWhenThePackageDirIsItselfARoot()
        {
            // Regression: deriving the FQN from the shortest root-relative path made
            // src/app_server/middleware.py come out as 'middleware', which turned every
            // `from .routes import ...` in it into a bogus "beyond top-level package" error.
            File_("", "ws", "src", "app_server", "__init__.py");
            File_("class Request:\n    pass\n", "ws", "src", "app_server", "routes.py");
            const string src = "from .routes import Request\n";
            string entry = File_(src, "ws", "src", "app_server", "middleware.py");

            var path = PythonSearchPath.FromRoots(new[]
            {
                Path.Combine(_root, "ws", "src", "app_server"),   // the project directory
                Path.Combine(_root, "ws", "src"),                 // its parent
            });

            Assert.AreEqual("app_server.middleware", path.ModuleFqnOf(entry));
            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count);
        }

        [TestMethod]
        public void ConditionallyDefinedName_CountsAsExported()
        {
            File_("import sys\n" +
                  "if sys.version_info >= (3, 8):\n" +
                  "    def helper():\n        return 1\n" +
                  "else:\n" +
                  "    helper = None\n", "app", "pkg", "__init__.py");
            const string src = "from pkg import helper\n";
            string entry = File_(src, "app", "main.py");
            var path = PythonSearchPath.FromRoots(new[] { Path.Combine(_root, "app") });

            Assert.AreEqual(0, ImportDiagnostics(CompileWith(src, entry, path)).Count);
        }

        [TestMethod]
        public void WithoutASearchPath_ThePassIsInert()
        {
            // Callers that never modelled sys.path (editor-side single-file compiles, older CLI
            // paths) must not start seeing import errors they have no way to be right about.
            const string src = "from totally_unknown_thing import Whatever\n";
            CompileResult r = VirtualCompiler.CompileSource(src, Path.Combine(_root, "x.py"),
                new CompileOptions { EnableTypeChecker = false, EnableCompileValidator = false });

            Assert.AreEqual(0, ImportDiagnostics(r).Count);
        }

        // ---- path model ----

        [TestMethod]
        public void ModuleFqnOf_UsesTheLongestMatchingRoot()
        {
            MakeSampleWorkspaceOopLayout();
            var path = PythonSearchPath.FromRoots(new[]
            {
                Path.Combine(_root, "projects"),
                Path.Combine(_root, "projects", "sample_common"),
            });

            Assert.AreEqual("sample_common", path.ModuleFqnOf(
                Path.Combine(_root, "projects", "sample_common", "sample_common", "__init__.py")));
            Assert.AreEqual("sample_common.datasets", path.ModuleFqnOf(
                Path.Combine(_root, "projects", "sample_common", "sample_common", "datasets.py")));
        }

        [TestMethod]
        public void ForRun_PutsTheScriptDirectoryFirst()
        {
            MakeSampleWorkspaceOopLayout();
            string entry = File_(MainSource, "projects", "prepare_sample_tsv", "main.py");

            PythonSearchPath path = PythonSearchPath.ForRun(entry, new[]
            {
                Path.Combine(_root, "projects", "sample_common"),
            });

            Assert.AreEqual(Path.GetFullPath(Path.Combine(_root, "projects", "prepare_sample_tsv")),
                path.Roots[0], "CPython sets sys.path[0] to the script's own directory");
        }
    }
}
