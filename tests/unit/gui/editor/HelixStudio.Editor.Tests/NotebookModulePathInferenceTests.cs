using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// A registered module root must reach the type-inference engine, not only the kernel.
    ///
    /// <para>
    /// The report: a notebook in the user's Documents folder imported a book's example package
    /// from another drive, registered that folder in the Modules pane, and ran a cell correctly —
    /// while the inlays directly above the running code read "couldn't infer type" on every call
    /// into the package. Both functions involved were annotated. The engine simply never read
    /// their files: it resolved imports by walking the disk around the <c>.ipynb</c>, and that
    /// folder was its own git repository, so the walk was confined to four files and could not
    /// reach the package at any depth or budget.
    /// </para>
    ///
    /// <para>
    /// <c>module_paths</c> had exactly one consumer — <c>NotebookKernelClient</c>'s <c>sys_path</c>
    /// request. The user had already answered "where does this import live" and the engine that
    /// reads the code was not told. These tests build that shape in miniature: a document inside
    /// its own <c>.git</c> island, and the library it imports on a separate tree that no walk from
    /// the document can reach.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookModulePathInferenceTests
    {
        private string _island;     // where the notebook lives: its own repository, nothing else in it
        private string _library;    // the registered root, unreachable from the island by walking

        [TestInitialize]
        public void CreateTrees()
        {
            string stem = Guid.NewGuid().ToString("N");
            _island = Path.Combine(Path.GetTempPath(), "helix_island_" + stem);
            _library = Path.Combine(Path.GetTempPath(), "helix_library_" + stem);
            Directory.CreateDirectory(_island);
            Directory.CreateDirectory(_library);
            // The `.git` is the point: it makes the island the import search root, so the walk-up
            // and the recursive fallback are both confined to it. This is the user's Documents
            // folder, which turned out to be a repository of its own.
            Directory.CreateDirectory(Path.Combine(_island, ".git"));
        }

        [TestCleanup]
        public void RemoveTrees()
        {
            foreach (string tree in new[] { _island, _library })
            {
                try { if (Directory.Exists(tree)) Directory.Delete(tree, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The kit the notebook imports — the real one's `fixture()` shape.</summary>
        private void WriteKit()
        {
            File.WriteAllText(Path.Combine(_library, "bmkit.py"),
                "from pathlib import Path\n\n\ndef fixture(prefix: str = 'f-') -> Path:\n    return Path(prefix)\n");
        }

        /// <summary>A notebook file that exists on disk, so the document is genuinely anchored.</summary>
        private string WriteNotebook()
        {
            string notebook = Path.Combine(_island, "MyApp01.ipynb");
            File.WriteAllText(notebook, "{\"cells\": [], \"nbformat\": 4}");
            return notebook;
        }

        private static IList<string> Roots(params string[] roots)
        {
            return new List<string>(roots);
        }

        private const string ImportsTheKit = "import bmkit as k\n\nbase = k.fixture()\n";

        [TestMethod]
        public void AModuleUnderARegisteredRootResolvesItsReturnAnnotation()
        {
            WriteKit();
            var markers = VirtualTypeMarkerRenderer.ComputeMarkers(
                ImportsTheKit, WriteNotebook(), Roots(_library));

            Assert.AreEqual("Path", markers[2],
                "`fixture()` is annotated `-> Path` and its file sits directly under the registered root.");
        }

        /// <summary>
        /// The pin that proves the one above can fail. Same tree, same code, root withheld — this
        /// is verbatim what the user saw, and it must keep being what happens without a root, or
        /// the test above is passing on the walk rather than on the fix.
        /// </summary>
        [TestMethod]
        public void WithoutTheRegisteredRootTheSameImportStaysUnresolved()
        {
            WriteKit();
            var markers = VirtualTypeMarkerRenderer.ComputeMarkers(
                ImportsTheKit, WriteNotebook(), null);

            Assert.AreEqual("couldn't infer type", markers[2],
                "nothing but the registered root can reach the kit from inside the island.");
        }

        /// <summary>
        /// A root is absolute and says nothing about where the document is, so it must work for a
        /// notebook that has never been saved — the case that needs it most. The engine used to
        /// require an existing file on disk before it would resolve any import at all.
        /// </summary>
        [TestMethod]
        public void AnUnsavedNotebookStillResolvesThroughARegisteredRoot()
        {
            WriteKit();
            var markers = VirtualTypeMarkerRenderer.ComputeMarkers(ImportsTheKit, null, Roots(_library));

            Assert.AreEqual("Path", markers[2]);
        }

        /// <summary>
        /// The second half of the report: `bm.Certify.certify(...)`, where `Certify` is re-exported
        /// by the package's `__init__.py` and the annotation lives one module deeper. Following
        /// that chain already worked; it just never started, because the package was never found.
        /// </summary>
        [TestMethod]
        public void ARegisteredRootFollowsAPackagesReExportChain()
        {
            string package = Path.Combine(_library, "bugmapish");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "certify.py"),
                "class Certify:\n    @staticmethod\n    def certify(run) -> Certificate:\n        return run\n");
            File.WriteAllText(Path.Combine(package, "__init__.py"),
                "from .certify import Certificate, Certify\n");

            string code = "import bugmapish as bm\n\ncertificate = bm.Certify.certify(None)\n";
            var markers = VirtualTypeMarkerRenderer.ComputeMarkers(code, WriteNotebook(), Roots(_library));

            Assert.AreEqual("Certificate", markers[2]);
        }

        /// <summary>
        /// A registered root can name a drive that is not mounted — the paths are absolute and the
        /// notebook travels. A bad root must be stepped over, never thrown from, and never allowed
        /// to cost the good root beside it.
        /// </summary>
        [TestMethod]
        public void AMissingOrMalformedRootIsSteppedOverRatherThanThrowing()
        {
            WriteKit();
            var markers = VirtualTypeMarkerRenderer.ComputeMarkers(
                ImportsTheKit, WriteNotebook(),
                Roots(@"Q:\not\mounted", "|not<a>path|", null, string.Empty, _library));

            Assert.AreEqual("Path", markers[2]);
        }

        /// <summary>
        /// A miss is cached as hard as a hit, so the roots have to be part of the cache key.
        /// Otherwise the first notebook's "not found" answers the second notebook's question and
        /// the feature appears to work only for whoever asked first.
        /// </summary>
        [TestMethod]
        public void ARootRegisteredAfterAMissIsNotAnsweredFromTheCachedMiss()
        {
            WriteKit();
            string notebook = WriteNotebook();

            var before = VirtualTypeMarkerRenderer.ComputeMarkers(ImportsTheKit, notebook, null);
            Assert.AreEqual("couldn't infer type", before[2], "precondition: the miss is recorded first");

            var after = VirtualTypeMarkerRenderer.ComputeMarkers(ImportsTheKit, notebook, Roots(_library));
            Assert.AreEqual("Path", after[2], "the cached miss must not survive the root being registered");
        }
    }
}
