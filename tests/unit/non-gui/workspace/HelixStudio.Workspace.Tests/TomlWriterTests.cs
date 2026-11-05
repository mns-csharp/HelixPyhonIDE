using System.Linq;
using System.Globalization;
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toml;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class TomlWriterTests
    {
        private static TomlValue V(TomlDocument d, params string[] path) { return (TomlValue)d.Find(path); }

        [TestMethod]
        public void RoundTrips_Pyproject_StructureAndValues()
        {
            string src =
                "[project]\n" +
                "name = \"demo\"\n" +
                "version = \"0.1.0\"\n" +
                "requires-python = \">=3.12\"\n" +
                "dependencies = [\"numpy>=1.24\", \"requests\"]\n" +
                "\n[tool.helixstudio]\n" +
                "kind = \"app\"\n";
            var d1 = TomlParser.Parse(src);
            string emitted = TomlWriter.Write(d1);
            var d2 = TomlParser.Parse(emitted);

            Assert.IsFalse(d2.HasErrors, emitted + "\n" + string.Join("; ", d2.Diagnostics.Select(x => x.ToString())));
            Assert.AreEqual("demo", V(d2, "project", "name").StringValue);
            Assert.AreEqual("0.1.0", V(d2, "project", "version").StringValue);
            var deps = V(d2, "project", "dependencies");
            Assert.AreEqual(TomlValueKind.Array, deps.Kind);
            CollectionAssert.AreEqual(new[] { "numpy>=1.24", "requests" }, deps.Array.Select(x => x.StringValue).ToArray());
            Assert.AreEqual("app", V(d2, "tool", "helixstudio", "kind").StringValue);
        }

        [TestMethod]
        public void RoundTrips_TypedScalars()
        {
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string src = "i = 42\nf = 3.5\nb = true\nd = " + date + "\n";
            var d2 = TomlParser.Parse(TomlWriter.Write(TomlParser.Parse(src)));
            Assert.IsFalse(d2.HasErrors);
            Assert.AreEqual(42L, V(d2, "i").IntValue);
            Assert.AreEqual(3.5, V(d2, "f").FloatValue, 1e-9);
            Assert.IsTrue(V(d2, "b").BoolValue);
            Assert.AreEqual(TomlValueKind.LocalDate, V(d2, "d").Kind);
        }

        [TestMethod]
        public void RoundTrips_ArrayOfTables()
        {
            string src = "[[bin]]\nname = \"a\"\n[[bin]]\nname = \"b\"\n";
            var d2 = TomlParser.Parse(TomlWriter.Write(TomlParser.Parse(src)));
            Assert.IsFalse(d2.HasErrors);
            var arr = d2.Find("bin") as TomlTableArray;
            Assert.IsNotNull(arr);
            Assert.AreEqual(2, arr.Elements.Count);
            Assert.AreEqual("b", ((TomlValue)arr.Elements[1].Get("name")).StringValue);
        }

        [TestMethod]
        public void RoundTrips_InlineTable()
        {
            var d2 = TomlParser.Parse(TomlWriter.Write(TomlParser.Parse("point = { x = 1, y = 2 }\n")));
            Assert.IsFalse(d2.HasErrors);
            var pt = V(d2, "point");
            Assert.AreEqual(TomlValueKind.InlineTable, pt.Kind);
            Assert.AreEqual(2L, ((TomlValue)pt.Inline.Get("y")).IntValue);
        }

        [TestMethod]
        public void Escapes_SpecialCharactersInStrings()
        {
            var d1 = TomlParser.Parse("s = \"a\\\"b\\nc\"\n");
            var d2 = TomlParser.Parse(TomlWriter.Write(d1));
            Assert.IsFalse(d2.HasErrors, TomlWriter.Write(d1));
            Assert.AreEqual(V(d1, "s").StringValue, V(d2, "s").StringValue);
        }

        [TestMethod]
        public void PreservesKeyOrder()
        {
            string src = "[project]\nz = 1\na = 2\nm = 3\n";
            string emitted = TomlWriter.Write(TomlParser.Parse(src));
            int zi = emitted.IndexOf("z = 1");
            int ai = emitted.IndexOf("a = 2");
            int mi = emitted.IndexOf("m = 3");
            Assert.IsTrue(zi >= 0 && zi < ai && ai < mi, "keys emitted in original order: " + emitted);
        }
    }
}
