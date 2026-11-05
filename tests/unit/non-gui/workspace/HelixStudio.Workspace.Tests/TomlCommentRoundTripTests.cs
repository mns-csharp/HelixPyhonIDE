using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toml;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class TomlCommentRoundTripTests
    {
        [TestMethod]
        public void Parser_CapturesLeadingAndTrailingComments()
        {
            const string src =
                "# top of file\n" +
                "# author note\n" +
                "name = \"demo\"  # the project name\n" +
                "version = \"1.0\"\n";
            var doc = TomlParser.Parse(src);
            var name = doc.Root.Get("name");
            Assert.IsNotNull(name.LeadingComments);
            CollectionAssert.AreEqual(new[] { "# top of file", "# author note" }, name.LeadingComments.ToArray());
            Assert.AreEqual("# the project name", name.TrailingComment);
            Assert.IsNull(doc.Root.Get("version").LeadingComments);
        }

        [TestMethod]
        public void Parser_CapturesCommentAboveTableHeader()
        {
            const string src =
                "# a section\n" +
                "[server]\n" +
                "host = \"localhost\"\n";
            var doc = TomlParser.Parse(src);
            var server = doc.Root.Get("server");
            Assert.IsNotNull(server.LeadingComments);
            CollectionAssert.AreEqual(new[] { "# a section" }, server.LeadingComments.ToArray());
        }

        [TestMethod]
        public void Writer_EmitsLeadingAndTrailingComments()
        {
            const string src =
                "# hello\n" +
                "name = \"demo\"  # trailing\n";
            var doc = TomlParser.Parse(src);
            string outp = TomlWriter.Write(doc);
            StringAssert.Contains(outp, "# hello");
            StringAssert.Contains(outp, "name = \"demo\"  # trailing");
        }

        [TestMethod]
        public void FullRoundTrip_PreservesCommentsAndStructure()
        {
            const string src =
                "# project metadata\n" +
                "name = \"geometry\"\n" +
                "version = \"0.1.0\"\n" +
                "\n" +
                "# dependency section\n" +
                "[deps]\n" +
                "json = \"1.2.3\"  # json lib\n" +
                "toml = \"0.5.0\"\n";
            var doc = TomlParser.Parse(src);
            string first = TomlWriter.Write(doc);

            // Comments survive.
            StringAssert.Contains(first, "# project metadata");
            StringAssert.Contains(first, "# dependency section");
            StringAssert.Contains(first, "# json lib");

            // Idempotent: writing the re-parsed doc yields the same text (stable round-trip).
            var doc2 = TomlParser.Parse(first);
            string second = TomlWriter.Write(doc2);
            Assert.AreEqual(first, second, "write∘parse is idempotent (comments + structure stable)");

            // Structure/values still intact.
            Assert.IsFalse(doc2.HasErrors);
            Assert.AreEqual("geometry", ((TomlValue)doc2.Root.Get("name")).StringValue);
            var deps = doc2.Root.Get("deps") as TomlTable;
            Assert.IsNotNull(deps);
            Assert.AreEqual("1.2.3", ((TomlValue)deps.Get("json")).StringValue);
        }

        [TestMethod]
        public void CommentFreeDocument_AllocatesNoCommentLists()
        {
            var doc = TomlParser.Parse("a = 1\nb = 2\n");
            Assert.IsNull(doc.Root.Get("a").LeadingComments);
            Assert.IsNull(doc.Root.Get("a").TrailingComment);
        }
    }
}
