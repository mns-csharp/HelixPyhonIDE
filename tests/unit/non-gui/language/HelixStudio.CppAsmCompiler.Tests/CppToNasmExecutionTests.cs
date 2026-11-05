using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.CppAsmCompiler.Tests
{
    /// <summary>
    /// Every test here compiles C++ to NASM, assembles it, runs it, and compares the result with
    /// MSVC compiling the same source. The cases are chosen where a plausible backend is wrong:
    /// signs, overflow, evaluation order, NaN.
    /// </summary>
    [TestClass]
    public class CppToNasmExecutionTests
    {
        // ---- integers ---------------------------------------------------------------------

        [TestMethod]
        public void IntegerArithmetic()
        {
            const string src = "int f(int a, int b) { return a * b + a - b; }";
            CppOracle.AssertMatchesCpp(src, "f", 6, 7);
            CppOracle.AssertMatchesCpp(src, "f", -6, 7);
            CppOracle.AssertMatchesCpp(src, "f", 0, 0);
        }

        /// <summary>
        /// C++ division truncates toward zero; Python's floors. Mixed signs are where the two
        /// disagree, and where reusing the Python emitter would have produced wrong answers.
        /// </summary>
        [TestMethod]
        public void DivisionAndModuloTruncateTowardZero()
        {
            const string src = "int f(int a, int b) { return a / b; }";
            const string mod = "int g(int a, int b) { return a % b; }";
            foreach (var pair in new[] { (7, 2), (-7, 2), (7, -2), (-7, -2), (1, 3), (-1, 3) })
            {
                CppOracle.AssertMatchesCpp(src, "f", pair.Item1, pair.Item2);
                CppOracle.AssertMatchesCpp(mod, "g", pair.Item1, pair.Item2);
            }
        }

        /// <summary>`int` is 32 bits and wraps. A 64-bit accumulator would quietly disagree.</summary>
        [TestMethod]
        public void IntegerOverflowWrapsAt32Bits()
        {
            const string src = "int f(int a, int b) { return a * b; }";
            CppOracle.AssertMatchesCpp(src, "f", 100000, 100000);
            CppOracle.AssertMatchesCpp(src, "f", 2147483647, 2);
            CppOracle.AssertMatchesCpp(src, "f", -2147483648, -1);
        }

        /// <summary>`>>` on a signed int is arithmetic: a logical shift makes negatives positive.</summary>
        [TestMethod]
        public void ShiftsAndBitwiseOperators()
        {
            const string src = "int f(int a, int b) { return ((a << 2) ^ (a >> b)) & 0x7fff | 1; }";
            CppOracle.AssertMatchesCpp(src, "f", 12345, 3);
            CppOracle.AssertMatchesCpp(src, "f", -12345, 3);
        }

        [TestMethod]
        public void ComparisonsAndLogicalOperators()
        {
            const string src = "int f(int a, int b) { return (a < b) + 2 * (a == b) + 4 * (a >= b); }";
            CppOracle.AssertMatchesCpp(src, "f", 3, 5);
            CppOracle.AssertMatchesCpp(src, "f", 5, 5);
            CppOracle.AssertMatchesCpp(src, "f", 9, 5);

            const string sc = "int f(int a, int b) { return (a != 0 && b / a > 2) || b == 0; }";
            // b / a would divide by zero if && evaluated both sides — the crash IS the assertion.
            CppOracle.AssertMatchesCpp(sc, "f", 0, 10);
            CppOracle.AssertMatchesCpp(sc, "f", 3, 10);
        }

        [TestMethod]
        public void UnaryOperators()
        {
            const string src = "int f(int a) { return -a + !a + ~a; }";
            CppOracle.AssertMatchesCpp(src, "f", 5);
            CppOracle.AssertMatchesCpp(src, "f", 0);
            CppOracle.AssertMatchesCpp(src, "f", -5);
        }

        /// <summary>Post-increment yields the OLD value; pre- the new one.</summary>
        [TestMethod]
        public void PreAndPostIncrementDifferInTheirValue()
        {
            CppOracle.AssertMatchesCpp("int f(int a) { int b = a++; return b * 10 + a; }", "f", 4);
            CppOracle.AssertMatchesCpp("int f(int a) { int b = ++a; return b * 10 + a; }", "f", 4);
            CppOracle.AssertMatchesCpp("int f(int a) { int b = a--; return b * 10 + a; }", "f", 4);
        }

        [TestMethod]
        public void CompoundAssignment()
        {
            const string src = "int f(int a, int b) { int c = a; c += b; c *= 3; c -= 1; c /= 2; c %= 97; return c; }";
            CppOracle.AssertMatchesCpp(src, "f", 10, 4);
            CppOracle.AssertMatchesCpp(src, "f", -10, 4);
        }

        [TestMethod]
        public void ConditionalOperator()
        {
            const string src = "int f(int a, int b) { return a > b ? a - b : b - a; }";
            CppOracle.AssertMatchesCpp(src, "f", 9, 4);
            CppOracle.AssertMatchesCpp(src, "f", 4, 9);
        }

        // ---- control flow -----------------------------------------------------------------

        [TestMethod]
        public void IfElseChain()
        {
            const string src = @"
int classify(int n) {
    if (n < 0) { return -1; }
    else if (n == 0) { return 0; }
    else if (n < 10) { return 1; }
    else { return 2; }
}";
            foreach (int n in new[] { -5, 0, 3, 42 }) CppOracle.AssertMatchesCpp(src, "classify", n);
        }

        [TestMethod]
        public void WhileLoopWithBreakAndContinue()
        {
            const string src = @"
int f(int n) {
    int total = 0;
    int i = 0;
    while (1) {
        i = i + 1;
        if (i > n) { break; }
        if (i % 3 == 0) { continue; }
        total += i;
    }
    return total;
}";
            CppOracle.AssertMatchesCpp(src, "f", 10);
            CppOracle.AssertMatchesCpp(src, "f", 0);
        }

        [TestMethod]
        public void ForLoop()
        {
            const string src = @"
int f(int n) {
    int total = 0;
    for (int i = 0; i < n; ++i) { total += i * i; }
    return total;
}";
            CppOracle.AssertMatchesCpp(src, "f", 7);
            CppOracle.AssertMatchesCpp(src, "f", 0);
        }

        /// <summary>A do-while runs its body once even when the condition is false at entry.</summary>
        [TestMethod]
        public void DoWhileRunsItsBodyAtLeastOnce()
        {
            const string src = @"
int f(int n) {
    int i = 0;
    int total = 0;
    do { total += i; i++; } while (i < n);
    return total * 10 + i;
}";
            CppOracle.AssertMatchesCpp(src, "f", 0);
            CppOracle.AssertMatchesCpp(src, "f", 4);
        }

        /// <summary>`continue` in a for-loop must still run the increment, or it never terminates.</summary>
        [TestMethod]
        public void ContinueInAForLoopStillRunsTheIncrement()
        {
            const string src = @"
int f(int n) {
    int total = 0;
    for (int i = 0; i < n; i++) {
        if (i % 2 == 0) { continue; }
        total += i;
    }
    return total;
}";
            CppOracle.AssertMatchesCpp(src, "f", 10);
        }

        // ---- doubles ----------------------------------------------------------------------

        [TestMethod]
        public void DoubleArithmetic()
        {
            const string src = "double f(double a, double b) { return (a + b) * (a - b) / 3.0; }";
            CppOracle.AssertMatchesCpp(src, "f", 3.5, 1.25);
            CppOracle.AssertMatchesCpp(src, "f", -0.1, 0.3);
        }

        /// <summary>Every comparison with NaN is false except !=.</summary>
        [TestMethod]
        public void NaNComparesFalseExceptNotEqual()
        {
            double nan = double.NaN;
            const string lt = "int f(double a, double b) { return a < b; }";
            const string eq = "int f(double a, double b) { return a == b; }";
            const string ne = "int f(double a, double b) { return a != b; }";
            const string ge = "int f(double a, double b) { return a >= b; }";
            foreach (string src in new[] { lt, eq, ne, ge })
            {
                CppOracle.AssertMatchesCpp(src, "f", nan, 1.0);
                CppOracle.AssertMatchesCpp(src, "f", 1.0, nan);
                CppOracle.AssertMatchesCpp(src, "f", nan, nan);
            }
        }

        /// <summary>A NaN condition is true, because NaN != 0.</summary>
        [TestMethod]
        public void NaNIsTruthy()
        {
            const string src = "int f(double a) { if (a) { return 1; } return 0; }";
            CppOracle.AssertMatchesCpp(src, "f", double.NaN);
            CppOracle.AssertMatchesCpp(src, "f", 0.0);
            CppOracle.AssertMatchesCpp(src, "f", -0.0);
            CppOracle.AssertMatchesCpp(src, "f", 2.5);
        }

        [TestMethod]
        public void MixedIntAndDoubleArithmeticPromotes()
        {
            const string src = "double f(int a, double b) { return a / 2 + b + a * 1.5; }";
            CppOracle.AssertMatchesCpp(src, "f", 7, 0.25);
            CppOracle.AssertMatchesCpp(src, "f", -7, 0.25);
        }

        /// <summary>A double-to-int cast truncates toward zero, it does not round.</summary>
        [TestMethod]
        public void CastsBetweenIntAndDouble()
        {
            const string src = "int f(double a) { return (int)a; }";
            foreach (double d in new[] { 2.9, -2.9, 0.5, -0.5, 7.0 })
                CppOracle.AssertMatchesCpp(src, "f", d);

            CppOracle.AssertMatchesCpp("double g(int a) { return (double)a / 4.0; }", "g", 7);
        }

        [TestMethod]
        public void NegatingZeroKeepsTheSignBit()
        {
            // -0.0 and 0.0 compare equal but have different bits, and the oracle compares bits.
            CppOracle.AssertMatchesCpp("double f(double a) { return -a; }", "f", 0.0);
            CppOracle.AssertMatchesCpp("double f(double a) { return -a; }", "f", 2.5);
        }

        // ---- calls ------------------------------------------------------------------------

        [TestMethod]
        public void CallsBetweenFunctions()
        {
            const string src = @"
int square(int x) { return x * x; }
int sum_of_squares(int a, int b) { return square(a) + square(b); }";
            CppOracle.AssertMatchesCpp(src, "sum_of_squares", 3, 4);
        }

        [TestMethod]
        public void Recursion()
        {
            const string src = @"
int fib(int n) {
    if (n < 2) { return n; }
    return fib(n - 1) + fib(n - 2);
}";
            CppOracle.AssertMatchesCpp(src, "fib", 20);
        }

        /// <summary>A callee defined later in the file, and mutual recursion through it.</summary>
        [TestMethod]
        public void MutualRecursionAndForwardCalls()
        {
            // The prototype is required by C++ itself (a name must be declared before use), so the
            // oracle needs it too. The translator reads prototypes for their signature and emits
            // nothing for them.
            const string src = @"
int is_odd(int n);
int is_even(int n) { if (n == 0) { return 1; } return is_odd(n - 1); }
int is_odd(int n) { if (n == 0) { return 0; } return is_even(n - 1); }";
            CppOracle.AssertMatchesCpp(src, "is_even", 10);
            CppOracle.AssertMatchesCpp(src, "is_even", 7);
        }

        /// <summary>
        /// More than four arguments, so some travel on the stack — and mixed types, so the
        /// register slot index is shared between the integer and SSE register files.
        /// </summary>
        [TestMethod]
        public void ManyAndMixedArgumentsFollowTheWin64Convention()
        {
            const string src = @"
double mix(int a, double b, int c, double d, int e, double f) {
    return a + b * 2.0 + c + d * 4.0 + e + f * 8.0;
}";
            CppOracle.AssertMatchesCpp(src, "mix", 1, 2.5, 3, 4.25, 5, 6.125);
        }

        /// <summary>
        /// An argument that is itself a call: the staged argument must survive the inner call,
        /// which would clobber it if arguments were staged in volatile registers.
        /// </summary>
        [TestMethod]
        public void ACallInsideAnArgumentList()
        {
            const string src = @"
int add(int a, int b) { return a + b; }
int f(int n) { return add(add(n, 1), add(n, 2)); }";
            CppOracle.AssertMatchesCpp(src, "f", 10);
        }

        /// <summary>
        /// Deeply nested arithmetic: the spill-slot allocator must not hand a nested
        /// sub-expression a slot its parent is still holding a value in.
        /// </summary>
        [TestMethod]
        public void DeeplyNestedExpressions()
        {
            const string src = "int f(int a, int b) { return ((a + b) * ((a - b) + (a * (b + (a - (b * 2)))))) - a; }";
            CppOracle.AssertMatchesCpp(src, "f", 7, 3);
            CppOracle.AssertMatchesCpp(src, "f", -7, 3);
        }
    }
}
