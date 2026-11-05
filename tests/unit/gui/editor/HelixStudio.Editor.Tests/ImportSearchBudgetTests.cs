using System;
using System.Diagnostics;
using System.IO;
using HelixPythonNamespace.Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Opening a Python file must not depend on how big the folder tree around it happens to be.
    ///
    /// <para>
    /// It did, and it froze the IDE. Type-marker inference follows a file's imports, and any import
    /// it cannot resolve beside the file falls back to a recursive search from the nearest enclosing
    /// <c>.git</c> — a root chosen by walking up twelve levels. Opening a book's example package
    /// from <c>…\AuthoredBooks\…\code\cms</c> picked <c>AuthoredBooks</c>: 54,169 directories of
    /// unrelated documents, walked once per unresolved name, for every one of the 54 modules the
    /// package pulls in. The UI thread never came back, and the window went to "(Not Responding)".
    /// </para>
    ///
    /// <para>
    /// These build the same shape in miniature and assert the two properties that make it bounded:
    /// the standard library is never hunted for on disk, and the hunt gives up rather than running
    /// out of time. The timings are asserted with wide margins — the pre-fix behaviour was minutes,
    /// so a margin of seconds is not a close call.
    /// </para>
    /// </summary>
    [TestClass]
    public class ImportSearchBudgetTests
    {
        private string _root;

        [TestInitialize]
        public void CreateTree()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_budget_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            // A `.git` at the top makes this the import search root, exactly as AuthoredBooks was.
            Directory.CreateDirectory(Path.Combine(_root, ".git"));
        }

        [TestCleanup]
        public void RemoveTree()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>A wide, boring tree — the document library, in miniature.</summary>
        private void MakeDecoyDirectories(int count)
        {
            for (int i = 0; i < count; i++)
                Directory.CreateDirectory(Path.Combine(_root, "book" + i, "chapters", "figures"));
        }

        private string MakePackage(string source)
        {
            string package = Path.Combine(_root, "code", "cms");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "__init__.py"), string.Empty);
            string file = Path.Combine(package, "tensor.py");
            File.WriteAllText(file, source);
            return file;
        }

        [TestMethod]
        public void OpeningAFileInABigDocumentTreeStaysBounded()
        {
            MakeDecoyDirectories(400);                     // ~1,200 directories to walk
            string file = MakePackage(
                "import math\n" +
                "import torch\n" +
                "import typing\n" +
                "from dataclasses import dataclass\n" +
                "\n" +
                "def device() -> str:\n" +
                "    return 'cpu'\n");

            var clock = Stopwatch.StartNew();
            VirtualTypeMarkerRenderer.ComputeMarkers(File.ReadAllText(file), file);
            clock.Stop();

            Assert.IsTrue(clock.Elapsed.TotalSeconds < 8,
                "computing markers took " + clock.Elapsed.TotalSeconds.ToString("F1")
                + "s; unbounded, this is the path that stopped returning at all");
        }

        /// <summary>
        /// The standard library short-circuit, isolated. Each name is unique to this tree, so no
        /// cache from an earlier test can be answering instead of the code under test.
        /// </summary>
        [TestMethod]
        public void StandardLibraryImportsAreNotHuntedForOnDisk()
        {
            MakeDecoyDirectories(600);                     // ~1,800 directories

            string stdlibOnly = MakePackage(
                "import math\nimport json\nimport os\nimport re\nimport typing\n"
              + "def f() -> int:\n    return 1\n");

            var clock = Stopwatch.StartNew();
            VirtualTypeMarkerRenderer.ComputeMarkers(File.ReadAllText(stdlibOnly), stdlibOnly);
            clock.Stop();

            Assert.IsTrue(clock.Elapsed.TotalSeconds < 3,
                "five stdlib imports cost " + clock.Elapsed.TotalSeconds.ToString("F1")
                + "s; each one should have been refused before any directory was opened");
        }

        /// <summary>
        /// The stdlib short-circuit, asserted by BEHAVIOUR rather than by a clock.
        ///
        /// <para>
        /// A decoy <c>math.py</c> is planted deep in an unrelated corner of the tree — not beside
        /// the file, not in any parent — so the only way to reach it is the recursive "a sibling
        /// project might own it" search. Before the fix that search found it and imported its
        /// annotation. It must not now: <c>import math</c> means the standard library, and a
        /// stranger's file three folders away in somebody's book collection is not it.
        /// </para>
        /// </summary>
        [TestMethod]
        public void ADecoyStdlibNamedFileElsewhereInTheTreeIsNotFollowed()
        {
            string decoy = Path.Combine(_root, "someone_elses_book", "scratch");
            Directory.CreateDirectory(decoy);
            File.WriteAllText(Path.Combine(decoy, "math.py"),
                "def sqrt() -> DecoyTypeThatOnlyExistsHere:\n    return None\n");

            string file = MakePackage("import math\n\nvalue = math.sqrt()\n");

            var markers = VirtualTypeMarkerRenderer.ComputeMarkers(File.ReadAllText(file), file);

            foreach (string marker in markers.Values)
                Assert.IsFalse(marker != null && marker.Contains("DecoyTypeThatOnlyExistsHere"),
                    "`import math` followed a stray math.py in an unrelated folder");
        }

        /// <summary>
        /// A file whose imports DO resolve locally must still get its annotations — the bound must
        /// not have been bought by giving up on the work.
        /// </summary>
        [TestMethod]
        public void ALocalImportIsStillResolvedAndItsAnnotationsUsed()
        {
            string package = Path.Combine(_root, "code", "cms");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "__init__.py"), string.Empty);
            File.WriteAllText(Path.Combine(package, "helpers.py"),
                "def make_frame() -> Frame:\n    return Frame()\n");

            string file = Path.Combine(package, "user.py");
            File.WriteAllText(file, "from helpers import make_frame\n\nvalue = make_frame()\n");

            var markers = VirtualTypeMarkerRenderer.ComputeMarkers(File.ReadAllText(file), file);

            bool sawFrame = false;
            foreach (string marker in markers.Values)
                if (marker != null && marker.Contains("Frame")) sawFrame = true;
            Assert.IsTrue(sawFrame,
                "the annotation from the imported module should still reach the inlay; "
                + "bounding the SEARCH must not stop resolution that succeeds");
        }
    }
}
