using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// Phase 2 — control flow. Each program is assembled, linked, executed and compared with
    /// CPython. Loops are the place where an off-by-one in the emitted comparison produces a
    /// number that still looks reasonable, so the counts are checked against the real interpreter
    /// rather than against an expectation typed here.
    /// </summary>
    [TestClass]
    public class ControlFlowTests
    {
        [TestMethod]
        public void IfElse()
        {
            const string source =
                "def f(a: int) -> int:\n"
                + "    if a > 0:\n"
                + "        return 1\n"
                + "    else:\n"
                + "        return -1\n";
            NasmOracle.AssertMatchesCPython(source, "f", 5);
            NasmOracle.AssertMatchesCPython(source, "f", -5);
        }

        [TestMethod]
        public void IfWithoutElse()
        {
            const string source =
                "def f(a: int) -> int:\n"
                + "    r = 0\n"
                + "    if a % 2 == 0:\n"
                + "        r = 100\n"
                + "    return r + a\n";
            NasmOracle.AssertMatchesCPython(source, "f", 4);
            NasmOracle.AssertMatchesCPython(source, "f", 7);
        }

        [TestMethod]
        public void ElifChain()
        {
            const string source =
                "def f(a: int) -> int:\n"
                + "    if a < 0:\n"
                + "        return -1\n"
                + "    elif a == 0:\n"
                + "        return 0\n"
                + "    elif a < 10:\n"
                + "        return 1\n"
                + "    else:\n"
                + "        return 2\n";
            foreach (long v in new long[] { -3, 0, 5, 50 })
                NasmOracle.AssertMatchesCPython(source, "f", v);
        }

        [TestMethod]
        public void WhileLoop()
        {
            const string source =
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    i = 0\n"
                + "    while i < n:\n"
                + "        total += i\n"
                + "        i += 1\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 10);
            NasmOracle.AssertMatchesCPython(source, "f", 0);      // the body must not run at all
            NasmOracle.AssertMatchesCPython(source, "f", -3);
        }

        [TestMethod]
        public void WhileWithBreakAndContinue()
        {
            const string source =
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    i = 0\n"
                + "    while True:\n"
                + "        i += 1\n"
                + "        if i > n:\n"
                + "            break\n"
                + "        if i % 3 == 0:\n"
                + "            continue\n"
                + "        total += i\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 20);
        }

        [TestMethod]
        public void ForRange_OneArgument()
        {
            const string source =
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(n):\n"
                + "        total += i * i\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 8);
            NasmOracle.AssertMatchesCPython(source, "f", 0);
            NasmOracle.AssertMatchesCPython(source, "f", -1);     // an empty range, not an infinite loop
        }

        [TestMethod]
        public void ForRange_TwoArguments()
        {
            const string source =
                "def f(a: int, b: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(a, b):\n"
                + "        total += i\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", -4, 5);
            NasmOracle.AssertMatchesCPython(source, "f", 5, 5);
        }

        [TestMethod]
        public void ForRange_ThreeArguments()
        {
            const string source =
                "def f(a: int, b: int, c: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(a, b, c):\n"
                + "        total += i\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 0, 20, 3);
            NasmOracle.AssertMatchesCPython(source, "f", 1, 2, 5);
        }

        [TestMethod]
        public void ForRange_NegativeStepCountsDown()
        {
            // The terminating comparison flips with the sign of the step; getting this wrong
            // either runs forever or never enters the loop.
            const string source =
                "def f(a: int, b: int, c: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(a, b, c):\n"
                + "        total = total * 10 + i\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 5, 0, -1);
            NasmOracle.AssertMatchesCPython(source, "f", 0, 5, -1);     // empty
        }

        [TestMethod]
        public void ForRangeBoundsAreEvaluatedOnce()
        {
            // Python evaluates range()'s arguments once, before the loop. Re-evaluating `n // 2`
            // on every iteration would still terminate here, which is why the count is compared
            // with CPython rather than merely checked for termination.
            const string source =
                "def f(n: int) -> int:\n"
                + "    count = 0\n"
                + "    for i in range(n // 2):\n"
                + "        n = n + 10\n"
                + "        count += 1\n"
                + "    return count\n";
            NasmOracle.AssertMatchesCPython(source, "f", 9);
        }

        [TestMethod]
        public void ForWithBreakAndContinue()
        {
            const string source =
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(n):\n"
                + "        if i % 2 == 0:\n"
                + "            continue\n"
                + "        if i > 15:\n"
                + "            break\n"
                + "        total += i\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 40);
        }

        [TestMethod]
        public void NestedLoopsBindBreakToTheInnermost()
        {
            const string source =
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(n):\n"
                + "        for j in range(n):\n"
                + "            if j > i:\n"
                + "                break\n"
                + "            total += 1\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 6);
        }

        [TestMethod]
        public void LoopInsideConditional()
        {
            const string source =
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    if n > 0:\n"
                + "        while n > 0:\n"
                + "            total += n\n"
                + "            n -= 1\n"
                + "    else:\n"
                + "        total = -1\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 5);
            NasmOracle.AssertMatchesCPython(source, "f", -5);
        }

        [TestMethod]
        public void ARealAlgorithm_GreatestCommonDivisor()
        {
            const string source =
                "def gcd(a: int, b: int) -> int:\n"
                + "    while b != 0:\n"
                + "        t = b\n"
                + "        b = a % b\n"
                + "        a = t\n"
                + "    return a\n";
            NasmOracle.AssertMatchesCPython(source, "gcd", 1071, 462);
            NasmOracle.AssertMatchesCPython(source, "gcd", -1071, 462);
        }

        [TestMethod]
        public void ARealAlgorithm_Fibonacci()
        {
            const string source =
                "def fib(n: int) -> int:\n"
                + "    a = 0\n"
                + "    b = 1\n"
                + "    for _ in range(n):\n"
                + "        t = a + b\n"
                + "        a = b\n"
                + "        b = t\n"
                + "    return a\n";
            NasmOracle.AssertMatchesCPython(source, "fib", 40);
        }

        [TestMethod]
        public void ARealAlgorithm_CountPrimesBelow()
        {
            const string source =
                "def count_primes(limit: int) -> int:\n"
                + "    found = 0\n"
                + "    for n in range(2, limit):\n"
                + "        d = 2\n"
                + "        prime = 1\n"
                + "        while d * d <= n:\n"
                + "            if n % d == 0:\n"
                + "                prime = 0\n"
                + "                break\n"
                + "            d += 1\n"
                + "        if prime:\n"
                + "            found += 1\n"
                + "    return found\n";
            NasmOracle.AssertMatchesCPython(source, "count_primes", 200);
        }
    }
}
