namespace HelixPythonNamespace.Metrics.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Metrics;

    [TestClass]
    public class MetricsEngineTests
    {
        private static MetricsReport Analyze(string text)
        {
            MetricsEngine engine = new MetricsEngine();
            return engine.Analyze(new List<MetricsSource> { new MetricsSource("test.py", text) });
        }

        private static FunctionMetric Func(MetricsReport r, string name)
        {
            foreach (FunctionMetric f in r.Functions)
            {
                if (f.Name == name) return f;
            }
            Assert.Fail("function not found: " + name);
            return null;
        }

        [TestMethod]
        public void LineCountsClassifyBlankCommentCode()
        {
            string src = "x = 1\n\n# a comment\ny = 2\n";
            MetricsReport r = Analyze(src);
            FileMetric fm = r.Files[0];
            Assert.AreEqual(5, fm.TotalLines); // trailing newline yields a final empty line
            Assert.AreEqual(2, fm.BlankLines);
            Assert.AreEqual(1, fm.CommentLines);
            Assert.AreEqual(2, fm.CodeLines);
        }

        [TestMethod]
        public void TopLevelFunctionAndClassCounted()
        {
            string src = "def f():\n    pass\n\nclass C:\n    def m(self):\n        pass\n";
            MetricsReport r = Analyze(src);
            // Two function records overall: top-level f and method C.m.
            Assert.AreEqual(2, r.Files[0].FunctionCount);
            Assert.IsNotNull(Func(r, "f"));
            Assert.IsNotNull(Func(r, "C.m"));
            bool hasC = false;
            foreach (ClassMetric c in r.Classes) if (c.Name == "C") { hasC = true; Assert.AreEqual(1, c.MethodCount); }
            Assert.IsTrue(hasC);
        }

        [TestMethod]
        public void ParameterCountMatches()
        {
            MetricsReport r = Analyze("def f(a, b, c):\n    return a\n");
            Assert.AreEqual(3, Func(r, "f").ParameterCount);
        }

        [TestMethod]
        public void CyclomaticComplexityCountsBranchesAndBoolOps()
        {
            // base 1 + if(1) + elif(1) + for(1) + while(1) + and(1) + or(1) = 7
            string src =
                "def f(x):\n" +
                "    if x and x > 0:\n" +
                "        pass\n" +
                "    elif x or x < 0:\n" +
                "        pass\n" +
                "    for i in range(3):\n" +
                "        while i:\n" +
                "            i = i - 1\n";
            FunctionMetric f = Func(Analyze(src), "f");
            Assert.AreEqual(7, f.CyclomaticComplexity);
        }

        [TestMethod]
        public void MaxNestingDepthMeasured()
        {
            string src =
                "def f():\n" +
                "    if True:\n" +
                "        for i in range(2):\n" +
                "            while i:\n" +
                "                i = 0\n";
            FunctionMetric f = Func(Analyze(src), "f");
            Assert.AreEqual(3, f.MaxNestingDepth);
        }

        [TestMethod]
        public void NestedFunctionGetsOwnQualifiedRecord()
        {
            string src =
                "def outer():\n" +
                "    def inner():\n" +
                "        pass\n" +
                "    return inner\n";
            MetricsReport r = Analyze(src);
            Assert.IsNotNull(Func(r, "outer"));
            Assert.IsNotNull(Func(r, "outer.inner"));
        }

        [TestMethod]
        public void StatementCountExcludesNestedDefs()
        {
            string src =
                "def f():\n" +
                "    a = 1\n" +
                "    b = 2\n" +
                "    if a:\n" +
                "        c = 3\n";
            // a, b, if, c  => 4 statements
            FunctionMetric f = Func(Analyze(src), "f");
            Assert.AreEqual(4, f.StatementCount);
        }
    }
}
