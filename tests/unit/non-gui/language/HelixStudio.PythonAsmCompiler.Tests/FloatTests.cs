using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// Phase 5 — IEEE-754 doubles.
    ///
    /// Every result is compared with CPython's as a raw 64-bit pattern. Comparing printed decimals
    /// would hide precisely the errors worth catching: an operation reassociated, an int widened
    /// at the wrong moment, or a sign bit produced by subtracting from zero instead of flipping.
    /// </summary>
    [TestClass]
    public class FloatTests
    {
        private static void Check(string signature, string body, params object[] args)
        {
            // `signature` carries its own closing paren and return annotation, e.g.
            // "a: float, b: float) -> float".
            NasmOracle.AssertMatches("def f(" + signature + ":\n    return " + body + "\n", "f", args);
        }

        // ---- arithmetic --------------------------------------------------

        [TestMethod] public void Addition() { Check("a: float, b: float) -> float", "a + b", 0.1, 0.2); }
        [TestMethod] public void Subtraction() { Check("a: float, b: float) -> float", "a - b", 0.3, 0.1); }
        [TestMethod] public void Multiplication() { Check("a: float, b: float) -> float", "a * b", 1.0 / 3.0, 3.0); }
        [TestMethod] public void Division() { Check("a: float, b: float) -> float", "a / b", 1.0, 3.0); }

        [TestMethod]
        public void AssociativityIsNotRearranged()
        {
            // (a + b) + c and a + (b + c) are different doubles. A compiler that "simplifies" the
            // expression tree silently changes the answer.
            Check("a: float, b: float, c: float) -> float", "(a + b) + c", 1e16, 1.0, -1e16);
            Check("a: float, b: float, c: float) -> float", "a + (b + c)", 1e16, 1.0, -1e16);
        }

        [TestMethod] public void FloatLiteral() { Check("a: float) -> float", "a * 3.14159265358979", 2.0); }
        [TestMethod] public void NegationFlipsTheSignBit() { Check("a: float) -> float", "-a", 0.0); }
        [TestMethod] public void NegationOfANegative() { Check("a: float) -> float", "-a", -2.5); }
        [TestMethod] public void UnaryPlus() { Check("a: float) -> float", "+a", -2.5); }

        [TestMethod]
        public void IntegerDivisionOfIntegersProducesAFloat()
        {
            // Python's `/` is true division even between two ints, and 1/3 is not representable.
            Check("a: int, b: int) -> float", "a / b", 1L, 3L);
            Check("a: int, b: int) -> float", "a / b", -7L, 2L);
        }

        [TestMethod]
        public void MixedArithmeticPromotesTheInteger()
        {
            Check("a: int, b: float) -> float", "a + b", 3L, 0.5);
            Check("a: float, b: int) -> float", "a * b", 0.1, 3L);
            Check("a: int, b: float) -> float", "a - b * 2", 1L, 0.7);
        }

        [TestMethod]
        public void AnIntegerReturnedFromAFloatFunctionIsWidened()
        {
            Check("a: int) -> float", "a * 2", 21L);
        }

        // ---- comparisons -------------------------------------------------

        [TestMethod] public void LessThan() { Check("a: float, b: float) -> int", "a < b", 0.1, 0.2); }
        [TestMethod] public void LessOrEqual() { Check("a: float, b: float) -> int", "a <= b", 0.2, 0.2); }
        [TestMethod] public void GreaterThan() { Check("a: float, b: float) -> int", "a > b", 0.3, 0.2); }
        [TestMethod] public void GreaterOrEqual() { Check("a: float, b: float) -> int", "a >= b", 0.1, 0.2); }
        [TestMethod] public void Equality() { Check("a: float, b: float) -> int", "a == b", 0.5, 0.5); }
        [TestMethod] public void Inequality() { Check("a: float, b: float) -> int", "a != b", 0.5, 0.25); }

        [TestMethod]
        public void ComparingAFloatWithAnInteger()
        {
            Check("a: float, b: int) -> int", "a < b", 2.5, 3L);
            Check("a: int, b: float) -> int", "a >= b", 3L, 2.5);
        }

        [TestMethod]
        public void EveryComparisonWithNaNIsFalseExceptInequality()
        {
            // ucomisd reports "unordered" by setting CF, ZF and PF together, which is
            // indistinguishable from "less or equal" unless the parity flag is consulted. Getting
            // this wrong makes NaN compare as though it were smaller than everything.
            foreach (string op in new[] { "<", "<=", ">", ">=", "==", "!=" })
            {
                Check("a: float, b: float) -> int", "a " + op + " b", double.NaN, 1.0);
                Check("a: float, b: float) -> int", "a " + op + " b", 1.0, double.NaN);
                Check("a: float, b: float) -> int", "a " + op + " b", double.NaN, double.NaN);
            }
        }

        [TestMethod]
        public void InfinitiesCompareAndArithmeticAsCPythonDoes()
        {
            Check("a: float, b: float) -> int", "a > b", double.PositiveInfinity, 1e308);
            Check("a: float, b: float) -> float", "a + b", double.PositiveInfinity, double.NegativeInfinity);
            // Overflow to infinity is silent in both, unlike division by zero: CPython raises
            // ZeroDivisionError where SSE quietly yields an infinity, and this subset has no
            // exception machinery to raise with. That divergence is documented, not tested.
            Check("a: float, b: float) -> float", "a * b", 1e308, 10.0);
        }

        // ---- truthiness and control flow ---------------------------------

        [TestMethod]
        public void ZeroIsFalseyAndNaNIsTruthy()
        {
            const string source =
                "def f(a: float) -> int:\n"
                + "    if a:\n"
                + "        return 1\n"
                + "    return 0\n";
            NasmOracle.AssertMatches(source, "f", 0.0);
            NasmOracle.AssertMatches(source, "f", -0.0);
            NasmOracle.AssertMatches(source, "f", 0.5);
            NasmOracle.AssertMatches(source, "f", double.NaN);
        }

        [TestMethod]
        public void NotOnAFloat()
        {
            Check("a: float) -> int", "not a", 0.0);
            Check("a: float) -> int", "not a", 2.5);
            Check("a: float) -> int", "not a", double.NaN);
        }

        [TestMethod]
        public void ShortCircuitYieldsTheOperandItself()
        {
            // Python's `or` returns the operand, not a bool, so 0.0 or 2.5 is 2.5 exactly.
            Check("a: float, b: float) -> float", "a or b", 0.0, 2.5);
            Check("a: float, b: float) -> float", "a or b", 1.5, 2.5);
            Check("a: float, b: float) -> float", "a and b", 0.0, 2.5);
            Check("a: float, b: float) -> float", "a and b", 1.5, 2.5);
        }

        [TestMethod]
        public void ConditionalExpressionWithFloats()
        {
            Check("a: float, b: float) -> float", "a if a > b else b", 1.5, 2.5);
            Check("a: float, b: float) -> float", "a if a > b else b", 3.5, 2.5);
        }

        [TestMethod]
        public void AFloatAccumulatorInALoop()
        {
            const string source =
                "def f(n: int) -> float:\n"
                + "    total = 0.0\n"
                + "    for i in range(n):\n"
                + "        total = total + 1.0 / (i + 1)\n"
                + "    return total\n";
            NasmOracle.AssertMatches(source, "f", 25L);
        }

        [TestMethod]
        public void AugmentedAssignmentOnAFloat()
        {
            const string source =
                "def f(a: float) -> float:\n"
                + "    x = a\n"
                + "    x += 0.5\n"
                + "    x *= 3.0\n"
                + "    x -= 1.25\n"
                + "    x /= 7.0\n"
                + "    return x\n";
            NasmOracle.AssertMatches(source, "f", 0.1);
        }

        [TestMethod]
        public void FloatTupleAssignmentSwaps()
        {
            const string source =
                "def f(a: float, b: float) -> float:\n"
                + "    a, b = b, a\n"
                + "    return a - b\n";
            NasmOracle.AssertMatches(source, "f", 0.25, 4.75);
        }

        // ---- conversions -------------------------------------------------

        [TestMethod]
        public void IntOfAFloatTruncatesTowardZero()
        {
            // Truncation, not flooring: int(-2.7) is -2 while -2.7 // 1 would be -3.
            foreach (double v in new[] { 2.7, -2.7, 0.0, -0.9, 1e15 })
                Check("a: float) -> int", "int(a)", v);
        }

        [TestMethod]
        public void FloatOfAnIntWidens()
        {
            Check("a: int) -> float", "float(a)", -1234567L);
        }

        [TestMethod]
        public void ConversionsAreIdentityOnTheirOwnType()
        {
            Check("a: int) -> int", "int(a)", 42L);
            Check("a: float) -> float", "float(a)", 4.25);
        }

        [TestMethod]
        public void ConversionInsideAnExpression()
        {
            Check("a: float, b: int) -> int", "int(a * 2.0) + b", 3.75, 10L);
        }

        // ---- calls -------------------------------------------------------

        [TestMethod]
        public void CallingAFloatFunction()
        {
            const string source =
                "def scale(x: float, k: float) -> float:\n"
                + "    return x * k\n"
                + "\n"
                + "def f(a: float) -> float:\n"
                + "    return scale(a, 0.5) + scale(a, 0.25)\n";
            NasmOracle.AssertMatches(source, "f", 0.3);
        }

        [TestMethod]
        public void MixedIntAndFloatParametersUseTheRightRegisters()
        {
            // Under Win64 the register is chosen by POSITION and type together: an int in slot 1
            // goes to RDX while a double in slot 1 goes to XMM1.
            const string source =
                "def mix(a: int, b: float, c: int, d: float) -> float:\n"
                + "    return a * 1000.0 + b * 100.0 + c * 10.0 + d\n"
                + "\n"
                + "def f(a: int) -> float:\n"
                + "    return mix(a, 0.5, a + 1, 0.25)\n";
            NasmOracle.AssertMatches(source, "f", 7L);
        }

        [TestMethod]
        public void FloatArgumentsPastTheFourthTravelOnTheStack()
        {
            const string source =
                "def six(a: float, b: float, c: float, d: float, e: float, g: float) -> float:\n"
                + "    return a + b * 2.0 + c * 4.0 + d * 8.0 + e * 16.0 + g * 32.0\n";
            NasmOracle.AssertMatches(source, "six", 0.5, 0.25, 0.125, 1.5, 2.25, 3.125);
        }

        [TestMethod]
        public void RecursionOverFloats()
        {
            const string source =
                "def power(base: float, n: int) -> float:\n"
                + "    if n == 0:\n"
                + "        return 1.0\n"
                + "    return base * power(base, n - 1)\n";
            NasmOracle.AssertMatches(source, "power", 1.1, 20L);
        }

        [TestMethod]
        public void ARealNumericProgram()
        {
            // Newton's method: several float locals live across a loop and a call.
            const string source =
                "def better(x: float, guess: float) -> float:\n"
                + "    return (guess + x / guess) / 2.0\n"
                + "\n"
                + "def sqrt(x: float) -> float:\n"
                + "    guess = 1.0\n"
                + "    for _ in range(40):\n"
                + "        guess = better(x, guess)\n"
                + "    return guess\n";
            NasmOracle.AssertMatches(source, "sqrt", 2.0);
            NasmOracle.AssertMatches(source, "sqrt", 1e6);
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
        public void FloorDivisionOnFloatsIsRefused()
        {
            // CPython routes float // and % through libm's fmod and then corrects the sign;
            // floor(a/b) differs in the last place, and being nearly right is the worst outcome.
            AssertRefused("def f(a: float, b: float) -> float:\n    return a // b\n", "fmod");
        }

        [TestMethod]
        public void ModuloOnFloatsIsRefused()
        {
            AssertRefused("def f(a: float, b: float) -> float:\n    return a % b\n", "fmod");
        }

        [TestMethod]
        public void BitwiseOperatorsOnFloatsAreRefused()
        {
            AssertRefused("def f(a: float, b: float) -> float:\n    return a & b\n", "not defined for floats");
            AssertRefused("def f(a: float) -> int:\n    return ~a\n", "not defined for floats");
        }

        [TestMethod]
        public void AFloatWhereAnIntIsRequiredIsRefused()
        {
            AssertRefused("def f(a: float) -> int:\n    return a\n",
                          "use int(...) to truncate it explicitly");
        }

        [TestMethod]
        public void ALocalThatChangesTypeIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> float:\n"
                + "    x = 0\n"
                + "    x = x + 1.5\n"
                + "    return x\n",
                "used as both int and float");
        }

        [TestMethod]
        public void AnAugmentedAssignmentThatChangesTypeIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> float:\n"
                + "    x = 0\n"
                + "    x += 1.5\n"
                + "    return x\n",
                "would change 'x' from int to float");
        }

        [TestMethod]
        public void RangeOverFloatsIsRefused()
        {
            AssertRefused(
                "def f(a: float) -> int:\n"
                + "    n = 0\n"
                + "    for i in range(a):\n"
                + "        n += 1\n"
                + "    return n\n",
                "range() takes integer arguments");
        }

        [TestMethod]
        public void AnUnknownAnnotationIsRefused()
        {
            AssertRefused("def f(a: str) -> int:\n    return 1\n", "'int' or 'float'");
            AssertRefused("def f(a: int) -> str:\n    return 1\n", "'-> int' or '-> float'");
        }

        [TestMethod]
        public void RedefiningABuiltinConversionIsRefused()
        {
            AssertRefused("def int(a: float) -> int:\n    return 1\n", "cannot be redefined");
        }

        [TestMethod]
        public void TheSignatureIsReportedBackToTheCaller()
        {
            TranslationResult result = PythonToNasm.Translate(
                "def f(a: int, b: float) -> float:\n    return a + b\n");
            Assert.IsTrue(result.Succeeded, result.RefusalReason);
            Assert.AreEqual(1, result.Signatures.Count);
            Assert.AreEqual("f(int, float) -> float", result.Signatures[0].ToString());
        }
    }
}
