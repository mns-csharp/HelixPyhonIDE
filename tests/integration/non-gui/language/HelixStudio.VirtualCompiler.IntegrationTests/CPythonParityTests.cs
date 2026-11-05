using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Phase-1.2 verification: a curated corpus of Python fragments that either
    /// compile cleanly under CPython's <c>compile(src, '&lt;test&gt;', 'exec')</c> or
    /// raise <c>SyntaxError</c>/<c>TypeError</c> at compile time. Our virtual
    /// compiler must match the binary outcome (any-error vs. clean) for every
    /// entry. Specific message parity is out of scope; only classification is
    /// asserted here.
    /// <para>
    /// Corpus is hand-curated from CPython's own
    /// <c>Lib/test/test_syntax.py</c> / <c>test_compile.py</c> corpora plus
    /// examples from the Python Language Reference. Each case is marked
    /// <c>ShouldError=true</c> or <c>false</c> based on how the current stable
    /// CPython (3.12) classifies it.
    /// </para>
    /// <para>
    /// <b>Intentional over-flagging:</b> our name resolver emits undefined-name
    /// diagnostics that CPython treats as runtime <c>NameError</c>, not compile-time
    /// errors. Such cases are either phrased to avoid undefined names (using
    /// builtins) or listed in <see cref="IntentionalDivergences"/>.
    /// </para>
    /// </summary>
    [TestClass]
    public class CPythonParityTests
    {
        /// <summary>A source fragment plus the expected compile-time classification.</summary>
        public sealed class Case
        {
            public readonly string Name;
            public readonly string Source;
            public readonly bool ShouldError;
            public Case(string name, string source, bool shouldError)
            {
                Name = name; Source = source; ShouldError = shouldError;
            }
        }

        public static readonly Case[] Corpus = new[]
        {
            // ---- Clean cases (CPython compiles without error) ----
            new Case("Empty", "", false),
            new Case("JustPass", "pass\n", false),
            new Case("SimpleAssign", "x = 1\n", false),
            new Case("SimpleFunction", "def f(): return 1\n", false),
            new Case("SimpleClass", "class C: pass\n", false),
            new Case("ImportStdlib", "import os\n", false),
            new Case("FromImport", "from os import path\n", false),
            new Case("WhileLoop", "while True:\n    pass\n", false),
            new Case("ForLoop", "for i in range(3):\n    pass\n", false),
            new Case("ForLoopWithBreak", "for i in range(3):\n    break\n", false),
            new Case("TryExcept", "try:\n    pass\nexcept Exception as e:\n    pass\n", false),
            new Case("WithStatement", "with open('x') as f:\n    pass\n", false),
            new Case("NestedFunction", "def f():\n    def g(): return 1\n    return g\n", false),
            new Case("AsyncFunction", "async def f(awaitable):\n    await awaitable\n", false),
            new Case("AnnotatedAssign", "x: int = 1\n", false),
            new Case("AugAssign", "x = 0\nx += 1\n", false),
            new Case("MultipleTargets", "a = b = 1\n", false),
            new Case("TupleUnpack", "a, b = 1, 2\n", false),
            new Case("StarredUnpack", "a, *b, c = [1, 2, 3, 4]\n", false),
            new Case("GlobalDeclFirst", "def f():\n    global x\n    x = 1\n", false),
            new Case("NonlocalInner",
                "def outer():\n    x = 1\n    def inner():\n        nonlocal x\n        x = 2\n", false),

            // ---- SyntaxError cases (CPython rejects at compile time) ----
            new Case("ReturnOutsideFunction", "return 42\n", true),
            new Case("YieldOutsideFunction", "yield 42\n", true),
            new Case("BreakOutsideLoop", "def f():\n    break\n", true),
            new Case("ContinueOutsideLoop", "def f():\n    continue\n", true),
            new Case("AwaitInSyncFunction", "def f():\n    await foo()\n", true),
            new Case("DuplicateParameter", "def f(x, x): pass\n", true),
            new Case("NonlocalWithoutBinding", "def f():\n    nonlocal nosuch\n", true),
            new Case("GlobalAfterAssign", "def f():\n    x = 1\n    global x\n", true),
            new Case("AssignToNone", "None = 1\n", true),
            new Case("AssignToDebug", "__debug__ = 1\n", true),
            new Case("MultipleStarredInAssign", "*a, *b = [1, 2, 3]\n", true),
            new Case("AnnotatedGlobal",
                "def f():\n    global x\n    x: int = 1\n", true),
            new Case("AnnotatedNonlocal",
                "def outer():\n    x = 1\n    def inner():\n        nonlocal x\n        x: int = 2\n", true),
        };

        /// <summary>
        /// Known places where our compiler is stricter than CPython's <c>compile()</c>.
        /// These are runtime errors in CPython (NameError, UnboundLocalError) that we
        /// opt to flag at compile time for higher signal — document, don't assert parity.
        /// </summary>
        public static readonly string[] IntentionalDivergences =
        {
            "undefined-name-in-function-body",                 // CPython: runtime NameError
            "class-body-name-not-visible-in-method",           // CPython: runtime NameError
            "comprehension-target-leak",                       // CPython: runtime NameError
        };

        [TestMethod]
        public void Corpus_Passes_AtLeast_95Percent()
        {
            int passed = 0;
            var failures = new System.Collections.Generic.List<string>();
            foreach (Case c in Corpus)
            {
                CompileResult r = VirtualCompiler.CompileSource(c.Source, "<" + c.Name + ">");
                bool ourError = r.HasErrors;
                if (ourError == c.ShouldError)
                {
                    passed++;
                }
                else
                {
                    failures.Add(string.Format(
                        "{0}: expected {1}, got {2} [{3}]",
                        c.Name,
                        c.ShouldError ? "ERROR" : "CLEAN",
                        ourError ? "ERROR" : "CLEAN",
                        string.Join(", ", r.Diagnostics.Select(d => d.Code))));
                }
            }

            double parity = passed / (double)Corpus.Length;
            Assert.IsTrue(parity >= 0.95,
                string.Format("CPython parity {0:P1} below 95% target ({1}/{2} passed). Failures:{3}{4}",
                    parity, passed, Corpus.Length, System.Environment.NewLine,
                    string.Join(System.Environment.NewLine, failures)));
        }

        [TestMethod]
        public void Every_CleanCase_Reports_NoErrors()
        {
            // Stricter per-case assertion: every entry marked ShouldError=false must
            // produce zero Error-severity diagnostics. Runtime-category flags (undefined
            // name etc.) would violate this — the corpus is deliberately phrased with
            // only builtins so resolution succeeds.
            var failures = new System.Collections.Generic.List<string>();
            foreach (Case c in Corpus)
            {
                if (c.ShouldError) continue;
                CompileResult r = VirtualCompiler.CompileSource(c.Source, "<" + c.Name + ">");
                if (r.HasErrors)
                {
                    failures.Add(c.Name + " → " +
                        string.Join(", ", r.Diagnostics
                            .Where(d => d.Severity >= HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error)
                            .Select(d => d.Code + " " + d.Message)));
                }
            }
            Assert.AreEqual(0, failures.Count,
                "Clean cases produced errors:" + System.Environment.NewLine +
                string.Join(System.Environment.NewLine, failures));
        }

        [TestMethod]
        public void Every_ErrorCase_Reports_AtLeastOneError()
        {
            var failures = new System.Collections.Generic.List<string>();
            foreach (Case c in Corpus)
            {
                if (!c.ShouldError) continue;
                CompileResult r = VirtualCompiler.CompileSource(c.Source, "<" + c.Name + ">");
                if (!r.HasErrors)
                {
                    failures.Add(c.Name + " produced no errors despite being invalid");
                }
            }
            Assert.AreEqual(0, failures.Count,
                "Error cases missed:" + System.Environment.NewLine +
                string.Join(System.Environment.NewLine, failures));
        }
    }
}
