using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PyProjectIOTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void WriteThenRead_MinimalTomlRoundTrips()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "pyproject.toml");
            PyProjectWriter.WriteMinimal(path, "my_project", "3.11");
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual("my_project", meta.Name);
            Assert.AreEqual("0.1.0", meta.Version);
            Assert.AreEqual("3.11", meta.PythonVersion);
        }

        [TestMethod]
        public void WriteThenRead_SpecialCharsInNameRoundTripExactly()
        {
            // Regression (iter 52 writer-escape + iter 53 reader-unescape): a name with a
            // double-quote and backslash must survive write -> read UNCHANGED. Before the
            // reader learned to unescape, this read back with extra backslashes.
            string dir = TempDir();
            string path = Path.Combine(dir, "pyproject.toml");
            string q = ((char)34).ToString();   // "
            string bs = ((char)92).ToString();  // backslash
            string weird = "a" + q + "b" + bs + "c";   // a"b\c
            PyProjectWriter.WriteMinimal(path, weird, "3.11");
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual(weird, meta.Name, "special chars must round-trip write->read exactly");
        }

        [TestMethod]
        public void WriteMinimal_EscapesSpecialCharsInName()
        {
            // Regression (iter 52): a project name containing a double-quote or backslash
            // produced malformed TOML that tomllib/uv/pip could not parse. Values must be
            // escaped per the TOML basic-string rules.
            string dir = TempDir();
            string path = Path.Combine(dir, "pyproject.toml");
            string q = ((char)34).ToString();   // "
            string bs = ((char)92).ToString();  // backslash
            string weird = "a" + q + "b" + bs + "c";   // a"b\c
            PyProjectWriter.WriteMinimal(path, weird, "3.11");
            string text = File.ReadAllText(path);
            // Expected escaped line: name = "a\"b\\c"
            string expected = "name = " + q + "a" + bs + q + "b" + bs + bs + "c" + q;
            Assert.IsTrue(text.Contains(expected),
                "special chars in the project name must be TOML-escaped; got:\n" + text);
        }

        [TestMethod]
        public void ReadsDependenciesInlineArray()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(path,
                "[project]\nname = \"foo\"\nrequires-python = \">=3.12\"\ndependencies = [\"requests\", \"click>=8.0\"]\n");
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual(2, meta.Dependencies.Count);
            CollectionAssert.Contains(meta.Dependencies, "requests");
            CollectionAssert.Contains(meta.Dependencies, "click>=8.0");
        }

        [TestMethod]
        public void ReadsToolHelixstudioTable()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(path,
                "[project]\nname = \"foo\"\n\n[tool.helixstudio]\ninterface-style = \"abc\"\n");
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual("abc", meta.ToolHelixStudio["interface-style"]);
        }

        [TestMethod]
        public void CommentsAndBlankLinesAreIgnored()
        {
            string dir = TempDir();
            string path = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(path,
                "# top comment\n[project]\nname = \"bar\" # inline comment\n\n# another\nrequires-python = \">=3.10\"\n");
            PyProjectMetadata meta = PyProjectReader.Read(path);
            Assert.AreEqual("bar", meta.Name);
            Assert.AreEqual("3.10", meta.PythonVersion);
        }
    }
}
