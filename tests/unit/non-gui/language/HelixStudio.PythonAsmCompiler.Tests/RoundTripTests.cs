using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// Phase 6 — NASM back to Python.
    ///
    /// The oracle is a round trip, not a text comparison. Compilation legitimately loses
    /// <c>+=</c>, <c>elif</c>, <c>while True</c> and the difference between an explicit
    /// <c>float(x)</c> and an implicit promotion; none of those change what the program computes.
    /// So each case is compiled, read back, compiled again, EXECUTED, and compared with CPython
    /// running the original — and the second decompilation must be a fixed point, because a round
    /// trip that keeps drifting will eventually drift into a different program.
    /// </summary>
    [TestClass]
    public class RoundTripTests
    {
        // ---- expressions -------------------------------------------------

        [TestMethod]
        public void ArithmeticAndPrecedence()
        {
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int, c: int, d: int) -> int:\n"
                + "    return (a + b) * (c - d) - a * b + (c + d) // (a + 7) % 5\n",
                "f", 3L, -4L, 11L, 6L);
        }

        [TestMethod]
        public void ParenthesesAreRestoredWhereTheyChangeTheAnswer()
        {
            // a - (b - c) and (a - b) - c differ. If the decompiler drops the parentheses because
            // it renders the tree left to right, the recovered program computes something else.
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int, c: int) -> int:\n    return a - (b - c)\n",
                "f", 10L, 4L, 3L);
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int, c: int) -> int:\n    return a // (b * c)\n",
                "f", 100L, 3L, 7L);
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int, c: int) -> int:\n    return (a | b) & c\n",
                "f", 12L, 10L, 6L);
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int) -> int:\n    return -(a + b) * 2\n",
                "f", 5L, 9L);
        }

        [TestMethod]
        public void FloorDivisionAndModuloKeepTheirSignCorrection()
        {
            const string source = "def f(a: int, b: int) -> int:\n    return a // b + a % b\n";
            NasmOracle.AssertRoundTrips(source, "f", -7L, 2L);
            NasmOracle.AssertRoundTrips(source, "f", 7L, -2L);
        }

        [TestMethod]
        public void ComparisonsAndLogic()
        {
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int, c: int) -> int:\n"
                + "    return (a < b and b < c) or (a == c and not b)\n",
                "f", 1L, 2L, 3L);
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int, c: int) -> int:\n"
                + "    return (a < b and b < c) or (a == c and not b)\n",
                "f", 3L, 0L, 3L);
        }

        [TestMethod]
        public void ConditionalExpressions()
        {
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int) -> int:\n"
                + "    return (a if a > b else b) + (1 if a else 2)\n",
                "f", 4L, 9L);
        }

        [TestMethod]
        public void ShiftsAndBitwiseOperators()
        {
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int) -> int:\n"
                + "    return ((a << b) ^ (a >> b)) & 0xFFFF | ~a\n",
                "f", -1234L, 3L);
        }

        // ---- control flow ------------------------------------------------

        [TestMethod]
        public void IfElseChains()
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
                NasmOracle.AssertRoundTrips(source, "f", v);
        }

        [TestMethod]
        public void WhileWithBreakAndContinue()
        {
            NasmOracle.AssertRoundTrips(
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
                + "    return total\n",
                "f", 20L);
        }

        [TestMethod]
        public void ForLoopsInEveryArity()
        {
            NasmOracle.AssertRoundTrips(
                "def f(n: int) -> int:\n"
                + "    t = 0\n"
                + "    for i in range(n):\n"
                + "        t += i\n"
                + "    return t\n", "f", 9L);
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int) -> int:\n"
                + "    t = 0\n"
                + "    for i in range(a, b):\n"
                + "        t += i\n"
                + "    return t\n", "f", -4L, 5L);
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int, c: int) -> int:\n"
                + "    t = 0\n"
                + "    for i in range(a, b, c):\n"
                + "        t = t * 10 + i\n"
                + "    return t\n", "f", 5L, 0L, -1L);
        }

        [TestMethod]
        public void NestedLoopsKeepTheirOwnLabels()
        {
            // Each loop's five labels are numbered consecutively. Matching a loop to the NEAREST
            // step label instead would splice an inner body onto an outer loop.
            NasmOracle.AssertRoundTrips(
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(n):\n"
                + "        for j in range(n, 0, -1):\n"
                + "            if j > 3:\n"
                + "                break\n"
                + "            total += i * j\n"
                + "        total += 1\n"
                + "    return total\n",
                "f", 6L);
        }

        [TestMethod]
        public void ALoopInsideAConditionalInsideALoop()
        {
            NasmOracle.AssertRoundTrips(
                "def f(n: int) -> int:\n"
                + "    total = 0\n"
                + "    for i in range(n):\n"
                + "        if i % 2 == 0:\n"
                + "            k = i\n"
                + "            while k > 0:\n"
                + "                total += k\n"
                + "                k -= 1\n"
                + "        else:\n"
                + "            total -= i\n"
                + "    return total\n",
                "f", 8L);
        }

        // ---- locals ------------------------------------------------------

        [TestMethod]
        public void TupleAssignmentSurvivesAsASwap()
        {
            // Decoding the temp-then-store pattern as two statements would turn the swap into two
            // copies of b — the single most likely way to get this silently wrong.
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: int) -> int:\n"
                + "    a, b = b, a\n"
                + "    return a * 100 + b\n",
                "f", 3L, 7L);
        }

        [TestMethod]
        public void ChainedAssignmentIsNotDuplicated()
        {
            // `x = y = g()` recovered as two statements would call g twice.
            NasmOracle.AssertRoundTrips(
                "def g(a: int) -> int:\n    return a + 1\n"
                + "def f(a: int) -> int:\n"
                + "    x = y = g(a)\n"
                + "    return x * 10 + y\n",
                "f", 4L);
        }

        [TestMethod]
        public void TheFibonacciIdiom()
        {
            NasmOracle.AssertRoundTrips(
                "def fib(n: int) -> int:\n"
                + "    a, b = 0, 1\n"
                + "    for _ in range(n):\n"
                + "        a, b = b, a + b\n"
                + "    return a\n",
                "fib", 40L);
        }

        // ---- calls -------------------------------------------------------

        [TestMethod]
        public void CallsAndRecursion()
        {
            NasmOracle.AssertRoundTrips(
                "def fact(n: int) -> int:\n"
                + "    if n <= 1:\n"
                + "        return 1\n"
                + "    return n * fact(n - 1)\n",
                "fact", 15L);
        }

        [TestMethod]
        public void MutualRecursion()
        {
            NasmOracle.AssertRoundTrips(
                "def is_even(n: int) -> int:\n"
                + "    if n == 0:\n"
                + "        return 1\n"
                + "    return is_odd(n - 1)\n"
                + "def is_odd(n: int) -> int:\n"
                + "    if n == 0:\n"
                + "        return 0\n"
                + "    return is_even(n - 1)\n",
                "is_even", 51L);
        }

        [TestMethod]
        public void NestedCallArguments()
        {
            NasmOracle.AssertRoundTrips(
                "def add(a: int, b: int) -> int:\n    return a + b\n"
                + "def mul(a: int, b: int) -> int:\n    return a * b\n"
                + "def f(a: int, b: int, c: int) -> int:\n"
                + "    return add(mul(a, b), mul(add(a, c), add(b, c)))\n",
                "f", 3L, -4L, 9L);
        }

        [TestMethod]
        public void StackPassedArgumentsAreRecoveredInOrder()
        {
            // Arguments five and six are written to [rsp+32] and [rsp+40] in descending order and
            // read back in ascending order; getting that backwards swaps them silently.
            NasmOracle.AssertRoundTrips(
                "def six(a: int, b: int, c: int, d: int, e: int, g: int) -> int:\n"
                + "    return a * 100000 + b * 10000 + c * 1000 + d * 100 + e * 10 + g\n"
                + "def f(a: int) -> int:\n"
                + "    return six(a, a + 1, a + 2, a + 3, a + 4, a + 5)\n",
                "f", 1L);
        }

        [TestMethod]
        public void ACallEvaluatedOnlyForItsEffectIsNotLost()
        {
            NasmOracle.AssertRoundTrips(
                "def slow(n: int) -> int:\n"
                + "    t = 0\n"
                + "    for i in range(n):\n"
                + "        t += i\n"
                + "    return t\n"
                + "def f(a: int) -> int:\n"
                + "    slow(a)\n"
                + "    return a * 2\n",
                "f", 5L);
        }

        // ---- floats ------------------------------------------------------

        [TestMethod]
        public void FloatArithmeticKeepsItsExactBits()
        {
            NasmOracle.AssertRoundTrips(
                "def f(a: float, b: float) -> float:\n"
                + "    return (a + b) * 0.1 - a / b\n",
                "f", 0.1, 0.3);
        }

        [TestMethod]
        public void FloatLiteralsSurviveTheTrip()
        {
            // The literal goes out as a bit pattern and comes back as decimal text; if that text
            // does not parse to the identical double, the recovered program is a different one.
            foreach (double v in new[] { 3.14159265358979, 1e-300, 1e300, 0.1, -0.0, 1.0 / 3.0 })
                NasmOracle.AssertRoundTrips(
                    "def f(a: float) -> float:\n    return a * " + v.ToString("R") + "\n",
                    "f", 2.0);
        }

        [TestMethod]
        public void FloatComparisonsIncludingNaN()
        {
            const string source =
                "def f(a: float, b: float) -> int:\n"
                + "    if a < b:\n"
                + "        return 1\n"
                + "    if a == b:\n"
                + "        return 2\n"
                + "    if a != b:\n"
                + "        return 3\n"
                + "    return 4\n";
            NasmOracle.AssertRoundTrips(source, "f", 1.0, 2.0);
            NasmOracle.AssertRoundTrips(source, "f", 2.0, 2.0);
            NasmOracle.AssertRoundTrips(source, "f", double.NaN, 2.0);
        }

        [TestMethod]
        public void MixedIntAndFloatWork()
        {
            NasmOracle.AssertRoundTrips(
                "def f(a: int, b: float) -> float:\n"
                + "    return a + b * 2 - float(a) / 4.0\n",
                "f", 7L, 0.25);
        }

        [TestMethod]
        public void FloatTruthinessAndShortCircuit()
        {
            const string source =
                "def f(a: float, b: float) -> float:\n"
                + "    r = a or b\n"
                + "    if r:\n"
                + "        r = r + 1.0\n"
                + "    return r\n";
            NasmOracle.AssertRoundTrips(source, "f", 0.0, 2.5);
            NasmOracle.AssertRoundTrips(source, "f", 1.5, 2.5);
        }

        [TestMethod]
        public void ConversionsRoundTrip()
        {
            NasmOracle.AssertRoundTrips(
                "def f(a: float) -> int:\n    return int(a) + int(a * 2.0)\n",
                "f", -2.7);
        }

        [TestMethod]
        public void ARealNumericProgram()
        {
            NasmOracle.AssertRoundTrips(
                "def better(x: float, guess: float) -> float:\n"
                + "    return (guess + x / guess) / 2.0\n"
                + "def sqrt(x: float) -> float:\n"
                + "    guess = 1.0\n"
                + "    for _ in range(40):\n"
                + "        guess = better(x, guess)\n"
                + "    return guess\n",
                "sqrt", 2.0);
        }

        [TestMethod]
        public void ARealIntegerProgram()
        {
            NasmOracle.AssertRoundTrips(
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
                + "    return found\n",
                "count_primes", 200L);
        }

        // ---- what it declines --------------------------------------------

        private static void AssertRefused(string assembly, string expectedFragment)
        {
            DecompileResult result = NasmToPython.Decompile(assembly);
            Assert.IsFalse(result.Succeeded, "expected a refusal; got:\n" + result.Source);
            StringAssert.Contains(result.RefusalReason, expectedFragment,
                "it said: " + result.RefusalReason);
        }

        [TestMethod]
        public void AssemblyWithoutASymbolMapIsRefused()
        {
            // Hand-written NASM records nothing about which offsets were variables or which jumps
            // were loops, and inventing an answer is how a decompiler produces plausible fiction.
            AssertRefused(
                "section .text\nglobal f\nf:\n    mov rax, 1\n    ret\n",
                "no '; helix.function' blocks found");
        }

        [TestMethod]
        public void EmptyInputIsRefused()
        {
            AssertRefused("", "empty assembly");
        }

        [TestMethod]
        public void AnInstructionOutsideTheEmittedSubsetIsRefused()
        {
            TranslationResult forward = PythonToNasm.Translate(
                "def f(a: int) -> int:\n    return a + 1\n");
            Assert.IsTrue(forward.Succeeded, forward.RefusalReason);

            // Splice in something the emitter never produces. The decompiler must say so rather
            // than skip it and hand back a program that quietly omits an instruction.
            string tampered = forward.Assembly.Replace("    add     rax, rcx",
                                                       "    rol     rax, 3\n    add     rax, rcx");
            AssertRefused(tampered, "outside the shapes HelixPython");
        }

        [TestMethod]
        public void TheRecoveredSourceNamesTheOriginalVariables()
        {
            // The names come from the ; helix. block. Without it the best a decompiler can do is
            // v1, v2, v3 — which is exactly why the emitter writes it.
            string recovered = NasmOracle.AssertRoundTrips(
                "def area(width: int, height: int) -> int:\n"
                + "    half = width * height\n"
                + "    return half // 2\n",
                "area", 6L, 7L);
            StringAssert.Contains(recovered, "def area(width: int, height: int) -> int:");
            StringAssert.Contains(recovered, "half = width * height");
        }
    }
}
