namespace HelixPythonNamespace.Testing.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Testing;

    [TestClass]
    public class TestOutputParserTests
    {
        private static TestNode Find(TestOutputParser p, string id)
        {
            foreach (TestNode n in p.Results) if (n.Id == id) return n;
            return null;
        }

        // ---- pytest result lines ----

        [TestMethod]
        public void Pytest_ParsesPassFailErrorSkip()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            p.HandleLine("tests/test_a.py::test_ok PASSED                  [ 25%]");
            p.HandleLine("tests/test_a.py::test_bad FAILED                 [ 50%]");
            p.HandleLine("tests/test_a.py::test_err ERROR                  [ 75%]");
            p.HandleLine("tests/test_a.py::test_skip SKIPPED               [100%]");

            Assert.AreEqual(TestStatus.Passed, Find(p, "tests/test_a.py::test_ok").Status);
            Assert.AreEqual(TestStatus.Failed, Find(p, "tests/test_a.py::test_bad").Status);
            Assert.AreEqual(TestStatus.Error, Find(p, "tests/test_a.py::test_err").Status);
            Assert.AreEqual(TestStatus.Skipped, Find(p, "tests/test_a.py::test_skip").Status);
        }

        [TestMethod]
        public void Pytest_RealVerboseOutput_CountsMatchInterpreter()
        {
            // The EXACT `pytest -v --no-header -rN` lines produced by a real run (Python 3.11
            // / pytest) on a 2-pass/1-fail/1-skip suite — note SKIPPED carries a (reason) that
            // must not break status parsing. Verifies the runner reports the same counts the
            // interpreter does (task-D parity for the test runner).
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            p.HandleLine("test_sample.py::test_pass_one PASSED                                     [ 25%]");
            p.HandleLine("test_sample.py::test_pass_two PASSED                                     [ 50%]");
            p.HandleLine("test_sample.py::test_fail_math FAILED                                    [ 75%]");
            p.HandleLine("test_sample.py::test_skipped SKIPPED (demonstrate skip)                  [100%]");

            int passed = 0, failed = 0, skipped = 0, other = 0;
            foreach (TestNode n in p.Results)
            {
                if (n.Status == TestStatus.Passed) passed++;
                else if (n.Status == TestStatus.Failed) failed++;
                else if (n.Status == TestStatus.Skipped) skipped++;
                else other++;
            }
            Assert.AreEqual(2, passed, "passed");
            Assert.AreEqual(1, failed, "failed");
            Assert.AreEqual(1, skipped, "skipped");
            Assert.AreEqual(0, other, "no misclassified");
            // SKIPPED-with-reason must still be a skip, not misparsed.
            Assert.AreEqual(TestStatus.Skipped, Find(p, "test_sample.py::test_skipped").Status);
        }

        [TestMethod]
        public void Pytest_XfailAndXpassCountAsPassed()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            p.HandleLine("tests/t.py::test_xf XFAIL [ 50%]");
            p.HandleLine("tests/t.py::test_xp XPASS [100%]");
            Assert.AreEqual(TestStatus.Passed, Find(p, "tests/t.py::test_xf").Status);
            Assert.AreEqual(TestStatus.Passed, Find(p, "tests/t.py::test_xp").Status);
        }

        [TestMethod]
        public void Pytest_ResolvesFilePathFromIdPrefix()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, @"C:\proj");
            p.HandleLine("tests/test_a.py::test_ok PASSED [100%]");
            TestNode n = Find(p, "tests/test_a.py::test_ok");
            Assert.IsTrue(n.FilePath.Replace('/', '\\').EndsWith(@"proj\tests\test_a.py"));
        }

        // ---- definition-line resolution (the Tests panel's Line column) ----

        private static string WriteTempTestFile()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "hx_defline_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "test_geo.py");
            System.IO.File.WriteAllLines(file, new[]
            {
                "import math",                       // 1
                "",                                  // 2
                "def test_free_function():",         // 3
                "    assert True",                   // 4
                "",                                  // 5
                "class TestVec:",                    // 6
                "    def test_add(self):",           // 7
                "        assert 1 + 1 == 2",         // 8
                "",                                  // 9
                "    async def test_async_op(self):",// 10
                "        assert True",               // 11
                "",                                  // 12
                "class TestOther:",                  // 13
                "    def test_add(self):",           // 14  same method name, other class
                "        assert 2 + 2 == 4",         // 15
            });
            return file;
        }

        [TestMethod]
        public void ResolveDefinitionLine_FindsFunctionMethodAsyncAndParametrized()
        {
            string file = WriteTempTestFile();
            try
            {
                var cache = new Dictionary<string, string[]>();
                Assert.AreEqual(3, TestOutputParser.ResolveDefinitionLine(file, "test_free_function", cache));
                Assert.AreEqual(7, TestOutputParser.ResolveDefinitionLine(file, "TestVec::test_add", cache));
                Assert.AreEqual(10, TestOutputParser.ResolveDefinitionLine(file, "TestVec::test_async_op", cache));
                // The class segment scopes the search — the same method name in a LATER class
                // must not resolve to the first one.
                Assert.AreEqual(14, TestOutputParser.ResolveDefinitionLine(file, "TestOther::test_add", cache));
                // Parametrized ids resolve to the underlying function.
                Assert.AreEqual(7, TestOutputParser.ResolveDefinitionLine(file, "TestVec::test_add[3-5]", cache));
                // Unknown names / files yield 0, never a bogus location.
                Assert.AreEqual(0, TestOutputParser.ResolveDefinitionLine(file, "TestVec::no_such", cache));
                Assert.AreEqual(0, TestOutputParser.ResolveDefinitionLine(file + ".missing", "test_x", cache));
            }
            finally
            {
                try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(file), true); } catch { }
            }
        }

        [TestMethod]
        public void Pytest_PassedResult_GetsTheDefinitionLine_NotJustFailures()
        {
            // Regression: the Line column used to be populated only for failures (from the
            // traceback), so every passed row navigated to line 0 and showed no line.
            string file = WriteTempTestFile();
            string workDir = System.IO.Path.GetDirectoryName(file);
            try
            {
                TestOutputParser p = new TestOutputParser(TestFramework.Pytest, workDir);
                p.HandleLine("test_geo.py::TestVec::test_add PASSED [100%]");
                TestNode n = Find(p, "test_geo.py::TestVec::test_add");
                Assert.AreEqual(7, n.Line);
            }
            finally
            {
                try { System.IO.Directory.Delete(workDir, true); } catch { }
            }
        }

        [TestMethod]
        public void Pytest_ProgressReportedAndDeduped()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            List<int> percents = new List<int>();
            p.ProgressReported += (pct, completed, total) => percents.Add(pct);
            p.HandleLine("t.py::a PASSED [ 50%]");
            p.HandleLine("t.py::b PASSED [ 50%]");  // duplicate percent -> no new event
            p.HandleLine("t.py::c PASSED [100%]");
            CollectionAssert.AreEqual(new List<int> { 50, 100 }, percents);
            Assert.AreEqual(100, p.LastEmittedPercent);
        }

        [TestMethod]
        public void Pytest_ProgressTotalEstimateConverges()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            int seenTotal = -1;
            p.ProgressReported += (pct, completed, total) => seenTotal = total;
            p.HandleLine("t.py::a PASSED [ 50%]"); // 1 completed at 50% -> total est 2
            Assert.AreEqual(2, seenTotal);
        }

        [TestMethod]
        public void Pytest_FailureHeaderAssignsLineToFailedNode()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            p.HandleLine("tests/test_sample.py::TestRectangle::test_dimensions_stored FAILED [100%]");
            p.HandleLine("____ TestRectangle.test_dimensions_stored ____");
            p.HandleLine("tests/test_sample.py:56: AssertionError");
            TestNode n = Find(p, "tests/test_sample.py::TestRectangle::test_dimensions_stored");
            Assert.AreEqual(56, n.Line);
        }

        [TestMethod]
        public void Pytest_NodeBatchedFiresPerResult()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            int count = 0;
            p.NodeBatched += n => count++;
            p.HandleLine("t.py::a PASSED [ 50%]");
            p.HandleLine("t.py::b FAILED [100%]");
            Assert.AreEqual(2, count);
        }

        // ---- unittest result lines ----

        [TestMethod]
        public void Unittest_ParsesStatuses()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Unittest, "");
            p.HandleLine("test_ok (pkg.mod.Class) ... ok");
            p.HandleLine("test_bad (pkg.mod.Class) ... FAIL");
            p.HandleLine("test_err (pkg.mod.Class) ... ERROR");
            p.HandleLine("test_sk (pkg.mod.Class) ... skipped 'reason'");
            Assert.AreEqual(TestStatus.Passed, Find(p, "pkg.mod.Class.test_ok").Status);
            Assert.AreEqual(TestStatus.Failed, Find(p, "pkg.mod.Class.test_bad").Status);
            Assert.AreEqual(TestStatus.Error, Find(p, "pkg.mod.Class.test_err").Status);
            Assert.AreEqual(TestStatus.Skipped, Find(p, "pkg.mod.Class.test_sk").Status);
        }

        [TestMethod]
        public void Unittest_IgnoresUnrelatedLines()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Unittest, "");
            p.HandleLine("Ran 4 tests in 0.003s");
            p.HandleLine("OK");
            Assert.AreEqual(0, p.Results.Count);
        }

        // ---- FailedIds ----

        [TestMethod]
        public void FailedIds_ReturnsFailedAndErrorOnly()
        {
            TestOutputParser p = new TestOutputParser(TestFramework.Pytest, "");
            p.HandleLine("t.py::a PASSED [ 33%]");
            p.HandleLine("t.py::b FAILED [ 66%]");
            p.HandleLine("t.py::c ERROR [100%]");
            List<string> failed = p.FailedIds();
            CollectionAssert.AreEquivalent(new List<string> { "t.py::b", "t.py::c" }, failed);
        }

        // ---- helpers ----

        [TestMethod]
        public void StripParametrize_RemovesBracketSuffix()
        {
            Assert.AreEqual("test_area", TestOutputParser.StripParametrize("test_area[2.0-3.0-6.0]"));
            Assert.AreEqual("test_x", TestOutputParser.StripParametrize("test_x"));
        }

        [TestMethod]
        public void IdMatchesHeader_NormalisesSeparators()
        {
            Assert.IsTrue(TestOutputParser.IdMatchesHeader(
                "tests/test_sample.py::TestRectangle::test_dimensions_stored",
                "TestRectangle.test_dimensions_stored"));
            Assert.IsTrue(TestOutputParser.IdMatchesHeader(
                "tests/test_sample.py::test_pi_constant",
                "test_pi_constant"));
            Assert.IsFalse(TestOutputParser.IdMatchesHeader(
                "tests/test_sample.py::TestRectangle::test_dimensions_stored",
                "TestRectangle.test_other"));
        }

        [TestMethod]
        public void IdMatchesHeader_IgnoresParametrize()
        {
            Assert.IsTrue(TestOutputParser.IdMatchesHeader(
                "tests/t.py::TestCalc::test_area[2-3]",
                "TestCalc.test_area"));
        }

        [TestMethod]
        public void IdMatchesHeader_NoSeparatorIsFalse()
        {
            Assert.IsFalse(TestOutputParser.IdMatchesHeader("plain_id", "plain_id"));
        }
    }
}
