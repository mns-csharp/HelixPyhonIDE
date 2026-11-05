using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toml;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class TomlAnalyzerTests
    {
        private static bool Has(System.Collections.Generic.List<TomlLintFinding> f, string code)
        {
            return f.Any(x => x.Code == code);
        }

        [TestMethod]
        public void ValidPyproject_NoFindings()
        {
            string src =
                "[project]\nname = \"demo\"\nversion = \"0.1.0\"\nrequires-python = \">=3.12\"\n" +
                "dependencies = [\"numpy>=1.24\", \"requests[security]>=2.31\"]\n";
            var f = TomlAnalyzer.Analyze(src);
            Assert.AreEqual(0, f.Count, string.Join("; ", f.Select(x => x.ToString())));
        }

        [TestMethod]
        public void Flags_MissingName()
        {
            var f = TomlAnalyzer.Analyze("[project]\nversion = \"0.1.0\"\n");
            Assert.IsTrue(f.Any(x => x.Code == "TMA001" && x.Message.Contains("name")));
        }

        [TestMethod]
        public void Flags_WrongTypedField()
        {
            var f = TomlAnalyzer.Analyze("[project]\nname = 123\nversion = \"1.0\"\n");
            Assert.IsTrue(f.Any(x => x.Code == "TMA001" && x.Message.Contains("must be a string")));
        }

        [TestMethod]
        public void Flags_MalformedPep508Dependency()
        {
            var f = TomlAnalyzer.Analyze("[project]\nname = \"d\"\nversion = \"1\"\ndependencies = [\">=1.0\"]\n");
            Assert.IsTrue(f.Any(x => x.Code == "TMA002"), "requirement with no distribution name flagged");
        }

        [TestMethod]
        public void ValidPep508_Dependencies_NotFlagged()
        {
            var f = TomlAnalyzer.Analyze("[project]\nname = \"d\"\nversion = \"1\"\ndependencies = [\"flask\", \"numpy>=1.24,<2\"]\n");
            Assert.IsFalse(Has(f, "TMA002"));
        }

        [TestMethod]
        public void Flags_UnknownProjectKey()
        {
            var f = TomlAnalyzer.Analyze("[project]\nname = \"d\"\nversion = \"1\"\ndependancies = []\n"); // typo
            Assert.IsTrue(f.Any(x => x.Code == "TMA003" && x.Message.Contains("dependancies")));
        }

        [TestMethod]
        public void DynamicVersion_NotFlaggedMissing()
        {
            var f = TomlAnalyzer.Analyze("[project]\nname = \"d\"\ndynamic = [\"version\"]\n");
            Assert.IsFalse(f.Any(x => x.Code == "TMA001" && x.Message.Contains("version")), "dynamic version is allowed");
        }

        [TestMethod]
        public void WorkspaceMembers_UnresolvedFlagged()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_wsana_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "pkg_present"));
            try
            {
                string src = "[project]\nname = \"root\"\nversion = \"1\"\n\n[tool.uv.workspace]\nmembers = [\"pkg_present\", \"pkg_missing\"]\n";
                var f = TomlAnalyzer.Analyze(src, dir);
                var wm = f.Where(x => x.Code == "TMA004").ToList();
                Assert.IsTrue(wm.Any(x => x.Message.Contains("pkg_missing")), "missing member flagged");
                Assert.IsFalse(wm.Any(x => x.Message.Contains("pkg_present")), "present member not flagged");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
