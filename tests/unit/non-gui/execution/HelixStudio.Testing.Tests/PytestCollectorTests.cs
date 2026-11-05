namespace HelixPythonNamespace.Testing.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Testing;

    [TestClass]
    public class PytestCollectorTests
    {
        [TestMethod]
        public void ParseCollectedIds_KeepsOnlyIdLines()
        {
            string output =
                "tests/test_a.py::test_one\n" +
                "tests/test_a.py::TestB::test_two\n" +
                "\n" +
                "3 tests collected in 0.05s\n" +
                "warning: something\n";
            List<string> ids = PytestCollector.ParseCollectedIds(output);
            CollectionAssert.AreEqual(
                new List<string> { "tests/test_a.py::test_one", "tests/test_a.py::TestB::test_two" },
                ids);
        }

        [TestMethod]
        public void ParseCollectedIds_TrimsWhitespace()
        {
            List<string> ids = PytestCollector.ParseCollectedIds("   tests/t.py::test_x   \r\n");
            Assert.AreEqual(1, ids.Count);
            Assert.AreEqual("tests/t.py::test_x", ids[0]);
        }

        [TestMethod]
        public void ParseCollectedIds_EmptyInputYieldsEmpty()
        {
            Assert.AreEqual(0, PytestCollector.ParseCollectedIds("").Count);
            Assert.AreEqual(0, PytestCollector.ParseCollectedIds(null).Count);
        }

        [TestMethod]
        public void Collect_EmptyInterpreterReturnsFalse()
        {
            PytestCollector c = new PytestCollector();
            Assert.IsFalse(c.Collect("", "", new List<string>()));
        }
    }
}
