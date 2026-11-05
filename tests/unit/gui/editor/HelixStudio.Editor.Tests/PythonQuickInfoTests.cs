using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Catalog;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Quick Info (F1 / Ctrl+K,Ctrl+I) and hover doc resolution, driven against the real
    /// <c>async_ops.py</c> fixture the bug was reported on.
    ///
    /// The old implementation asked for the COMPLETION LIST at the caret and looked for an entry
    /// whose label matched the word, so it answered nothing for the two ordinary cases below:
    /// a module name on its own `import` line, and a member of an imported module.
    /// </summary>
    [TestClass]
    public class PythonQuickInfoTests
    {
        private static string _source;
        private static string _path;

        [ClassInitialize]
        public static void LoadFixture(TestContext ctx)
        {
            string dir = Path.GetDirectoryName(new Uri(typeof(PythonQuickInfoTests).Assembly.CodeBase).LocalPath);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir,
                    @"tests\fixtures\python\python-test-sample-oop\lib_utils\async_ops.py");
                if (File.Exists(candidate)) { _path = candidate; _source = File.ReadAllText(candidate); return; }
                dir = Path.GetDirectoryName(dir);
            }
        }

        private static void RequireFixture()
        {
            if (_source == null) Assert.Inconclusive("async_ops.py fixture not found from the test binary location");
        }

        // Offset of the caret inside the Nth occurrence of `needle`, `caretIn` chars past its start.
        private static int OffsetIn(string needle, int caretIn, int occurrence = 1)
        {
            int idx = -1;
            for (int i = 0; i < occurrence; i++)
                idx = _source.IndexOf(needle, idx + 1, StringComparison.Ordinal);
            Assert.IsTrue(idx >= 0, "fixture must contain \"" + needle + "\" (occurrence " + occurrence + ")");
            return idx + caretIn;
        }

        private static PythonCompletionEngine EngineWithAsyncioCatalog()
        {
            // Stands in for what CatalogScanner streams in after `python introspect.py asyncio`:
            // the module's own entry plus the members that matter here.
            var catalog = new PythonCatalog();
            catalog.Add(new CatalogSymbol
            {
                ModuleFqn = "",
                Name = "asyncio",
                Kind = "module",
                Summary = "The asyncio package, tracking PEP 3156.",
                FullDoc = "The asyncio package, tracking PEP 3156.",
            });
            catalog.Add(new CatalogSymbol
            {
                ModuleFqn = "asyncio",
                Name = "Semaphore",
                Kind = "class",
                Signature = "(value=1)",
                Summary = "A Semaphore implementation.",
                FullDoc = "A Semaphore implementation.\n\nA semaphore manages an internal counter.",
            });
            return new PythonCompletionEngine { Catalog = catalog };
        }

        // What `python introspect.py asyncio` really emits for `call_later`: the same method on
        // four event-loop classes, and only the concrete ones carry the docstring. Verified
        // against a live interpreter — AbstractEventLoop.call_later has an EMPTY doc, which is
        // exactly the entry a "first bare-name match wins" lookup would pick.
        private static PythonCompletionEngine EngineWithEventLoopCatalog()
        {
            var engine = EngineWithAsyncioCatalog();
            engine.Catalog.Add(new CatalogSymbol
            {
                ModuleFqn = "asyncio",
                Name = "get_running_loop",
                Kind = "function",
                Signature = "()",           // no return annotation: a C-level builtin
                Summary = "Return the running event loop.",
                FullDoc = "Return the running event loop.  Raise a RuntimeError if there is none.",
            });
            engine.Catalog.Add(new CatalogSymbol
            {
                ModuleFqn = "asyncio.AbstractEventLoop",
                Name = "call_later",
                Kind = "method",
                Signature = "(self, delay, callback, *args, context=None)",
            });
            engine.Catalog.Add(new CatalogSymbol
            {
                ModuleFqn = "asyncio.BaseEventLoop",
                Name = "call_later",
                Kind = "method",
                Signature = "(self, delay, callback, *args, context=None)",
                Summary = "Arrange for a callback to be called at a given time.",
                FullDoc = "Arrange for a callback to be called at a given time.\n\n"
                        + "Return a Handle: an opaque object with a cancel() method.",
            });
            return engine;
        }

        // 4.1 — F1 on `asyncio` in `import asyncio`.
        [TestMethod]
        public void ImportedModuleName_ResolvesEvenWithNoCatalog()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();   // no catalog scanned yet
            int caret = OffsetIn("import asyncio", "import ".Length + 2);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick, "F1 on a module name must resolve to something");
            Assert.AreEqual("asyncio", quick.Item.Label);
            Assert.AreEqual("module asyncio", quick.Item.Detail);
            Assert.IsFalse(string.IsNullOrEmpty(quick.Item.Documentation),
                "the built-in module summary stands in until a scan lands");
            Assert.AreEqual("asyncio", quick.UnresolvedModuleFqn,
                "an unscanned module must be reported so the caller can start the introspection scan");
        }

        [TestMethod]
        public void ImportedModuleName_UsesTheScannedDocstringOnceCatalogued()
        {
            RequireFixture();
            var engine = EngineWithAsyncioCatalog();
            int caret = OffsetIn("import asyncio", "import ".Length + 2);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick);
            Assert.AreEqual("module asyncio", quick.Item.Detail);
            StringAssert.Contains(quick.Item.Documentation, "PEP 3156");
            Assert.IsNull(quick.UnresolvedModuleFqn, "no second scan once the module is catalogued");
        }

        // 4.2 — F1 on `Semaphore` in `semaphore = asyncio.Semaphore(max_concurrent)`.
        [TestMethod]
        public void MemberOfImportedModule_ResolvesFromTheCatalog()
        {
            RequireFixture();
            var engine = EngineWithAsyncioCatalog();
            int caret = OffsetIn("asyncio.Semaphore(", "asyncio.".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick, "F1 on a member of an imported module must resolve");
            Assert.AreEqual("Semaphore", quick.Item.Label);
            Assert.AreEqual("class asyncio.Semaphore(value=1)", quick.Item.Detail);
            StringAssert.Contains(quick.Item.Documentation, "internal counter");
        }

        [TestMethod]
        public void MemberOfImportedModule_WithoutCatalog_StillNamesItAndAsksForTheScan()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();
            int caret = OffsetIn("asyncio.Semaphore(", "asyncio.".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick, "showing the fully-qualified name beats showing nothing");
            Assert.AreEqual("asyncio.Semaphore", quick.Item.Detail);
            Assert.AreEqual("asyncio", quick.UnresolvedModuleFqn);
        }

        // 4.3 — hover over a function name resolves to its signature + docstring.
        [TestMethod]
        public void ModuleLevelFunction_ResolvesToSignatureAndDocstring()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();
            int caret = OffsetIn("async def retry_async(", "async def ".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick);
            StringAssert.StartsWith(quick.Item.Detail, "async def retry_async(");
            StringAssert.Contains(quick.Item.Documentation, "Retry an async operation");
        }

        [TestMethod]
        public void LocalFunctionDoctipPreservesAnnotationsDefaultsKindsAndReturnType()
        {
            const string src =
                "def transform(value: list[int], scale: float = 1.0, *args: str, **options: bool) -> tuple[int, str]:\n" +
                "    \"\"\"Transform values.\"\"\"\n" +
                "    return 1, 'x'\n";
            var quick = new PythonCompletionEngine().GetQuickInfo(src, src.IndexOf("transform", StringComparison.Ordinal) + 2,
                "typed.py", null);
            Assert.IsNotNull(quick);
            Assert.AreEqual(
                "def transform(value: list[int], scale: float=1.0, *args: str, **options: bool) -> tuple[int, str]",
                quick.Item.Detail);
            StringAssert.Contains(quick.Item.Documentation, "Transform values");
        }

        [TestMethod]
        public void LegacyHoverFallbackUsesTheSameQuickInfoEngine()
        {
            const string src = "def calculate(value: int) -> int:\n    \"\"\"Return the calculation.\"\"\"\n    return value\n";
            string hover = new PythonHoverProvider().GetHoverInfo(src, 1, 6, "calculate", "calc.py");
            Assert.IsFalse(string.IsNullOrEmpty(hover));
            StringAssert.Contains(hover, "calculate(value: int) -> int");
            StringAssert.Contains(hover, "Return the calculation");
        }

        // The case a completion-list lookup could never answer: methods are not module-level
        // symbols, so they never appeared in the general completion list.
        [TestMethod]
        public void MethodInsideAClass_ResolvesToSignatureAndDocstring()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();
            int caret = OffsetIn("async def acquire(self)", "async def ".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick, "hovering a class method must resolve");
            StringAssert.StartsWith(quick.Item.Detail, "async def acquire(");
            StringAssert.Contains(quick.Item.Documentation, "Block until a permit is available");
        }

        [TestMethod]
        public void ClassName_ResolvesToItsDocstring()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();
            int caret = OffsetIn("class AsyncRateLimiter:", "class ".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick);
            Assert.AreEqual("class AsyncRateLimiter", quick.Item.Detail);
            StringAssert.Contains(quick.Item.Documentation, "Simple async rate limiter");
        }

        // `from contextlib import asynccontextmanager` — a name bound by a from-import resolves
        // to <module>.<name>, not to nothing.
        [TestMethod]
        public void FromImportedName_ResolvesToItsFullyQualifiedName()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();
            int caret = OffsetIn("import asynccontextmanager", "import ".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick);
            Assert.AreEqual("contextlib.asynccontextmanager", quick.Item.Detail);
            Assert.AreEqual("contextlib", quick.UnresolvedModuleFqn);
        }

        // 4.4 — the reported bug: in
        //     handle = asyncio.get_running_loop().call_later(seconds, task.cancel)
        // hovering `get_running_loop` showed its doctip but hovering `call_later` showed nothing.
        // The receiver scan reads backwards over identifiers and dots and stops dead at the `)`,
        // so `call_later` looked like a bare name that resolved to nothing at all.

        [TestMethod]
        public void MemberOfAChainedCall_ResolvesFromTheCatalog()
        {
            RequireFixture();
            var engine = EngineWithEventLoopCatalog();
            int caret = OffsetIn("get_running_loop().call_later(", "get_running_loop().".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick, "hovering a method called on a call result must resolve");
            Assert.AreEqual("call_later", quick.Item.Label);
            Assert.AreEqual("asyncio.BaseEventLoop.call_later(delay, callback, *args, context=None)",
                quick.Item.Detail,
                "the best-documented overload wins, and the bound `self` is not shown");
            StringAssert.Contains(quick.Item.Documentation, "Arrange for a callback");
        }

        [TestMethod]
        public void MemberOfAChainedCall_WithoutCatalog_AsksForTheChainsRootModuleScan()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();
            int caret = OffsetIn("get_running_loop().call_later(", "get_running_loop().".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            Assert.IsNotNull(quick, "an unresolved chained member must still report what it needs");
            Assert.AreEqual("asyncio", quick.UnresolvedModuleFqn,
                "the scan to start is of the module the chain begins in");
        }

        [TestMethod]
        public void MemberOfAChainedCall_DoesNotAnswerFromAnUnrelatedModule()
        {
            RequireFixture();
            // A catalogued `call_later` that belongs to some OTHER module must not be offered as
            // the docs for asyncio's — a global by-name search would happily do exactly that.
            var engine = EngineWithAsyncioCatalog();
            engine.Catalog.Add(new CatalogSymbol
            {
                ModuleFqn = "tkinter.Misc",
                Name = "call_later",
                Kind = "method",
                Signature = "(self, ms)",
                FullDoc = "Not asyncio's method at all.",
            });
            int caret = OffsetIn("get_running_loop().call_later(", "get_running_loop().".Length + 3);

            var quick = engine.GetQuickInfo(_source, caret, _path, null);

            // asyncio IS scanned here, it just has no `call_later` — so there is nothing to say
            // and nothing left to scan. Declining is the answer; borrowing tkinter's docs is not.
            string doc = quick != null && quick.Item != null ? quick.Item.Documentation : null;
            Assert.IsFalse((doc ?? string.Empty).Contains("Not asyncio's method"),
                "a same-named method of an unrelated module must never be offered as the answer");
            Assert.IsTrue(string.IsNullOrEmpty(doc),
                "with asyncio already scanned and no call_later in it, Quick Info declines");
        }

        // The other half of the same fix: when the file itself says what the call returns, the
        // member resolves off the parse tree with no catalog involved at all.
        [TestMethod]
        public void MemberOfALocallyTypedCall_ResolvesFromTheFilesOwnClasses()
        {
            const string src =
                "class Handle:\n" +
                "    def cancel(self) -> None:\n" +
                "        \"\"\"Stop the scheduled callback.\"\"\"\n" +
                "\n" +
                "def schedule(seconds: float) -> Handle:\n" +
                "    return Handle()\n" +
                "\n" +
                "schedule(2.0).cancel()\n";
            var engine = new PythonCompletionEngine();
            int caret = src.IndexOf("schedule(2.0).cancel", StringComparison.Ordinal)
                        + "schedule(2.0).".Length + 3;

            var quick = engine.GetQuickInfo(src, caret, "x.py", null);

            Assert.IsNotNull(quick, "a call with a `-> T` annotation names its own receiver type");
            Assert.AreEqual("cancel", quick.Item.Label);
            StringAssert.Contains(quick.Item.Documentation, "Stop the scheduled callback");
        }

        [TestMethod]
        public void MemberOfADirectInstantiation_ResolvesFromTheFilesOwnClasses()
        {
            const string src =
                "class Handle:\n" +
                "    def cancel(self) -> None:\n" +
                "        \"\"\"Stop the scheduled callback.\"\"\"\n" +
                "\n" +
                "Handle().cancel()\n";
            var engine = new PythonCompletionEngine();
            int caret = src.IndexOf("Handle().cancel", StringComparison.Ordinal) + "Handle().".Length + 3;

            var quick = engine.GetQuickInfo(src, caret, "x.py", null);

            Assert.IsNotNull(quick);
            StringAssert.Contains(quick.Item.Documentation, "Stop the scheduled callback");
        }

        [TestMethod]
        public void CaretOnWhitespaceOrANumber_ResolvesToNothing()
        {
            RequireFixture();
            var engine = new PythonCompletionEngine();
            Assert.IsNull(engine.GetQuickInfo("x = 3.14\n", 6, null, null), "a numeric literal is not a symbol");
            Assert.IsNull(engine.GetQuickInfo("x = 1\n", 5, null, null), "end-of-line whitespace is not a symbol");
        }
    }
}
