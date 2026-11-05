using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// What the compiler declines to translate, and why it says so.
    ///
    /// These matter as much as the execution tests. Python is dynamically typed and
    /// garbage-collected; only a statically-typed integer subset survives the trip to bare
    /// assembly. Emitting plausible assembly for a list, a string or a float would produce a
    /// program that runs and is wrong, which is strictly worse than a refusal the author can read.
    /// </summary>
    [TestClass]
    public class RefusalTests
    {
        private static void AssertRefused(string source, string expectedFragment)
        {
            TranslationResult result = PythonToNasm.Translate(source, "refused.py");
            Assert.IsFalse(result.Succeeded,
                "expected a refusal but the translation succeeded:\n" + result.Assembly);
            StringAssert.Contains(result.RefusalReason, expectedFragment,
                "the refusal must say what to do about it; it said: " + result.RefusalReason);
        }

        [TestMethod]
        public void UnannotatedParameterIsRefused()
        {
            AssertRefused("def f(a) -> int:\n    return a\n", "annotated 'int'");
        }

        [TestMethod]
        public void AnAnnotationOutsideTheSubsetIsRefused()
        {
            // int and float are the two machine types; a str parameter has nowhere to live.
            AssertRefused("def f(a: str) -> int:\n    return 1\n", "annotated 'int' or 'float'");
        }

        [TestMethod]
        public void MissingReturnAnnotationIsRefused()
        {
            AssertRefused("def f(a: int):\n    return a\n", "-> int");
        }

        [TestMethod]
        public void MoreThanFourParametersIsAcceptedOnTheStack()
        {
            // Win64 passes the first four arguments in registers and the rest on the stack; see
            // FunctionCallTests for the executed proof that the offsets are right.
            TranslationResult result = PythonToNasm.Translate(
                "def f(a: int, b: int, c: int, d: int, e: int) -> int:\n    return a + e\n");
            Assert.IsTrue(result.Succeeded, result.RefusalReason);
            StringAssert.Contains(result.Assembly, "[rbp+48]");
        }

        [TestMethod]
        public void DefaultArgumentsAreRefused()
        {
            AssertRefused("def f(a: int = 1) -> int:\n    return a\n", "default argument");
        }

        [TestMethod]
        public void VarArgsAreRefused()
        {
            AssertRefused("def f(*a: int) -> int:\n    return 1\n", "*args");
        }

        [TestMethod]
        public void DecoratorsAreRefused()
        {
            AssertRefused("@cache\ndef f(a: int) -> int:\n    return a\n", "decorators");
        }

        [TestMethod]
        public void ClassesAreRefused()
        {
            AssertRefused("class C:\n    pass\n", "only function definitions");
        }

        [TestMethod]
        public void ModuleLevelStatementsAreRefused()
        {
            AssertRefused("x = 1\n", "only function definitions");
        }

        [TestMethod]
        public void TrueDivisionOfIntegersProducesAFloat()
        {
            // Python's `/` is true division even between two ints, so its result cannot come back
            // from an int function without an explicit int().
            AssertRefused("def f(a: int, b: int) -> int:\n    return a / b\n",
                          "use int(...) to truncate it explicitly");
        }

        [TestMethod]
        public void AFloatLiteralInAnIntFunctionIsRefused()
        {
            AssertRefused("def f() -> int:\n    return 1.5\n",
                          "use int(...) to truncate it explicitly");
        }

        [TestMethod]
        public void StringLiteralsAreRefused()
        {
            AssertRefused("def f() -> int:\n    return 'x'\n", "no heap");
        }

        [TestMethod]
        public void ListsAreRefused()
        {
            AssertRefused("def f() -> int:\n    return [1, 2][0]\n", "not supported");
        }

        [TestMethod]
        public void CallsToSomethingOutsideTheModuleAreRefused()
        {
            // Calls between the module's own functions are supported; anything else has no
            // implementation to jump to. See FunctionCallTests for the supported cases.
            AssertRefused("def f(a: int) -> int:\n    return g(a)\n",
                          "not a function defined in this module");
        }

        [TestMethod]
        public void ChainedComparisonIsRefusedRatherThanApproximated()
        {
            // `a < b < c` evaluates b once and short-circuits; the obvious rewrite to
            // `a < b and b < c` evaluates b twice, so it is refused instead of approximated.
            AssertRefused("def f(a: int, b: int, c: int) -> int:\n    return a < b < c\n",
                          "chained comparisons");
        }

        [TestMethod]
        public void IterationOverSomethingOtherThanRangeIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n    for x in [1, 2]:\n        a += x\n    return a\n",
                "object protocol");
        }

        [TestMethod]
        public void ForElseIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    for i in range(a):\n"
                + "        pass\n"
                + "    else:\n"
                + "        a = 1\n"
                + "    return a\n",
                "for/else");
        }

        [TestMethod]
        public void BreakOutsideALoopIsRefused()
        {
            // CPython itself makes this a SyntaxError, and so does the parser this backend sits on,
            // so the refusal arrives from the front end. The emitter keeps its own guard anyway —
            // it must not emit a jump to a label that does not exist, whatever the caller feeds it.
            AssertRefused("def f(a: int) -> int:\n    if a:\n        break\n    return a\n",
                          "'break' outside loop");
        }

        [TestMethod]
        public void UndefinedNameIsRefused()
        {
            // The definite-assignment pass reaches this first and names the variable, which is
            // more useful than the emitter's "no such slot" fallback.
            AssertRefused("def f(a: int) -> int:\n    return a + z\n",
                          "local 'z' may be read before");
        }

        [TestMethod]
        public void TryExceptIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    try:\n"
                + "        return a\n"
                + "    except ValueError:\n"
                + "        return 0\n",
                "not supported");
        }

        [TestMethod]
        public void NestedFunctionsAreRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    def g(b: int) -> int:\n"
                + "        return b\n"
                + "    return a\n",
                "FunctionDef is not supported");
        }

        [TestMethod]
        public void OversizedIntegerLiteralIsRefusedRatherThanTruncated()
        {
            AssertRefused("def f() -> int:\n    return 99999999999999999999999\n", "64 bits");
        }

        [TestMethod]
        public void SyntaxErrorsAreReportedAsRefusals()
        {
            AssertRefused("def f(a: int) -> int\n    return a\n", "syntax");
        }

        [TestMethod]
        public void AModuleWithNoFunctionsIsRefused()
        {
            AssertRefused("", "empty source");
            AssertRefused("import os\n", "no function definitions");
        }

        [TestMethod]
        public void ADocstringIsNotAnObstacle()
        {
            TranslationResult result = PythonToNasm.Translate(
                "\"\"\"Module docstring.\"\"\"\n"
                + "def f(a: int) -> int:\n"
                + "    \"\"\"Function docstring.\"\"\"\n"
                + "    return a\n");
            Assert.IsTrue(result.Succeeded, result.RefusalReason);
        }

        [TestMethod]
        public void ARefusalCarriesTheOffendingLine()
        {
            TranslationResult result = PythonToNasm.Translate(
                "def f(a: int) -> int:\n"
                + "    b = a + 1\n"
                + "    return b / 2\n");
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(3, result.Line,
                "the reported line is what lets the IDE put the squiggle in the right place");
        }
    }
}
