using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.CppAsmCompiler.Tests
{
    /// <summary>
    /// C++ -> NASM -> C++. Each case asserts the recovered program computes what the original
    /// computed, and that a second trip through the pipeline changes nothing further.
    /// </summary>
    [TestClass]
    public class RoundTripTests
    {
        [TestMethod]
        public void Arithmetic()
        {
            CppOracle.AssertRoundTripsCpp("int f(int a, int b) { return a * b + a - b; }", "f", 6, 7);
        }

        [TestMethod]
        public void DivisionAndModulo()
        {
            CppOracle.AssertRoundTripsCpp("int f(int a, int b) { return a / b + a % b; }", "f", -7, 2);
        }

        [TestMethod]
        public void Locals()
        {
            const string src = "int f(int a) { int b = a * 2; int c = b + 1; return b + c; }";
            CppOracle.AssertRoundTripsCpp(src, "f", 5);
        }

        [TestMethod]
        public void IfElse()
        {
            const string src = @"
int f(int a, int b) {
    if (a > b) { return a - b; }
    else { return b - a; }
}";
            CppOracle.AssertRoundTripsCpp(src, "f", 3, 9);
            CppOracle.AssertRoundTripsCpp(src, "f", 9, 3);
        }

        [TestMethod]
        public void IfWithoutElse()
        {
            const string src = @"
int f(int a) {
    int r = 0;
    if (a > 0) { r = a; }
    return r;
}";
            CppOracle.AssertRoundTripsCpp(src, "f", 4);
            CppOracle.AssertRoundTripsCpp(src, "f", -4);
        }

        [TestMethod]
        public void WhileLoop()
        {
            const string src = @"
int f(int n) {
    int total = 0;
    int i = 0;
    while (i < n) { total = total + i; i = i + 1; }
    return total;
}";
            CppOracle.AssertRoundTripsCpp(src, "f", 6);
        }

        [TestMethod]
        public void ForLoopRecoversAsAWhile()
        {
            const string src = @"
int f(int n) {
    int total = 0;
    for (int i = 0; i < n; i = i + 1) { total = total + i * i; }
    return total;
}";
            CppOracle.AssertRoundTripsCpp(src, "f", 5);
        }

        [TestMethod]
        public void DoWhileLoop()
        {
            const string src = @"
int f(int n) {
    int i = 0;
    int total = 0;
    do { total = total + i; i = i + 1; } while (i < n);
    return total;
}";
            CppOracle.AssertRoundTripsCpp(src, "f", 0);
            CppOracle.AssertRoundTripsCpp(src, "f", 5);
        }

        [TestMethod]
        public void BreakAndContinue()
        {
            const string src = @"
int f(int n) {
    int total = 0;
    int i = 0;
    while (i < n) {
        i = i + 1;
        if (i > 5) { break; }
        if (i == 2) { continue; }
        total = total + i;
    }
    return total;
}";
            CppOracle.AssertRoundTripsCpp(src, "f", 9);
        }

        [TestMethod]
        public void CompoundConditions()
        {
            const string src = @"
int f(int a, int b) {
    if (a > 0 && b > 0) { return 1; }
    return 0;
}";
            CppOracle.AssertRoundTripsCpp(src, "f", 1, 1);
            CppOracle.AssertRoundTripsCpp(src, "f", 1, -1);
        }

        [TestMethod]
        public void Doubles()
        {
            const string src = "double f(double a, double b) { return (a + b) * a / 2.0; }";
            CppOracle.AssertRoundTripsCpp(src, "f", 1.5, 2.25);
        }

        [TestMethod]
        public void Comparisons()
        {
            CppOracle.AssertRoundTripsCpp("int f(int a, int b) { return a < b; }", "f", 3, 5);
            CppOracle.AssertRoundTripsCpp("int f(double a, double b) { return a >= b; }", "f", 3.0, 5.0);
        }

        [TestMethod]
        public void CallsAndRecursion()
        {
            const string src = @"
int fib(int n) {
    if (n < 2) { return n; }
    return fib(n - 1) + fib(n - 2);
}";
            CppOracle.AssertRoundTripsCpp(src, "fib", 12);
        }

        [TestMethod]
        public void CallsBetweenTwoFunctions()
        {
            const string src = @"
int square(int x) { return x * x; }
int hypotenuse_squared(int a, int b) { return square(a) + square(b); }";
            CppOracle.AssertRoundTripsCpp(src, "hypotenuse_squared", 3, 4);
        }

        [TestMethod]
        public void CastsSurvive()
        {
            CppOracle.AssertRoundTripsCpp("int f(double a) { return (int)a + 1; }", "f", 2.75);
        }
    }
}
