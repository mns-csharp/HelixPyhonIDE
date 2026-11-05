using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// Phase 4 — functions and calls.
    ///
    /// Every program here is assembled, linked, executed and compared with CPython. A calling
    /// convention is the one part of a compiler where "it worked on my test" is least meaningful:
    /// a missing shadow-space reservation or a misaligned RSP produces code that runs correctly
    /// until the callee happens to spill a register or use SSE.
    /// </summary>
    [TestClass]
    public class FunctionCallTests
    {
        [TestMethod]
        public void ASimpleCall()
        {
            const string source =
                "def double(a: int) -> int:\n"
                + "    return a * 2\n"
                + "\n"
                + "def f(a: int) -> int:\n"
                + "    return double(a) + 1\n";
            NasmOracle.AssertMatchesCPython(source, "f", 21);
        }

        [TestMethod]
        public void AForwardReferenceResolves()
        {
            // Python resolves the name when the call runs, not when the def is read, so calling a
            // function defined further down the file is ordinary code.
            const string source =
                "def f(a: int) -> int:\n"
                + "    return later(a) * 3\n"
                + "\n"
                + "def later(a: int) -> int:\n"
                + "    return a + 7\n";
            NasmOracle.AssertMatchesCPython(source, "f", 5);
        }

        [TestMethod]
        public void NestedCallsAsArguments()
        {
            const string source =
                "def add(a: int, b: int) -> int:\n"
                + "    return a + b\n"
                + "\n"
                + "def mul(a: int, b: int) -> int:\n"
                + "    return a * b\n"
                + "\n"
                + "def f(a: int, b: int, c: int) -> int:\n"
                + "    return add(mul(a, b), mul(add(a, c), add(b, c)))\n";
            NasmOracle.AssertMatchesCPython(source, "f", 3, -4, 9);
        }

        [TestMethod]
        public void ACallInsideALoopCondition()
        {
            const string source =
                "def limit(a: int) -> int:\n"
                + "    return a * 2\n"
                + "\n"
                + "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    i = 0\n"
                + "    while i < limit(n):\n"
                + "        total += i\n"
                + "        i += 1\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", 9);
        }

        [TestMethod]
        public void RecursionFactorial()
        {
            const string source =
                "def fact(n: int) -> int:\n"
                + "    if n <= 1:\n"
                + "        return 1\n"
                + "    return n * fact(n - 1)\n";
            NasmOracle.AssertMatchesCPython(source, "fact", 20);
        }

        [TestMethod]
        public void RecursionFibonacci()
        {
            const string source =
                "def fib(n: int) -> int:\n"
                + "    if n < 2:\n"
                + "        return n\n"
                + "    return fib(n - 1) + fib(n - 2)\n";
            NasmOracle.AssertMatchesCPython(source, "fib", 24);
        }

        [TestMethod]
        public void MutualRecursion()
        {
            const string source =
                "def is_even(n: int) -> int:\n"
                + "    if n == 0:\n"
                + "        return 1\n"
                + "    return is_odd(n - 1)\n"
                + "\n"
                + "def is_odd(n: int) -> int:\n"
                + "    if n == 0:\n"
                + "        return 0\n"
                + "    return is_even(n - 1)\n";
            NasmOracle.AssertMatchesCPython(source, "is_even", 101);
            NasmOracle.AssertMatchesCPython(source, "is_odd", 101);
        }

        [TestMethod]
        public void DeepRecursionKeepsTheFramesIntact()
        {
            // 800 nested frames: enough that a prologue or epilogue that leaked even one push
            // would drift RSP far enough to fault.
            const string source =
                "def down(n: int) -> int:\n"
                + "    if n == 0:\n"
                + "        return 0\n"
                + "    return n + down(n - 1)\n";
            NasmOracle.AssertMatchesCPython(source, "down", 800);
        }

        [TestMethod]
        public void ARecursiveFunctionThatKeepsLocalsAcrossTheCall()
        {
            // The locals must survive the callee, which is the whole point of a frame.
            const string source =
                "def ack(m: int, n: int) -> int:\n"
                + "    if m == 0:\n"
                + "        return n + 1\n"
                + "    if n == 0:\n"
                + "        return ack(m - 1, 1)\n"
                + "    inner = ack(m, n - 1)\n"
                + "    return ack(m - 1, inner)\n";
            NasmOracle.AssertMatchesCPython(source, "ack", 2, 3);
        }

        [TestMethod]
        public void FourArgumentsFillTheRegisters()
        {
            const string source =
                "def take4(a: int, b: int, c: int, d: int) -> int:\n"
                + "    return a * 1000 + b * 100 + c * 10 + d\n";
            NasmOracle.AssertMatchesCPython(source, "take4", 1, 2, 3, 4);
        }

        [TestMethod]
        public void MoreThanFourParametersArriveOnTheStack()
        {
            // Arguments past the fourth are read from [rbp + 8*(i+2)], above the return address
            // and the saved RBP. Off by one slot here and the function silently reads the caller's
            // frame instead.
            const string source =
                "def take6(a: int, b: int, c: int, d: int, e: int, g: int) -> int:\n"
                + "    return a * 100000 + b * 10000 + c * 1000 + d * 100 + e * 10 + g\n";
            NasmOracle.AssertMatchesCPython(source, "take6", 1, 2, 3, 4, 5, 6);
        }

        [TestMethod]
        public void CallingAFunctionWithStackArguments()
        {
            const string source =
                "def take6(a: int, b: int, c: int, d: int, e: int, g: int) -> int:\n"
                + "    return a - b + c - d + e - g\n"
                + "\n"
                + "def f(a: int) -> int:\n"
                + "    return take6(a, a + 1, a + 2, a + 3, a + 4, a + 5)\n";
            NasmOracle.AssertMatchesCPython(source, "f", 10);
        }

        [TestMethod]
        public void CallingWithStackArgumentsThatAreThemselvesCalls()
        {
            // The outer call's stack slots are written only after every argument has been
            // evaluated, so an inner call cannot overwrite an argument already staged for the
            // outer one.
            const string source =
                "def sq(a: int) -> int:\n"
                + "    return a * a\n"
                + "\n"
                + "def take6(a: int, b: int, c: int, d: int, e: int, g: int) -> int:\n"
                + "    return a + b * 2 + c * 3 + d * 4 + e * 5 + g * 6\n"
                + "\n"
                + "def f(a: int) -> int:\n"
                + "    return take6(sq(a), sq(a + 1), sq(a + 2), sq(a + 3), sq(a + 4), sq(a + 5))\n";
            NasmOracle.AssertMatchesCPython(source, "f", 3);
        }

        [TestMethod]
        public void ACallWithNoArgumentsStillReservesShadowSpace()
        {
            const string source =
                "def answer() -> int:\n"
                + "    return 42\n"
                + "\n"
                + "def f(a: int) -> int:\n"
                + "    return answer() + a\n";
            NasmOracle.AssertMatchesCPython(source, "f", 8);
        }

        [TestMethod]
        public void ACallUsedForItsValueInEveryPosition()
        {
            const string source =
                "def one() -> int:\n"
                + "    return 1\n"
                + "\n"
                + "def f(a: int) -> int:\n"
                + "    total = one()\n"
                + "    total += one()\n"
                + "    if one() == 1:\n"
                + "        total = total * (a + one())\n"
                + "    for i in range(one() + 2):\n"
                + "        total += i\n"
                + "    return total + (one() if a > 0 else -one())\n";
            NasmOracle.AssertMatchesCPython(source, "f", 6);
            NasmOracle.AssertMatchesCPython(source, "f", -6);
        }

        [TestMethod]
        public void ARealProgramSpanningSeveralFunctions()
        {
            const string source =
                "def gcd(a: int, b: int) -> int:\n"
                + "    while b != 0:\n"
                + "        a, b = b, a % b\n"
                + "    return a\n"
                + "\n"
                + "def lcm(a: int, b: int) -> int:\n"
                + "    return a // gcd(a, b) * b\n"
                + "\n"
                + "def f(n: int) -> int:\n"
                + "    acc = 1\n"
                + "    for i in range(1, n + 1):\n"
                + "        acc = lcm(acc, i)\n"
                + "    return acc\n";
            NasmOracle.AssertMatchesCPython(source, "f", 20);
        }

        // ---- refusals ----------------------------------------------------

        private static void AssertRefused(string source, string expectedFragment)
        {
            TranslationResult result = PythonToNasm.Translate(source);
            Assert.IsFalse(result.Succeeded, "expected a refusal; got:\n" + result.Assembly);
            StringAssert.Contains(result.RefusalReason, expectedFragment,
                "it said: " + result.RefusalReason);
        }

        [TestMethod]
        public void CallingAnUndefinedFunctionIsRefused()
        {
            AssertRefused("def f(a: int) -> int:\n    return helper(a)\n",
                          "not a function defined in this module");
        }

        [TestMethod]
        public void CallingABuiltinIsRefused()
        {
            AssertRefused("def f(a: int) -> int:\n    return abs(a)\n",
                          "built-ins and imports have no implementation to call");
        }

        [TestMethod]
        public void TheWrongNumberOfArgumentsIsRefused()
        {
            // This is the failure a linker cannot catch: the call assembles and the callee reads a
            // register nobody set.
            AssertRefused(
                "def g(a: int, b: int) -> int:\n    return a + b\n"
                + "def f(a: int) -> int:\n    return g(a)\n",
                "'g' takes 2 arguments but 1 were given");
        }

        [TestMethod]
        public void KeywordArgumentsAreRefused()
        {
            AssertRefused(
                "def g(a: int, b: int) -> int:\n    return a + b\n"
                + "def f(a: int) -> int:\n    return g(a, b=2)\n",
                "keyword arguments are not supported");
        }

        [TestMethod]
        public void ArgumentUnpackingIsRefused()
        {
            AssertRefused(
                "def g(a: int, b: int) -> int:\n    return a + b\n"
                + "def f(a: int) -> int:\n    return g(*a)\n",
                "argument unpacking ('*') is not supported");
        }

        [TestMethod]
        public void RangeOutsideAForStatementIsRefused()
        {
            AssertRefused("def f(a: int) -> int:\n    return range(a)\n",
                          "only supported as the iterable of a 'for' statement");
        }

        [TestMethod]
        public void CallingSomethingThatIsNotAPlainNameIsRefused()
        {
            AssertRefused("def f(a: int) -> int:\n    return obj.method(a)\n",
                          "no function objects");
        }

        [TestMethod]
        public void TwoFunctionsWithTheSameNameAreRefused()
        {
            // Python keeps the last definition; an object file cannot hold two symbols of one
            // name, so translating both would either fail at link time or silently pick one.
            AssertRefused(
                "def f(a: int) -> int:\n    return a\n"
                + "def f(a: int, b: int) -> int:\n    return a + b\n",
                "defined more than once");
        }

        [TestMethod]
        public void PositionalOnlyParametersAreRefused()
        {
            AssertRefused("def f(a: int, /, b: int) -> int:\n    return a + b\n",
                          "positional-only");
        }

        [TestMethod]
        public void KeywordOnlyParametersAreRefused()
        {
            AssertRefused("def f(a: int, *, b: int) -> int:\n    return a + b\n",
                          "keyword-only");
        }
    }
}
