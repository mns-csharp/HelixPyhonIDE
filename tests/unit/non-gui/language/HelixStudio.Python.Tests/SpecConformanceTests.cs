using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Diagnostics;

namespace HelixPythonNamespace.Python.Tests
{
    /// <summary>
    /// Spec-conformance corpus for the supported Python syntax, written as self-contained cases
    /// (chapters 2/6/7/8 and the full grammar in chapter 10) plus the 3.11 syntax additions.
    /// Each entry is VALID Python the parser must accept with zero errors — a failure here is
    /// a coverage gap. The companion C++23/CUDA corpus lives in CudaVirtualCompiler.Tests.
    /// </summary>
    [TestClass]
    public class SpecConformanceTests
    {
        private static void AllClean(Dictionary<string, string> cases)
        {
            var failures = new List<string>();
            foreach (var c in cases)
            {
                ParseResult r = Python.ParseModule(c.Value, "spec.py");
                if (r.Diagnostics.Any(d => d.Severity == ParseSeverity.Error)) failures.Add(c.Key);
            }
            Assert.AreEqual(0, failures.Count,
                "spec features the parser no longer accepts: " + string.Join(", ", failures));
        }

        [TestMethod]
        public void LexicalAndExpressions_Parse()
        {
            AllClean(new Dictionary<string, string>
            {
                { "2.4 string prefixes", "a = 'x'\nb = r'\\d+'\nc = b'bytes'\nd = rb'\\x00'\ne = '''t'''\nf = 'a' 'b'\n" },
                { "2.4 f-strings", "s = f'{n!r:>{w}.{p}} {n=} {d[\"k\"]}'\n" },
                { "2.5 numeric literals", "a = 0x1F\nb = 0o17\nc = 0b1010\nd = 1_000_000\ne = 1_0.0_1e1_0\nf = 3j\ng = .5\nh = 5.\n" },
                { "6.2 displays + comprehensions", "a = [x for x in r if x]\nb = {x for x in r}\nc = {k: v for k, v in r}\nd = (x for x in r)\ne = {**x, **y}\nf = [*a, *b]\n" },
                { "6.3 slicing forms", "v = a[1:2:3]\nw = a[::2]\nx = a[:, 1]\ny = a[..., 0]\n" },
                { "6.12 conditional + lambda", "v = a if c else b\nf = lambda x, /, y=1, *a, k, **kw: x\n" },
                { "6.15 walrus", "if (n := len(a)) > 1:\n    pass\nb = [y := f(x), y ** 2]\n" },
                { "6.2.8 async comprehension", "async def f():\n    a = [await x async for x in it() if await p(x)]\n" },
                { "6.10 chained comparisons", "v = a < b <= c\nw = a is not b\nx = a not in b\n" },
            });
        }

        [TestMethod]
        public void Statements_Parse()
        {
            AllClean(new Dictionary<string, string>
            {
                { "7.2 assignment targets", "a = b = 1\nc, *d = [1, 2]\n*e, f = [1, 2]\ng.h = 1\ni[0] = 1\nj[0:2] = [1, 2]\n" },
                { "7.2.2 annotated assignment", "a: int = 1\nb: int\nc.d: int = 1\ne[0]: int = 1\n" },
                { "7.11 import forms", "import a.b.c as x\nfrom .m import (n, o,)\nfrom ...pkg.m import *\nfrom __future__ import annotations\n" },
                { "8.4 try/except/else/finally", "try:\n    pass\nexcept (E1, E2) as e:\n    pass\nexcept:\n    pass\nelse:\n    pass\nfinally:\n    pass\n" },
                { "8.4.1 except* (PEP 654, 3.11)", "try:\n    pass\nexcept* TypeError:\n    pass\nexcept* (ValueError, KeyError) as eg:\n    pass\n" },
                { "8.5 parenthesized with items", "with (open('a') as f, open('b') as g):\n    pass\n" },
                { "8.6 match patterns", "match p:\n    case 0 | 1:\n        pass\n    case [x, y, *rest]:\n        pass\n    case {'k': v, **rest}:\n        pass\n    case Point(x=0, y=0) as pt if pt:\n        pass\n    case _:\n        pass\n" },
                { "8.7 parameters + decorators", "@a.b(1)\n@buttons[0].clicked.connect\ndef f(a, /, b=1, *args, c, **kw) -> bool:\n    return True\n" },
                { "8.9 async def/for/with", "async def f():\n    async with c() as x:\n        async for y in it():\n            await g()\n" },
                { "PEP 604 unions", "def f(x: int | None = None) -> str | bytes:\n    y: dict[str, int | None] = {}\n" },
                { "PEP 646 star subscripts (3.11)", "x: Array[*Ts]\ndef f(*args: *Ts) -> None:\n    pass\n" },
                { "soft keywords as names", "match = 1\ncase = 2\ntype = 3\n" },
            });
        }
    }
}
