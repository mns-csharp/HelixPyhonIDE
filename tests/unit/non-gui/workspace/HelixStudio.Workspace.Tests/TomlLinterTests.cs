using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toml;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class TomlLinterTests
    {
        private static bool Has(System.Collections.Generic.List<TomlLintFinding> f, string code)
        {
            return f.Any(x => x.Code == code);
        }

        [TestMethod]
        public void FoldsParserDuplicateKeyError()
        {
            var f = TomlLinter.Lint("[t]\nx = 1\nx = 2\n");
            Assert.IsTrue(f.Any(x => x.Code == "TML001" && x.Severity == TomlLintSeverity.Error));
        }

        [TestMethod]
        public void Flags_TrailingWhitespace()
        {
            var f = TomlLinter.Lint("title = \"ok\"   \n");   // trailing spaces
            Assert.IsTrue(Has(f, "TLN001"));
        }

        [TestMethod]
        public void Flags_TabIndentation()
        {
            var f = TomlLinter.Lint("[t]\n\tx = 1\n");
            Assert.IsTrue(Has(f, "TLN002"));
        }

        [TestMethod]
        public void Flags_HeterogeneousArray()
        {
            var f = TomlLinter.Lint("mixed = [1, \"two\", true]\n");
            Assert.IsTrue(Has(f, "TLN003"), "mixed-type array flagged");
        }

        [TestMethod]
        public void DoesNotFlag_HomogeneousArray()
        {
            var f = TomlLinter.Lint("deps = [\"a\", \"b\", \"c\"]\n");
            Assert.IsFalse(Has(f, "TLN003"));
        }

        [TestMethod]
        public void Pyproject_MissingProjectTable_Flagged()
        {
            var f = TomlLinter.Lint("[tool.x]\ny = 1\n", "pyproject.toml");
            Assert.IsTrue(f.Any(x => x.Code == "TLN010"));
        }

        [TestMethod]
        public void Pyproject_MissingName_Flagged()
        {
            var f = TomlLinter.Lint("[project]\nversion = \"0.1.0\"\n", "pyproject.toml");
            Assert.IsTrue(f.Any(x => x.Code == "TLN010" && x.Message.Contains("name")));
        }

        [TestMethod]
        public void Pyproject_Valid_NoSchemaWarning()
        {
            string src = "[project]\nname = \"demo\"\nversion = \"0.1.0\"\ndependencies = [\"a\", \"b\"]\n";
            var f = TomlLinter.Lint(src, "pyproject.toml");
            Assert.IsFalse(Has(f, "TLN010"), "valid pyproject has no schema warning: " + string.Join("; ", f.Select(x => x.ToString())));
        }

        [TestMethod]
        public void NonPyproject_NoSchemaProfile()
        {
            // A non-pyproject .toml without [project] must NOT get the PEP-621 warning.
            var f = TomlLinter.Lint("[server]\nhost = \"localhost\"\n", "config.toml");
            Assert.IsFalse(Has(f, "TLN010"));
        }

        [TestMethod]
        public void Flags_MixedTableStyles()
        {
            // servers.alpha is header-declared; servers.beta is dotted-key declared — mixed at one level.
            var f = TomlLinter.Lint(
                "[servers.alpha]\nip = \"10.0.0.1\"\n\n[servers]\nbeta.ip = \"10.0.0.2\"\n", "config.toml");
            Assert.IsTrue(Has(f, "TLN004"), "mixed [header] + dotted-key sub-tables flagged");
        }

        [TestMethod]
        public void DoesNotFlag_ConsistentHeaderStyle()
        {
            var f = TomlLinter.Lint(
                "[project]\nname = \"demo\"\nversion = \"0.1.0\"\n\n[tool.helixstudio]\nkind = \"app\"\n", "pyproject.toml");
            Assert.IsFalse(Has(f, "TLN004"), "all-header style must not be flagged");
        }

        [TestMethod]
        public void DoesNotFlag_InlineTablesAsMixed()
        {
            // Inline tables ({…}) are values, not a competing table style — a header + inline mix is idiomatic
            // (e.g. pyproject `authors = [{name=…}]`) and must NOT trip TLN004.
            var f = TomlLinter.Lint(
                "[project]\nname = \"demo\"\nauthors = [{ name = \"A\", email = \"a@x\" }]\n", "pyproject.toml");
            Assert.IsFalse(Has(f, "TLN004"));
        }
    }
}
