using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.PythonAsmCompiler.Tests
{
    /// <summary>
    /// Phase 3 — locals and frames.
    ///
    /// The frame is what makes phase 4 possible. Expression temporaries live in numbered spill
    /// slots rather than on the machine stack, so RSP does not move between the prologue and the
    /// epilogue and the ABI's 16-byte alignment at a call site becomes a property of the frame
    /// size instead of the parity of whatever expression happens to surround the call.
    /// </summary>
    [TestClass]
    public class LocalsAndFramesTests
    {
        [TestMethod]
        public void ManyLocalsAllGetTheirOwnSlot()
        {
            const string source =
                "def f(a: int) -> int:\n"
                + "    v1 = a + 1\n"
                + "    v2 = v1 * 2\n"
                + "    v3 = v2 - 3\n"
                + "    v4 = v3 * v1\n"
                + "    v5 = v4 // 2\n"
                + "    v6 = v5 % 7\n"
                + "    v7 = v6 + v3\n"
                + "    v8 = v7 * v2\n"
                + "    v9 = v8 - v4\n"
                + "    v10 = v9 + v5\n"
                + "    v11 = v10 ^ v6\n"
                + "    v12 = v11 & 0xFFFF\n"
                + "    v13 = v12 | v7\n"
                + "    v14 = v13 + v8\n"
                + "    v15 = v14 - v9\n"
                + "    return v15 + v10 + v11 + v12 + v13 + v14\n";
            NasmOracle.AssertMatchesCPython(source, "f", 13);
        }

        [TestMethod]
        public void DeeplyNestedExpressionsDoNotReuseALiveSpillSlot()
        {
            // Depth-indexed spill slots exist for exactly this: a pooled temporary would be handed
            // to an inner sub-expression while its parent still needs the value in it.
            const string source =
                "def f(a: int, b: int, c: int, d: int) -> int:\n"
                + "    return ((a + b) * (c - d)) - ((a - b) * (c + d)) "
                + "+ (((a * b) + (c * d)) // ((a + 1) * (b + 2) + 3))\n";
            NasmOracle.AssertMatchesCPython(source, "f", 7, -3, 11, 5);
            NasmOracle.AssertMatchesCPython(source, "f", -2, 9, -6, 4);
        }

        [TestMethod]
        public void TupleAssignmentSwapsRatherThanOverwrites()
        {
            // The classic proof that the right-hand side is fully evaluated before anything is
            // stored: assigning as you go would leave both names holding the old b.
            const string source =
                "def f(a: int, b: int) -> int:\n"
                + "    a, b = b, a\n"
                + "    return a * 100 + b\n";
            NasmOracle.AssertMatchesCPython(source, "f", 3, 7);
        }

        [TestMethod]
        public void TupleAssignmentOfComputedValues()
        {
            const string source =
                "def f(a: int, b: int) -> int:\n"
                + "    x, y, z = a + b, a - b, a * b\n"
                + "    return x * 10000 + y * 100 + z\n";
            NasmOracle.AssertMatchesCPython(source, "f", 9, 4);
        }

        [TestMethod]
        public void TupleAssignmentInsideALoopIsTheFibonacciIdiom()
        {
            const string source =
                "def fib(n: int) -> int:\n"
                + "    a, b = 0, 1\n"
                + "    for _ in range(n):\n"
                + "        a, b = b, a + b\n"
                + "    return a\n";
            NasmOracle.AssertMatchesCPython(source, "fib", 40);
        }

        [TestMethod]
        public void ChainedAssignmentStoresOneValueIntoEveryTarget()
        {
            const string source =
                "def f(a: int) -> int:\n"
                + "    x = y = z = a * 3\n"
                + "    x = x + 1\n"
                + "    return x * 10000 + y * 100 + z\n";
            NasmOracle.AssertMatchesCPython(source, "f", 5);
        }

        [TestMethod]
        public void EveryAugmentedOperatorUpdatesInPlace()
        {
            const string source =
                "def f(a: int, b: int) -> int:\n"
                + "    x = a\n"
                + "    x += b\n"
                + "    x -= 2\n"
                + "    x *= 3\n"
                + "    x //= 4\n"
                + "    x %= 97\n"
                + "    x <<= 2\n"
                + "    x >>= 1\n"
                + "    x |= 5\n"
                + "    x &= 0xFF\n"
                + "    x ^= 0x0F\n"
                + "    return x\n";
            NasmOracle.AssertMatchesCPython(source, "f", -37, 11);
            NasmOracle.AssertMatchesCPython(source, "f", 250, 9);
        }

        [TestMethod]
        public void LocalsSurviveAcrossNestedLoops()
        {
            const string source =
                "def f(n: int) -> int:\n"
                + "    outer = 0\n"
                + "    for i in range(n):\n"
                + "        inner = 0\n"
                + "        for j in range(i):\n"
                + "            inner += j * i\n"
                + "        outer += inner\n"
                + "    return outer\n";
            NasmOracle.AssertMatchesCPython(source, "f", 12);
        }

        // ---- frame shape -------------------------------------------------

        private static string AssemblyOf(string source)
        {
            TranslationResult result = PythonToNasm.Translate(source);
            Assert.IsTrue(result.Succeeded, result.RefusalReason);
            return result.Assembly;
        }

        [TestMethod]
        public void TheFrameIsAlwaysAMultipleOfSixteenBytes()
        {
            // RSP must be 16-byte aligned at every call; after `push rbp` it already is, so the
            // subtraction must not disturb it.
            string asm = AssemblyOf(
                "def f(a: int, b: int, c: int) -> int:\n"
                + "    x = a * b + c\n"
                + "    y = x - a\n"
                + "    return x + y\n");
            var matches = Regex.Matches(asm, @"sub\s+rsp,\s*(\d+)");
            Assert.AreNotEqual(0, matches.Count, "the function must establish a frame");
            foreach (Match m in matches)
            {
                int frame = int.Parse(m.Groups[1].Value);
                Assert.AreEqual(0, frame % 16, "frame of " + frame + " bytes breaks RSP alignment");
            }
        }

        [TestMethod]
        public void TheBodyNeverMovesTheStackPointer()
        {
            // The one `push` is the prologue's `push rbp`, and the one `pop` its counterpart.
            // Anything else means an expression is moving RSP, and the alignment guarantee at a
            // call site would then depend on how deeply that call happened to be nested.
            string asm = AssemblyOf(
                "def f(a: int, b: int, c: int, d: int) -> int:\n"
                + "    return ((a + b) * (c + d)) // ((a - b) + (c - d) + 17)\n");
            Assert.AreEqual(1, Regex.Matches(asm, @"^\s*push\s", RegexOptions.Multiline).Count);
            Assert.AreEqual(1, Regex.Matches(asm, @"^\s*pop\s", RegexOptions.Multiline).Count);
            StringAssert.Contains(asm, "push    rbp");
            StringAssert.Contains(asm, "pop     rbp");
        }

        [TestMethod]
        public void ACallReservesShadowSpaceEvenForFewArguments()
        {
            // Win64 requires the caller to leave 32 bytes for the callee to home RCX-R9 in, even
            // when it passes fewer than four arguments. Omitting it lets the callee's prologue
            // write over the caller's locals.
            string asm = AssemblyOf(
                "def g(a: int) -> int:\n    return a\n"
                + "def f(a: int) -> int:\n    return g(a)\n");
            Match sub = Regex.Matches(asm, @"sub\s+rsp,\s*(\d+)")
                             .Cast<Match>().Last();
            Assert.IsTrue(int.Parse(sub.Groups[1].Value) >= 32,
                "the calling function reserved only " + sub.Groups[1].Value + " bytes");
        }

        // ---- definite assignment ----------------------------------------

        private static void AssertRefused(string source, string expectedFragment)
        {
            TranslationResult result = PythonToNasm.Translate(source);
            Assert.IsFalse(result.Succeeded, "expected a refusal; got:\n" + result.Assembly);
            StringAssert.Contains(result.RefusalReason, expectedFragment,
                "it said: " + result.RefusalReason);
        }

        [TestMethod]
        public void ReadingALocalBeforeItIsAssignedIsRefused()
        {
            // CPython raises UnboundLocalError here. A stack slot has no such notion: it would
            // quietly return whatever the previous call left behind.
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    b = x + 1\n"
                + "    x = 2\n"
                + "    return b\n",
                "before anything is stored in it");
        }

        [TestMethod]
        public void AssignmentInOnlyOneBranchIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    if a > 0:\n"
                + "        x = 1\n"
                + "    return x\n",
                "'x' may be read before");
        }

        [TestMethod]
        public void AssignmentInBothBranchesIsAccepted()
        {
            const string source =
                "def f(a: int) -> int:\n"
                + "    if a > 0:\n"
                + "        x = 1\n"
                + "    else:\n"
                + "        x = -1\n"
                + "    return x\n";
            NasmOracle.AssertMatchesCPython(source, "f", 4);
            NasmOracle.AssertMatchesCPython(source, "f", -4);
        }

        [TestMethod]
        public void ABranchThatReturnsDoesNotHaveToAssign()
        {
            // The `if` cannot reach the join, so only the fall-through path has to define x.
            const string source =
                "def f(a: int) -> int:\n"
                + "    if a > 0:\n"
                + "        return 0\n"
                + "    x = a * 2\n"
                + "    return x\n";
            NasmOracle.AssertMatchesCPython(source, "f", -6);
            NasmOracle.AssertMatchesCPython(source, "f", 6);
        }

        [TestMethod]
        public void AssignmentOnlyInsideALoopBodyIsRefused()
        {
            // The body may run zero times, so nothing it assigns is guaranteed afterwards.
            AssertRefused(
                "def f(n: int) -> int:\n"
                + "    for i in range(n):\n"
                + "        total = i\n"
                + "    return total\n",
                "'total' may be read before");
        }

        [TestMethod]
        public void TheLoopVariableIsNotBoundAfterTheLoop()
        {
            // Python leaves `i` unbound when the loop runs zero times, so reading it afterwards is
            // an UnboundLocalError there and a refusal here.
            AssertRefused(
                "def f(n: int) -> int:\n"
                + "    for i in range(n):\n"
                + "        pass\n"
                + "    return i\n",
                "'i' may be read before");
        }

        [TestMethod]
        public void AugmentedAssignmentToAnUnassignedLocalIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    total += a\n"
                + "    return total\n",
                "augmented assignment");
        }

        [TestMethod]
        public void ABareAnnotationDeclaresStorageButNotAValue()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    x: int\n"
                + "    return x\n",
                "'x' may be read before");
        }

        [TestMethod]
        public void UnpackingFromANonSequenceIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    x, y = a\n"
                + "    return x\n",
                "tuple or list");
        }

        [TestMethod]
        public void UnpackingTheWrongNumberOfValuesIsRefused()
        {
            AssertRefused(
                "def f(a: int) -> int:\n"
                + "    x, y = a, a, a\n"
                + "    return x\n",
                "cannot unpack 3 values into 2 targets");
        }
    }
}
