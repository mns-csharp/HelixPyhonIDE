using System.Linq;
using System.Globalization;
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toml;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class TomlParserTests
    {
        [TestMethod]
        public void Parses_RealPyproject_TablesAndScalars()
        {
            string src =
                "[project]\n" +
                "name = \"demo\"\n" +
                "version = \"0.1.0\"\n" +
                "requires-python = \">=3.12\"\n" +
                "dependencies = [\"numpy>=1.24\", \"requests\"]\n" +
                "\n" +
                "[tool.helixstudio]\n" +
                "kind = \"app\"\n";
            var doc = TomlParser.Parse(src);
            Assert.IsFalse(doc.HasErrors, string.Join("; ", doc.Diagnostics.Select(x => x.ToString())));
            Assert.AreEqual("demo", ((TomlValue)doc.Find("project", "name")).StringValue);
            var deps = (TomlValue)doc.Find("project", "dependencies");
            Assert.AreEqual(TomlValueKind.Array, deps.Kind);
            Assert.AreEqual(2, deps.Array.Count);
            Assert.AreEqual("numpy>=1.24", deps.Array[0].StringValue);
            // nested dotted table [tool.helixstudio]
            Assert.AreEqual("app", ((TomlValue)doc.Find("tool", "helixstudio", "kind")).StringValue);
        }

        [TestMethod]
        public void Parses_TypedScalars()
        {
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            string src =
                "i = 42\n" +
                "hex = 0xFF\n" +
                "neg = -17\n" +
                "f = 3.14\n" +
                "sci = 1e3\n" +
                "b = true\n" +
                "d = " + date + "\n" +
                "dt = " + timestamp + "\n";
            var doc = TomlParser.Parse(src);
            Assert.IsFalse(doc.HasErrors, string.Join("; ", doc.Diagnostics.Select(x => x.ToString())));
            Assert.AreEqual(42L, ((TomlValue)doc.Find("i")).IntValue);
            Assert.AreEqual(255L, ((TomlValue)doc.Find("hex")).IntValue);
            Assert.AreEqual(-17L, ((TomlValue)doc.Find("neg")).IntValue);
            Assert.AreEqual(3.14, ((TomlValue)doc.Find("f")).FloatValue, 1e-9);
            Assert.AreEqual(TomlValueKind.Float, ((TomlValue)doc.Find("sci")).Kind);
            Assert.IsTrue(((TomlValue)doc.Find("b")).BoolValue);
            Assert.AreEqual(TomlValueKind.LocalDate, ((TomlValue)doc.Find("d")).Kind);
            Assert.AreEqual(TomlValueKind.OffsetDateTime, ((TomlValue)doc.Find("dt")).Kind);
        }

        [TestMethod]
        public void Parses_ArrayOfTables()
        {
            string src =
                "[[bin]]\nname = \"a\"\n" +
                "[[bin]]\nname = \"b\"\n";
            var doc = TomlParser.Parse(src);
            Assert.IsFalse(doc.HasErrors, string.Join("; ", doc.Diagnostics.Select(x => x.ToString())));
            var arr = doc.Find("bin") as TomlTableArray;
            Assert.IsNotNull(arr);
            Assert.AreEqual(2, arr.Elements.Count);
            Assert.AreEqual("a", ((TomlValue)arr.Elements[0].Get("name")).StringValue);
            Assert.AreEqual("b", ((TomlValue)arr.Elements[1].Get("name")).StringValue);
        }

        [TestMethod]
        public void Parses_InlineTable_AndDottedKeys()
        {
            string src =
                "point = { x = 1, y = 2 }\n" +
                "a.b.c = 3\n";
            var doc = TomlParser.Parse(src);
            Assert.IsFalse(doc.HasErrors, string.Join("; ", doc.Diagnostics.Select(x => x.ToString())));
            var pt = (TomlValue)doc.Find("point");
            Assert.AreEqual(TomlValueKind.InlineTable, pt.Kind);
            Assert.AreEqual(1L, ((TomlValue)pt.Inline.Get("x")).IntValue);
            Assert.AreEqual(3L, ((TomlValue)doc.Find("a", "b", "c")).IntValue);
        }

        [TestMethod]
        public void Detects_DuplicateKey()
        {
            var doc = TomlParser.Parse("[t]\nx = 1\nx = 2\n");
            Assert.IsTrue(doc.Diagnostics.Any(d => d.Code == "TML001"));
        }

        [TestMethod]
        public void Detects_RedefinedTable()
        {
            var doc = TomlParser.Parse("[a]\nx = 1\n[a]\ny = 2\n");
            Assert.IsTrue(doc.Diagnostics.Any(d => d.Code == "TML002"));
        }

        [TestMethod]
        public void Parses_MultilineAndLiteralStrings()
        {
            string src = "s = \"\"\"line1\nline2\"\"\"\nlit = 'C:\\path\\no\\escape'\n";
            var doc = TomlParser.Parse(src);
            Assert.IsFalse(doc.HasErrors, string.Join("; ", doc.Diagnostics.Select(x => x.ToString())));
            StringAssert.Contains(((TomlValue)doc.Find("s")).StringValue, "line2");
            Assert.AreEqual("C:\\path\\no\\escape", ((TomlValue)doc.Find("lit")).StringValue);
        }

        [TestMethod]
        public void CleanDocument_NoDiagnostics()
        {
            var doc = TomlParser.Parse("# comment\ntitle = \"ok\"\n[section]\nvalue = 1\n");
            Assert.AreEqual(0, doc.Diagnostics.Count);
        }
    }
}
