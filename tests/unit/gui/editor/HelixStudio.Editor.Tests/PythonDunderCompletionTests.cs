using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Catalog;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// `np.__version__` in the editor.
    ///
    /// Reported: typing it offered `arange`, `array`, `asanyarray`… and never `__version__`. The
    /// catalog was the cause (see `Catalog.Tests/IntrospectionContractTests`), but the catalog is
    /// only half the path — the classifier has to read `__version__` as a member PREFIX rather
    /// than give up at the underscores, and the ranker has to keep an item whose name starts with
    /// two of them. These drive the real engine end to end so a regression in either half shows up
    /// here rather than as "IntelliSense is broken" months later.
    ///
    /// The catalog is built by RUNNING the real scanner against this machine's numpy, for the
    /// reason the fixture rule exists: a hand-built catalog would have agreed with the engine
    /// about a package neither of them had ever seen.
    /// </summary>
    [TestClass]
    public class PythonDunderCompletionTests
    {
        private static PythonCatalog _numpy;
        private static bool _scanned;

        /// <summary>The real scanner's output for numpy, or null when numpy is not installed.</summary>
        private static PythonCatalog NumpyCatalog()
        {
            if (_scanned) return _numpy;
            _scanned = true;

            string interpreter = FindInterpreter();
            string script = FindScript();
            if (interpreter == null || script == null) return null;

            var catalog = new PythonCatalog();
            var scanner = new CatalogScanner();
            bool finished = false;
            bool ok = false;
            scanner.Finished += (s, e) => { ok = e.Ok; finished = true; };
            scanner.StartScan(interpreter, script, catalog, new[] { "numpy" });

            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            while (!finished && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(50);
            if (!finished || !ok) return null;
            if (!catalog.HasModule("numpy")) return null;

            _numpy = catalog;
            return _numpy;
        }

        private static string FindInterpreter()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string candidate;
                try { candidate = Path.Combine(entry.Trim(), "python.exe"); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string FindScript()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, @"tools\catalog\introspect.py");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        private static List<CompletionItem> Complete(string source)
        {
            PythonCatalog catalog = NumpyCatalog();
            if (catalog == null) return null;
            var engine = new PythonCompletionEngine { Catalog = catalog };
            return engine.GetCompletions(source, source.Length, null, null).ToList();
        }

        private const string Header = "import numpy as np\n\n";

        [TestMethod]
        public void GeneralCompletionIncludesPythonExpansionSnippets()
        {
            var engine = new PythonCompletionEngine();
            List<CompletionItem> items = engine.GetCompletions("tr", 2, null, null);
            CompletionItem snippet = items.First(item => item.Kind == CompletionKind.Snippet && item.Label == "try");
            StringAssert.Contains(snippet.InsertText, "except Exception as error:");
            Assert.IsTrue(items.Any(item => item.Kind == CompletionKind.Snippet && item.Label == "main"));
            CompletionItem ctor = items.First(item => item.Kind == CompletionKind.Snippet && item.Label == "ctor");
            Assert.AreEqual("def __init__(self):\n    pass", ctor.InsertText);
            CompletionItem staticMethod = items.First(item => item.Kind == CompletionKind.Snippet && item.Label == "stat");
            Assert.AreEqual("@staticmethod\ndef function_name():\n    pass", staticMethod.InsertText);
            Assert.AreEqual(1, items.Count(item => item.Label == "def"),
                "the popup cannot distinguish duplicate labels; the snippet must replace the keyword row");
            Assert.AreEqual(CompletionKind.Snippet, items.Single(item => item.Label == "def").Kind);
        }

        [TestMethod]
        public void DefShortcutExpansion_PreservesClassIndentation()
        {
            const string source = "class NewClass:\n    def";
            string expansion = CompletionController.FormatSnippetExpansion(
                source, source.Length - "def".Length,
                "def function_name(self):\n    pass");
            Assert.AreEqual("def function_name(self):\n        pass", expansion);
        }

        [TestMethod]
        public void ConstructorSnippetExpansion_PreservesClassIndentation()
        {
            const string source = "class NewClass:\r\n    ctor";
            string expansion = CompletionController.FormatSnippetExpansion(
                source, source.Length - "ctor".Length, "def __init__(self):\n    pass");
            Assert.AreEqual("def __init__(self):\r\n        pass", expansion);
        }

        [TestMethod]
        public void StaticMethodSnippetExpansion_PreservesClassIndentation()
        {
            const string source = "class NewClass:\n    stat";
            string expansion = CompletionController.FormatSnippetExpansion(
                source, source.Length - "stat".Length,
                "@staticmethod\ndef function_name():\n    pass");
            Assert.AreEqual("@staticmethod\n    def function_name():\n        pass", expansion);
        }

        [TestMethod]
        public void CtrlSpaceAfterDefInAClassOffersDunderOverrides()
        {
            var engine = new PythonCompletionEngine();
            const string source = "class NewClass:\n    def ";
            List<CompletionItem> items = engine.GetCompletions(source, source.Length, null, null);

            foreach (string name in new[] { "__init__", "__repr__", "__str__", "__eq__", "__iter__" })
                Assert.IsTrue(items.Any(item => item.Label == name), name + " was not offered");
            Assert.AreEqual("__init__(self):", items.First(item => item.Label == "__init__").InsertText);
        }

        [TestMethod]
        public void DunderOverridesAreNotOfferedInsideAMethodBody()
        {
            var engine = new PythonCompletionEngine();
            const string source = "class NewClass:\n    def existing(self):\n        def ";
            List<CompletionItem> items = engine.GetCompletions(source, source.Length, null, null);

            Assert.IsFalse(items.Any(item => item.Label == "__init__"));
        }

        // ---- the classifier half --------------------------------------------------------------

        /// <summary>
        /// The underscores are part of the member prefix, not a break in it. If the classifier
        /// stopped at them the engine would be asked for members of `np.` with an empty prefix,
        /// which is exactly the unfiltered `arange, array, asanyarray…` list that was reported.
        /// </summary>
        [TestMethod]
        public void ADunderPrefixIsReadAsAMemberPrefix()
        {
            var engine = new PythonCompletionEngine();
            string source = Header + "print(np.__vers";
            CompletionContext context = engine.Classify(source, source.Length);

            Assert.AreEqual(CompletionContextKind.Member, context.Kind);
            Assert.AreEqual("np", context.MemberBase);
            Assert.AreEqual("__vers", context.Prefix);
        }

        // ---- the catalog + engine half ---------------------------------------------------------

        [TestMethod]
        public void NumpysVersionDunderIsOffered()
        {
            List<CompletionItem> items = Complete(Header + "print(np.__vers");
            if (items == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            Assert.IsTrue(items.Any(i => i.Label == "__version__"),
                "np.__version__ must be offered; got " + Preview(items));
        }

        [TestMethod]
        public void ConstantsAndSubmodulesAreOfferedToo()
        {
            List<CompletionItem> items = Complete(Header + "print(np.");
            if (items == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            foreach (string name in new[] { "pi", "e", "inf", "nan", "newaxis" })
                Assert.IsTrue(items.Any(i => i.Label == name),
                    "np." + name + " is a constant and must complete; got " + Preview(items));
            foreach (string name in new[] { "linalg", "random", "fft" })
                Assert.IsTrue(items.Any(i => i.Label == name),
                    "np." + name + " is a submodule and must complete; got " + Preview(items));

            // The functions that already worked must still be there — the fix adds, never replaces.
            Assert.IsTrue(items.Any(i => i.Label == "arange"));
            Assert.IsTrue(items.Any(i => i.Label == "array"));
        }

        /// <summary>
        /// Someone else's internals stay out. `_x` is protected and `__x` is private under the same
        /// rule Core's `SemanticModel.Visibility` applies to the user's own code, and completing
        /// them invites a call that breaks on the package's next release.
        /// </summary>
        [TestMethod]
        public void PrivateMembersAreStillNotOffered()
        {
            List<CompletionItem> items = Complete(Header + "print(np.");
            if (items == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            foreach (CompletionItem item in items)
            {
                string label = item.Label ?? string.Empty;
                bool dunder = label.Length >= 4 && label.StartsWith("__") && label.EndsWith("__");
                if (dunder) continue;
                Assert.IsFalse(label.StartsWith("_"), "a private member was offered: " + label);
            }
        }

        // ---- the ranker half --------------------------------------------------------------------

        /// <summary>
        /// Filtering to `__vers` must keep `__version__` and put it first. Two leading underscores
        /// are just characters to a prefix matcher — this pins that nothing downstream treats them
        /// as a reason to drop the item.
        /// </summary>
        [TestMethod]
        public void FilteringToTheDunderPrefixKeepsItAndRanksItFirst()
        {
            List<CompletionItem> items = Complete(Header + "print(np.__vers");
            if (items == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            List<CompletionItem> ranked = CompletionRanker.FilterAndRank(items, "__vers").ToList();

            Assert.IsTrue(ranked.Count > 0, "filtering to '__vers' dropped everything");
            Assert.AreEqual("__version__", ranked[0].Label,
                "an exact prefix match belongs at the top; got " + Preview(ranked));
        }

        /// <summary>
        /// The value is the documentation. `__version__` with no "2.2.6" beside it tells the user
        /// nothing they did not already know from typing it.
        /// </summary>
        [TestMethod]
        public void TheVersionCompletionCarriesTheActualVersion()
        {
            List<CompletionItem> items = Complete(Header + "print(np.__vers");
            if (items == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            CompletionItem version = items.First(i => i.Label == "__version__");
            string shown = (version.Documentation ?? string.Empty) + " " + (version.Detail ?? string.Empty);
            StringAssert.Contains(shown, "'", "the repr of the version should be shown: " + shown);
        }

        private static string Preview(IEnumerable<CompletionItem> items)
        {
            var labels = items.Select(i => i.Label).Take(30).ToArray();
            return string.Join(", ", labels);
        }
    }
}
