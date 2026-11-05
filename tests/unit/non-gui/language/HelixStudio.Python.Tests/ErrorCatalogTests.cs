using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Diagnostics;

namespace HelixPythonNamespace.Python.Tests
{
    /// <summary>
    /// Acceptance pins for the bizarre-erroneous-statement catalog in docs/errors.md.
    /// Every entry must be REJECTED (so the editor squiggles it), and every legal
    /// neighbour of those rules must stay clean (so the editor does not).
    /// </summary>
    [TestClass]
    public class ErrorCatalogTests
    {
        private static string FirstError(string source)
        {
            ParseResult result = Python.ParseModule(source, "catalog.py");
            var errors = result.Diagnostics.Where(d => d.Severity == ParseSeverity.Error).ToList();
            return errors.Count == 0 ? null : errors[0].Message;
        }

        private static void AssertRejected(string label, string source)
        {
            Assert.IsNotNull(FirstError(source), label + " should be rejected but parsed clean.");
        }

        private static void AssertClean(string label, string source)
        {
            string message = FirstError(source);
            Assert.IsNull(message, label + " should parse clean but reported: " + message);
        }

        [TestMethod]
        public void ErrorCatalog_BizarreStatements_AllRejected()
        {
            var catalog = new Dictionary<string, string>
            {
                { "P01 return at module level",   "return 99\n" },
                { "P02 dangling operator",        "x2 = 1 +\n" },
                { "P03 colon as parameter",       "def broken3(:\n    pass\n" },
                { "P04 Python-2 print",           "print \"hello\"\n" },
                { "P05 duplicate argument",       "def dup5(x, x):\n    pass\n" },
                { "P06 assign to literal",        "1 = x6\n" },
                { "P07 unclosed parenthesis",     "x7 = (1, 2\n" },
                { "P08 yield at module level",    "yield 8\n" },
                { "P09 nonlocal at module level", "nonlocal n9\n" },
                { "P10 await outside async",      "await f10()\n" },
                { "P11 two bare star args",       "def stars11(*a, *b):\n    pass\n" },
                { "P12 assign to True",           "True = 12\n" },
                { "P13 unparenthesized walrus",   "x13 := 13\n" },
                { "P14 break outside loop",       "break\n" },
                { "P15 from __future__ braces",   "from __future__ import braces\n" },
            };
            foreach (var entry in catalog) AssertRejected(entry.Key, entry.Value);
        }

        [TestMethod]
        public void ErrorCatalog_LegalNeighbors_StayClean()
        {
            var legal = new Dictionary<string, string>
            {
                { "parenthesized walrus",     "(x := 13)\n" },
                { "chained assignment",       "a = b = 5\n" },
                { "break inside loop",        "for i in range(3):\n    break\n" },
                { "return inside function",   "def f():\n    return 1\n" },
                { "yield inside generator",   "def g():\n    yield 1\n" },
                { "await inside async def",   "async def h():\n    await g()\n" },
                { "generator lambda yield",   "gen = type((lambda: (yield))())\n" },
                { "await in generator expr",  "def make(n):\n    return (i for i in range(n) if await w(i))\n" },
                { "nonlocal in nested def",   "def outer():\n    v = 1\n    def inner():\n        nonlocal v\n        v = 2\n" },
                { "nonlocal in class body",   "def f(x):\n    class c:\n        nonlocal x\n        x += 1\n" },
                { "star and double-star",     "def s(a, *args, **kwargs):\n    pass\n" },
                { "keyword-only parameter",   "def k(a, *, b=1):\n    pass\n" },
                { "nested tuple target",      "a, (b, c) = 1, (2, 3)\n" },
                { "starred target",           "first, *rest = [1, 2, 3]\n" },
                { "parenthesized target",     "def f():\n    (no_such): int\n" },
                { "annotated assignment",     "x: int = 5\n" },
                { "legal __future__ import",  "from __future__ import annotations\n" },
                { "print as a call",          "print(\"hello\", 42)\n" },
                { "inline semicolon suite",   "if x:\n    pass\nelse:\n    pass\n" },
                { "inline suite with semis",  "if t: cmd = 'A'; isdir = 1\nelse: cmd = 'B'; isdir = 0\n" },
                { "match used as a name",     "class R:\n    name: str\n    match: str\n" },
                { "PEP 695 type alias",       "type Alias[T] = list[T]\n" },
                { "PEP 696 param default",    "def f[T = int]():\n    pass\n" },
                { "PEP 646 star subscript",   "x = A[float, *tuple[int, ...]]\n" },
                { "PEP 646 star annotation",  "def func(*args: *Ts):\n    pass\n" },
                { "class with **kwargs base", "class C(Base, a=1, **kwargs):\n    pass\n" },
                { "complex number pattern",   "match x:\n    case 0 + 0j:\n        y = 0\n" },
                { "starred group pattern",    "match p:\n    case (*x,):\n        y = 0\n" },
                { "PEP 758 except tuple",     "try:\n    f()\nexcept OSError, ValueError:\n    pass\n" },
            };
            foreach (var entry in legal) AssertClean(entry.Key, entry.Value);
        }

        [TestMethod]
        public void InlineSuite_KeepsEveryStatementInsideTheBranch()
        {
            // `if t: a; b` used to leak `b` out of the if-branch, which silently changed
            // the meaning of the code for every downstream engine.
            ParseResult result = Python.ParseModule("if t: cmd = 'A'; isdir = 1\n", "inline.py");
            Assert.IsFalse(result.HasErrors);
            var ifStatement = result.Module.Statements
                .OfType<HelixPythonNamespace.Python.Syntax.IfStatement>().Single();
            Assert.AreEqual(2, ifStatement.Body.Statements.Count);
            Assert.AreEqual(1, result.Module.Statements.Count);
        }
    }
}
