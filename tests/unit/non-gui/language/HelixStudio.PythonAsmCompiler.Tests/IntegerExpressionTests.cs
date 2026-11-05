using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// Phase 1 — integer expressions. Every case is assembled, linked, executed, and its answer
    /// compared with CPython's for the same call.
    /// </summary>
    [TestClass]
    public class IntegerExpressionTests
    {
        private static void Check(string body, params long[] args)
        {
            string parameters = args.Length == 0 ? ""
                : args.Length == 1 ? "a: int"
                : args.Length == 2 ? "a: int, b: int"
                : args.Length == 3 ? "a: int, b: int, c: int"
                                   : "a: int, b: int, c: int, d: int";
            string source = "def f(" + parameters + ") -> int:\n    return " + body + "\n";
            NasmOracle.AssertMatchesCPython(source, "f", args);
        }

        [TestMethod] public void Literal() { Check("42"); }
        [TestMethod] public void NegativeLiteral() { Check("-42"); }
        [TestMethod] public void Parameter() { Check("a", 7); }
        [TestMethod] public void FourParameters() { Check("a + b * c - d", 1, 2, 3, 4); }

        [TestMethod] public void Addition() { Check("a + b", 11, 31); }
        [TestMethod] public void Subtraction() { Check("a - b", 11, 31); }
        [TestMethod] public void Multiplication() { Check("a * b", -11, 31); }
        [TestMethod] public void PrecedenceIsThePythonPrecedence() { Check("11 * 3 + 5 - 7"); }
        [TestMethod] public void UnaryMinusAndPlus() { Check("-(+a) - -b", 5, 9); }
        [TestMethod] public void BitwiseInvert() { Check("~a", 5); }

        // Floor division and modulo are where a naive backend is silently wrong: x86 idiv
        // truncates toward zero, Python floors toward negative infinity. All four sign
        // combinations are checked because only the mixed ones differ.
        [TestMethod] public void FloorDivision_BothPositive() { Check("a // b", 7, 2); }
        [TestMethod] public void FloorDivision_NegativeDividend() { Check("a // b", -7, 2); }
        [TestMethod] public void FloorDivision_NegativeDivisor() { Check("a // b", 7, -2); }
        [TestMethod] public void FloorDivision_BothNegative() { Check("a // b", -7, -2); }
        [TestMethod] public void FloorDivision_Exact() { Check("a // b", -8, 2); }

        [TestMethod] public void Modulo_BothPositive() { Check("a % b", 7, 3); }
        [TestMethod] public void Modulo_NegativeDividend() { Check("a % b", -7, 3); }
        [TestMethod] public void Modulo_NegativeDivisor() { Check("a % b", 7, -3); }
        [TestMethod] public void Modulo_BothNegative() { Check("a % b", -7, -3); }
        [TestMethod] public void Modulo_Exact() { Check("a % b", 9, 3); }

        [TestMethod] public void BitwiseAndOrXor() { Check("(a & b) | (a ^ b)", 0x3C, 0x5A); }
        [TestMethod] public void LeftShift() { Check("a << b", 3, 5); }
        // Python's >> is arithmetic: it keeps the sign, unlike an unsigned shift.
        [TestMethod] public void RightShiftKeepsTheSign() { Check("a >> b", -1024, 3); }

        [TestMethod] public void Equality() { Check("a == b", 3, 3); }
        [TestMethod] public void Inequality() { Check("a != b", 3, 4); }
        [TestMethod] public void LessThan() { Check("a < b", -4, 3); }
        [TestMethod] public void LessOrEqual() { Check("a <= b", 3, 3); }
        [TestMethod] public void GreaterThan() { Check("a > b", 3, -4); }
        [TestMethod] public void GreaterOrEqual() { Check("a >= b", 3, 4); }

        [TestMethod] public void LogicalNot() { Check("not a", 0); }
        [TestMethod] public void LogicalNotOfNonZero() { Check("not a", 5); }
        [TestMethod] public void AndShortCircuits() { Check("a > 0 and b > 0", -1, 5); }
        [TestMethod] public void OrShortCircuits() { Check("a > 0 or b > 0", -1, 5); }
        [TestMethod] public void ConditionalExpression() { Check("a if a > b else b", 4, 9); }

        [TestMethod] public void TrueAndFalseAreOneAndZero() { Check("True + False"); }

        [TestMethod]
        public void LocalsAndAugmentedAssignmentSurviveTheFrame()
        {
            const string source =
                "def f(a: int, b: int) -> int:\n"
                + "    total = a\n"
                + "    total += b\n"
                + "    total *= 3\n"
                + "    total -= 1\n"
                + "    total //= 2\n"
                + "    return total\n";
            NasmOracle.AssertMatchesCPython(source, "f", -5, 12);
        }

        [TestMethod]
        public void AnnotatedLocalsAreOrdinaryLocals()
        {
            const string source =
                "def f(a: int) -> int:\n"
                + "    acc: int = 0\n"
                + "    acc = acc + a * a\n"
                + "    return acc\n";
            NasmOracle.AssertMatchesCPython(source, "f", -9);
        }

        [TestMethod]
        public void ABareReturnYieldsZero()
        {
            NasmOracle.AssertMatchesCPython("def f(a: int) -> int:\n    return\n", "f", 1);
        }

        [TestMethod]
        public void FallingOffTheEndYieldsZero()
        {
            NasmOracle.AssertMatchesCPython("def f(a: int) -> int:\n    pass\n", "f", 1);
        }

        [TestMethod]
        public void SeveralFunctionsAreEmittedIntoOneModule()
        {
            const string source =
                "def helper(a: int) -> int:\n"
                + "    return a * 2\n"
                + "\n"
                + "def f(a: int) -> int:\n"
                + "    return a + 1\n";
            TranslationResult result = PythonToNasm.Translate(source);
            Assert.IsTrue(result.Succeeded, result.RefusalReason);
            CollectionAssert.AreEquivalent(new[] { "helper", "f" }, result.Functions.ToArray());
        }

        [TestMethod]
        public void TheEmittedAssemblyRestoresTheCalleeSavedRegister()
        {
            // RBX is non-volatile under Win64. Clobbering it corrupts the CALLER, which is a bug
            // that only shows up in builds that happen to keep a live value there.
            TranslationResult result = PythonToNasm.Translate("def f(a: int, b: int) -> int:\n    return a // b\n");
            Assert.IsTrue(result.Succeeded, result.RefusalReason);
            StringAssert.Contains(result.Assembly, "mov     rbx, qword [rbp-");
        }
    }
}
