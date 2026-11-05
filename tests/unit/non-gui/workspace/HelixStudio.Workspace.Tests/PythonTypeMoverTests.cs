using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §5.1 (loop 531): AST-level drag-rearrange of classes. PythonTypeMover is
    /// pure text surgery — no disk I/O — so these tests cover extraction, insertion,
    /// and the safety guards (nested-class refusal, duplicate-name refusal) directly.
    /// </summary>
    [TestClass]
    public class PythonTypeMoverTests
    {
        private const string TwoClasses =
            "import os\n" +
            "\n" +
            "class Alpha:\n" +
            "    def greet(self):\n" +
            "        return 'alpha'\n" +
            "\n" +
            "\n" +
            "class Beta:\n" +
            "    def greet(self):\n" +
            "        return 'beta'\n";

        [TestMethod]
        public void ExtractTopLevelClass_RemovesExactlyThatClass_LeavingTheRestIntact()
        {
            var result = PythonTypeMover.ExtractTopLevelClass(TwoClasses, "Alpha");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "class Alpha:");
            StringAssert.Contains(result.ExtractedText, "return 'alpha'");
            Assert.IsFalse(result.RemainingSource.Contains("class Alpha"));
            StringAssert.Contains(result.RemainingSource, "import os");
            StringAssert.Contains(result.RemainingSource, "class Beta:");
            StringAssert.Contains(result.RemainingSource, "return 'beta'");
        }

        [TestMethod]
        public void ExtractTopLevelClass_IncludesDecorators()
        {
            const string source =
                "@final\n" +
                "@register\n" +
                "class Widget:\n" +
                "    pass\n";
            var result = PythonTypeMover.ExtractTopLevelClass(source, "Widget");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "@final");
            StringAssert.Contains(result.ExtractedText, "@register");
            StringAssert.Contains(result.ExtractedText, "class Widget:");
        }

        [TestMethod]
        public void ExtractTopLevelClass_NestedClass_IsRefused()
        {
            const string source =
                "class Outer:\n" +
                "    class Inner:\n" +
                "        pass\n";
            var result = PythonTypeMover.ExtractTopLevelClass(source, "Inner");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "not a top-level class");
        }

        [TestMethod]
        public void ExtractTopLevelClass_UnknownName_Fails()
        {
            var result = PythonTypeMover.ExtractTopLevelClass(TwoClasses, "Gamma");
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void InsertClass_AppendsWithTwoBlankLineSeparation()
        {
            const string target = "import sys\n\ndef helper():\n    pass\n";
            var extract = PythonTypeMover.ExtractTopLevelClass(TwoClasses, "Alpha");
            Assert.IsTrue(extract.Success, extract.ErrorMessage);

            var inserted = PythonTypeMover.InsertClass(target, extract.ExtractedText, "Alpha");
            Assert.IsTrue(inserted.Success, inserted.ErrorMessage);
            StringAssert.Contains(inserted.ExtractedText, "def helper():\n    pass\n\n\nclass Alpha:");
            StringAssert.Contains(inserted.ExtractedText, "return 'alpha'");
        }

        [TestMethod]
        public void InsertClass_DuplicateNameInTarget_IsRefused()
        {
            var extract = PythonTypeMover.ExtractTopLevelClass(TwoClasses, "Alpha");
            Assert.IsTrue(extract.Success, extract.ErrorMessage);

            const string targetAlreadyHasAlpha = "class Alpha:\n    def other(self):\n        pass\n";
            var inserted = PythonTypeMover.InsertClass(targetAlreadyHasAlpha, extract.ExtractedText, "Alpha");
            Assert.IsFalse(inserted.Success);
            StringAssert.Contains(inserted.ErrorMessage, "already defines");
        }

        [TestMethod]
        public void ExtractThenInsert_RoundTrips_ToASingleValidClassDefinition()
        {
            var extract = PythonTypeMover.ExtractTopLevelClass(TwoClasses, "Beta");
            Assert.IsTrue(extract.Success, extract.ErrorMessage);

            var inserted = PythonTypeMover.InsertClass("class Existing:\n    pass\n", extract.ExtractedText, "Beta");
            Assert.IsTrue(inserted.Success, inserted.ErrorMessage);

            // The combined text must itself parse cleanly and expose exactly the
            // two expected top-level classes — proof the move produces valid code,
            // not just plausible-looking text.
            var reExtract = PythonTypeMover.ExtractTopLevelClass(inserted.ExtractedText, "Beta");
            Assert.IsTrue(reExtract.Success, "the moved class must be re-extractable from its new home");
            StringAssert.Contains(reExtract.RemainingSource, "class Existing:");
        }

        [TestMethod]
        public void InsertClass_IntoEmptyFile_ProducesJustTheClass()
        {
            var extract = PythonTypeMover.ExtractTopLevelClass(TwoClasses, "Alpha");
            var inserted = PythonTypeMover.InsertClass("", extract.ExtractedText, "Alpha");
            Assert.IsTrue(inserted.Success, inserted.ErrorMessage);
            StringAssert.Contains(inserted.ExtractedText, "class Alpha:");
            Assert.IsFalse(inserted.ExtractedText.StartsWith("\n"));
        }

        // -------------------------------------------------------------- ExtractTopLevelFunction
        // Phase 5 of the Solution Explorer CRUD-coverage sweep: "Delete" for a top-level function.

        private const string TwoFunctions =
            "import os\n" +
            "\n" +
            "def alpha():\n" +
            "    return 'alpha'\n" +
            "\n" +
            "\n" +
            "def beta():\n" +
            "    return 'beta'\n";

        [TestMethod]
        public void ExtractTopLevelFunction_RemovesExactlyThatFunction_LeavingTheRestIntact()
        {
            var result = PythonTypeMover.ExtractTopLevelFunction(TwoFunctions, "alpha");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "def alpha():");
            StringAssert.Contains(result.ExtractedText, "return 'alpha'");
            Assert.IsFalse(result.RemainingSource.Contains("def alpha"));
            StringAssert.Contains(result.RemainingSource, "import os");
            StringAssert.Contains(result.RemainingSource, "def beta():");
            StringAssert.Contains(result.RemainingSource, "return 'beta'");
        }

        [TestMethod]
        public void ExtractTopLevelFunction_IncludesDecorators()
        {
            const string source =
                "@cache\n" +
                "@log_calls\n" +
                "def widget():\n" +
                "    pass\n";
            var result = PythonTypeMover.ExtractTopLevelFunction(source, "widget");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "@cache");
            StringAssert.Contains(result.ExtractedText, "@log_calls");
            StringAssert.Contains(result.ExtractedText, "def widget():");
        }

        [TestMethod]
        public void ExtractTopLevelFunction_MethodOfSameName_IsNotMatched()
        {
            // A class method named the same as a top-level function is a different node —
            // member delete is a separate operation and must never be reached from here.
            const string source =
                "class Outer:\n" +
                "    def helper(self):\n" +
                "        pass\n";
            var result = PythonTypeMover.ExtractTopLevelFunction(source, "helper");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "not a top-level function");
        }

        [TestMethod]
        public void ExtractTopLevelFunction_UnknownName_Fails()
        {
            var result = PythonTypeMover.ExtractTopLevelFunction(TwoFunctions, "gamma");
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void IsOnlyTopLevelStatement_TrueForASingleFunction_FalseForTwo()
        {
            Assert.IsTrue(PythonTypeMover.IsOnlyTopLevelStatement("def solo():\n    pass\n"));
            Assert.IsFalse(PythonTypeMover.IsOnlyTopLevelStatement(TwoFunctions));
        }

        [TestMethod]
        public void IsOnlyTopLevelStatement_EmptyModule_IsTrue()
        {
            Assert.IsTrue(PythonTypeMover.IsOnlyTopLevelStatement(""));
        }

        // -------------------------------------------------------------- ExtractMethod
        // Phase 6 of the Solution Explorer CRUD-coverage sweep: "Delete" for a method/property/
        // constructor member.

        private const string ClassWithTwoMethods =
            "class Widget:\n" +
            "    def render(self):\n" +
            "        return 'rendered'\n" +
            "\n" +
            "    def reset(self):\n" +
            "        pass\n";

        [TestMethod]
        public void ExtractMethod_RemovesExactlyThatMethod_LeavingTheRestIntact()
        {
            var result = PythonTypeMover.ExtractMethod(ClassWithTwoMethods, "Widget", "render");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "def render(self):");
            StringAssert.Contains(result.ExtractedText, "return 'rendered'");
            Assert.IsFalse(result.RemainingSource.Contains("def render"));
            StringAssert.Contains(result.RemainingSource, "class Widget:");
            StringAssert.Contains(result.RemainingSource, "def reset(self):");
        }

        [TestMethod]
        public void ExtractMethod_IncludesDecorators()
        {
            const string source =
                "class Widget:\n" +
                "    @property\n" +
                "    def value(self):\n" +
                "        return self._v\n";
            var result = PythonTypeMover.ExtractMethod(source, "Widget", "value");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "@property");
            StringAssert.Contains(result.ExtractedText, "def value(self):");
        }

        [TestMethod]
        public void ExtractMethod_ConstructorIsARegularMethod_CanBeExtracted()
        {
            const string source =
                "class Widget:\n" +
                "    def __init__(self):\n" +
                "        self.x = 1\n" +
                "\n" +
                "    def go(self):\n" +
                "        pass\n";
            var result = PythonTypeMover.ExtractMethod(source, "Widget", "__init__");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "def __init__(self):");
            StringAssert.Contains(result.RemainingSource, "def go(self):");
        }

        [TestMethod]
        public void ExtractMethod_UnknownClass_Fails()
        {
            var result = PythonTypeMover.ExtractMethod(ClassWithTwoMethods, "Ghost", "render");
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void ExtractMethod_UnknownMethod_Fails()
        {
            var result = PythonTypeMover.ExtractMethod(ClassWithTwoMethods, "Widget", "ghost");
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.ErrorMessage, "not a method directly on");
        }

        [TestMethod]
        public void ExtractMethod_MethodOfADifferentClass_IsNotMatched()
        {
            const string source =
                "class Alpha:\n" +
                "    def go(self):\n" +
                "        pass\n" +
                "\n" +
                "\n" +
                "class Beta:\n" +
                "    def go(self):\n" +
                "        return 'beta'\n";
            var result = PythonTypeMover.ExtractMethod(source, "Beta", "go");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "return 'beta'");
            StringAssert.Contains(result.RemainingSource, "class Alpha:");
            StringAssert.Contains(result.RemainingSource, "def go(self):\n        pass");
        }

        // -------------------------------------------------------------- TryLocateUniqueFieldAssignment

        [TestMethod]
        public void TryLocateUniqueFieldAssignment_ClassLevelDeclaration_IsRemoved()
        {
            const string source =
                "class Widget:\n" +
                "    count = 0\n" +
                "\n" +
                "    def go(self):\n" +
                "        pass\n";
            var result = PythonTypeMover.TryLocateUniqueFieldAssignment(source, "Widget", "count");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "count = 0");
            Assert.IsFalse(result.RemainingSource.Contains("count"));
            StringAssert.Contains(result.RemainingSource, "def go(self):");
        }

        [TestMethod]
        public void TryLocateUniqueFieldAssignment_SingleSelfAssignment_IsRemoved()
        {
            const string source =
                "class Widget:\n" +
                "    def __init__(self):\n" +
                "        self.name = 'w'\n" +
                "        self.other = 1\n";
            var result = PythonTypeMover.TryLocateUniqueFieldAssignment(source, "Widget", "name");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.ExtractedText, "self.name = 'w'");
            Assert.IsFalse(result.RemainingSource.Contains("self.name"));
            StringAssert.Contains(result.RemainingSource, "self.other = 1");
        }

        [TestMethod]
        public void TryLocateUniqueFieldAssignment_AssignedInTwoMethods_IsRefused_FileUntouched()
        {
            const string source =
                "class Widget:\n" +
                "    def __init__(self):\n" +
                "        self.name = 'w'\n" +
                "\n" +
                "    def rename(self, n):\n" +
                "        self.name = n\n";
            var result = PythonTypeMover.TryLocateUniqueFieldAssignment(source, "Widget", "name");
            Assert.IsFalse(result.Success, "two assignment sites must be refused, not guessed at");
            StringAssert.Contains(result.ErrorMessage, "2 places");
            Assert.IsNull(result.RemainingSource, "a refused delete must not produce a partial edit");
        }

        [TestMethod]
        public void TryLocateUniqueFieldAssignment_NotFound_Fails()
        {
            var result = PythonTypeMover.TryLocateUniqueFieldAssignment(ClassWithTwoMethods, "Widget", "ghost");
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void TryLocateUniqueFieldAssignment_ChainedAssignment_IsNotMatched_EvenIfOnlySite()
        {
            // `a = b = 0` — removing the whole statement to delete `b` would also delete `a`'s
            // assignment, so this must refuse rather than silently take out both.
            const string source =
                "class Widget:\n" +
                "    def __init__(self):\n" +
                "        self.a = self.b = 0\n";
            var result = PythonTypeMover.TryLocateUniqueFieldAssignment(source, "Widget", "b");
            Assert.IsFalse(result.Success, "a chained assignment must never be matched for deletion");
        }
    }
}
