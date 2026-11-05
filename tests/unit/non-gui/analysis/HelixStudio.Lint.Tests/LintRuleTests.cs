namespace HelixPythonNamespace.Lint.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Lint;

    [TestClass]
    public class LintRuleTests
    {
        private static HashSet<string> Codes(string src)
        {
            PythonLinter linter = new PythonLinter();
            HashSet<string> codes = new HashSet<string>();
            foreach (LintFinding f in linter.Run(src, "t.py"))
            {
                codes.Add(f.Code);
            }
            return codes;
        }

        private static void AssertHas(string src, string code)
        {
            Assert.IsTrue(Codes(src).Contains(code), code + " expected for source:\n" + src);
        }

        private static void AssertNotHas(string src, string code)
        {
            Assert.IsFalse(Codes(src).Contains(code), code + " must NOT fire for source:\n" + src);
        }

        // ---------- C005: f-string without interpolation (iter 26) ----------

        [TestMethod]
        public void C005_PlainFString_NoBraces_Flagged()
        {
            AssertHas("x = f\"just plain text\"\n", "C005");
        }

        [TestMethod]
        public void C005_FormattedConstantField_NotFlagged()
        {
            // `f"{'n=50*K':>8}"` — a replacement field formatting a string constant. The
            // `f` is REQUIRED; dropping it yields the literal text `{'n=50*K':>8}`.
            AssertNotHas("x = f\"{'n=50*K':>8}\"\n", "C005");
        }

        [TestMethod]
        public void C005_EscapedBraces_NotFlagged()
        {
            // `f"g$_{{CM}}$"` — escaped braces; the f-prefix unescapes `{{`->`{`, so it is
            // meaningful (`f"{{x}}"` -> "{x}" vs "{{x}}" without f).
            AssertNotHas("x = f\"latex $_{{CM}}$ label\"\n", "C005");
        }

        [TestMethod]
        public void C005_RealInterpolation_NotFlagged()
        {
            AssertNotHas("name = 'a'\nx = f\"hello {name}\"\n", "C005");
        }

        [TestMethod]
        public void L001_LineTooLong()
        {
            AssertHas("x = '" + new string('a', 120) + "'\n", "L001");
        }

        [TestMethod]
        public void L002_TrailingWhitespace()
        {
            AssertHas("x = 1   \n", "L002");
        }

        [TestMethod]
        public void L003_MixedIndent()
        {
            // Leading spaces then a tab.
            AssertHas("if x:\n  \tpass\n", "L003");
        }

        [TestMethod]
        public void L004_MultipleStatements()
        {
            AssertHas("a = 1; b = 2\n", "L004");
        }

        [TestMethod]
        public void L004_NotTriggeredBySemicolonInString()
        {
            Assert.IsFalse(Codes("x = 'a; b'\n").Contains("L004"));
        }

        [TestMethod]
        public void N001_ClassNotPascalCase()
        {
            AssertHas("class my_class:\n    pass\n", "N001");
        }

        [TestMethod]
        public void N002_FunctionNotSnakeCase()
        {
            AssertHas("def MyFunc():\n    pass\n", "N002");
        }

        [TestMethod]
        public void N003_ShadowsBuiltinTopLevelDef()
        {
            AssertHas("def list():\n    pass\n", "N003");
        }

        [TestMethod]
        public void N003_ShadowsBuiltinAssignment()
        {
            AssertHas("dict = 5\n", "N003");
        }

        [TestMethod]
        public void I001_WildcardImport()
        {
            AssertHas("from os import *\n", "I001");
        }

        [TestMethod]
        public void I002_MultipleImportsOneLine()
        {
            AssertHas("import os, sys\n", "I002");
        }

        [TestMethod]
        public void C001_UnusedImport()
        {
            AssertHas("import os\nx = 1\n", "C001");
        }

        [TestMethod]
        public void C001_UsedImportNotFlagged()
        {
            Assert.IsFalse(Codes("import os\nprint(os.getcwd())\n").Contains("C001"));
        }

        // Regression: a name used ONLY in an assignment (its RHS value or target) must count as used.
        // AssignmentStatement.Children was missing, so the RHS was invisible to the lint walk and an
        // alias like `tk` used only in `root = tk.Tk()` was falsely reported unused.
        [TestMethod]
        public void C001_AliasUsedOnlyInAssignmentValue_NotFlagged()
        {
            Assert.IsFalse(Codes("import tkinter as tk\nroot = tk.Tk()\n").Contains("C001"),
                "aliased import used in an assignment RHS must not be flagged unused");
        }

        [TestMethod]
        public void C001_ImportUsedOnlyInAssignmentValue_NotFlagged()
        {
            Assert.IsFalse(Codes("import os\ncwd = os.getcwd()\n").Contains("C001"),
                "import used only in an assignment RHS must not be flagged unused");
        }

        // Guard against over-suppression: a genuinely unused aliased import is still flagged.
        [TestMethod]
        public void C001_GenuinelyUnusedAlias_StillFlagged()
        {
            AssertHas("import os as o\nx = 1\n", "C001");
        }

        // Regression: a name used ONLY in a PEP 695 type-parameter bound counts as used.
        // TypeParameterSyntax.Bound was missing from Children, so it was invisible to the lint walk.
        [TestMethod]
        public void C001_ImportUsedOnlyInTypeParamBound_NotFlagged()
        {
            Assert.IsFalse(
                Codes("from typing import Sequence\ndef f[T: Sequence]() -> None:\n    pass\n").Contains("C001"),
                "import used only in a PEP 695 type-param bound must not be flagged unused");
        }

        [TestMethod]
        public void C002_MutableDefaultArg()
        {
            AssertHas("def f(x=[]):\n    return x\n", "C002");
        }

        [TestMethod]
        public void C003_CompareToNone()
        {
            AssertHas("if x == None:\n    pass\n", "C003");
        }

        [TestMethod]
        public void C004_CompareToBoolLiteral()
        {
            AssertHas("if x == True:\n    pass\n", "C004");
        }

        [TestMethod]
        public void B001_BareExcept()
        {
            AssertHas("try:\n    pass\nexcept:\n    pass\n", "B001");
        }

        [TestMethod]
        public void B002_AssertTuple()
        {
            AssertHas("assert (x, 'message')\n", "B002");
        }

        [TestMethod]
        public void B003_IsLiteral()
        {
            AssertHas("if x is 5:\n    pass\n", "B003");
        }

        [TestMethod]
        public void B004_BareRaiseOutsideExcept()
        {
            AssertHas("def f():\n    raise\n", "B004");
        }

        [TestMethod]
        public void B004_BareRaiseInsideExceptIsOk()
        {
            Assert.IsFalse(Codes("try:\n    pass\nexcept ValueError:\n    raise\n").Contains("B004"));
        }

        [TestMethod]
        public void CleanCodeProducesNoFindings()
        {
            string clean =
                "import os\n" +
                "\n" +
                "\n" +
                "def compute_total(values):\n" +
                "    total = 0\n" +
                "    for v in values:\n" +
                "        total += v\n" +
                "    return total\n" +
                "\n" +
                "\n" +
                "class OrderBook:\n" +
                "    def add(self, order):\n" +
                "        return os.getpid()\n";
            HashSet<string> codes = Codes(clean);
            Assert.AreEqual(0, codes.Count, "unexpected findings: " + string.Join(",", codes));
        }
    }
}
