using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class ExtractVariableServiceTests
    {
        [TestMethod]
        public void Extract_SimpleExpression_InsertsAssignmentAndReplaces()
        {
            //          0123456789...
            string src = "def f(a, b):\n    return (a + b) * 2\n";
            int start = src.IndexOf("(a + b)");
            int end = start + "(a + b)".Length;

            var r = ExtractVariableService.Extract(src, start, end, "s");
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(
                "def f(a, b):\n    s = (a + b)\n    return s * 2\n",
                r.NewText);
        }

        [TestMethod]
        public void Extract_TrimsSelectionWhitespace()
        {
            string src = "x = compute(a) + 1\n";
            // Select "compute(a)" with a trailing space included.
            int start = src.IndexOf("compute(a)");
            int end = start + "compute(a) ".Length; // includes the trailing space
            var r = ExtractVariableService.Extract(src, start, end, "c");
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual("c = compute(a)\nx = c + 1\n", r.NewText);
        }

        [TestMethod]
        public void Extract_PreservesIndentation()
        {
            string src = "class C:\n    def m(self):\n        y = self.k * 3\n";
            int start = src.IndexOf("self.k * 3");
            int end = start + "self.k * 3".Length;
            var r = ExtractVariableService.Extract(src, start, end, "v");
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(
                "class C:\n    def m(self):\n        v = self.k * 3\n        y = v\n",
                r.NewText);
        }

        [TestMethod]
        public void ExtractConstant_InsertsAfterImports()
        {
            string src =
                "import os\n" +
                "\n" +
                "def f():\n" +
                "    return 3.14159 * 2\n";
            int start = src.IndexOf("3.14159");
            int end = start + "3.14159".Length;
            var r = ExtractVariableService.ExtractConstant(src, start, end, "PI");
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(
                "import os\n" +
                "\n" +
                "PI = 3.14159\n" +
                "def f():\n" +
                "    return PI * 2\n",
                r.NewText);
        }

        [TestMethod]
        public void ExtractConstant_InsertsAfterDocstring_WhenNoImports()
        {
            string src =
                "\"\"\"Module doc.\"\"\"\n" +
                "def f():\n" +
                "    return 42 + 1\n";
            int start = src.IndexOf("42");
            int end = start + 2;
            var r = ExtractVariableService.ExtractConstant(src, start, end, "ANSWER");
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(
                "\"\"\"Module doc.\"\"\"\n" +
                "ANSWER = 42\n" +
                "def f():\n" +
                "    return ANSWER + 1\n",
                r.NewText);
        }

        [TestMethod]
        public void Extract_EmptySelection_Fails()
        {
            string src = "x = 1\n";
            var r = ExtractVariableService.Extract(src, 2, 2, "v");
            Assert.IsFalse(r.Ok);
        }

        [TestMethod]
        public void Extract_BlankName_Fails()
        {
            string src = "x = a + b\n";
            int start = src.IndexOf("a + b");
            var r = ExtractVariableService.Extract(src, start, start + 5, "   ");
            Assert.IsFalse(r.Ok);
        }
    }
}
