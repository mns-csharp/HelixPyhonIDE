using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Iter 391 (PSM Slice 2): the startup selection is persisted surgically into a package's
    /// pyproject.toml under [tool.helixstudio].startup (PSM-10), preserving every other line — unlike
    /// PyProjectWriter.Update which rewrites the whole file.
    /// </summary>
    [TestClass]
    public class PyProjectStartupWriterTests
    {
        private static string TempToml(string content)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_startup_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(path, content);
            return path;
        }

        private const string Sample =
            "[project]\n" +
            "name = \"app_cli\"\n" +
            "description = \"CLI demo\"\n" +
            "dependencies = [\"lib_core\"]\n\n" +
            "[project.scripts]\n" +
            "app-cli = \"app_cli.main:main\"\n\n" +
            "[tool.helixstudio]\n" +
            "interface-style = \"protocol\"\n" +
            "kind = \"app\"\n";

        [TestMethod]
        public void SetStartupTrue_AddsFlag_AndPreservesEverythingElse()
        {
            string path = TempToml(Sample);
            Assert.IsTrue(PyProjectStartupWriter.SetStartup(path, true));

            string text = File.ReadAllText(path);
            StringAssert.Contains(text, "startup = true");
            // Nothing else was dropped.
            StringAssert.Contains(text, "[project.scripts]");
            StringAssert.Contains(text, "app-cli = \"app_cli.main:main\"");
            StringAssert.Contains(text, "description = \"CLI demo\"");
            StringAssert.Contains(text, "kind = \"app\"");

            // Round-trips through the reader.
            var meta = PyProjectReader.Read(path);
            Assert.IsTrue(meta.ToolHelixStudio.TryGetValue("startup", out string v) && v == "true");
        }

        [TestMethod]
        public void SetStartupFalse_RemovesTheFlag_Only()
        {
            string path = TempToml(Sample + "startup = true\n");
            Assert.IsTrue(PyProjectStartupWriter.SetStartup(path, false));

            string text = File.ReadAllText(path);
            Assert.IsFalse(text.Contains("startup"), text);
            StringAssert.Contains(text, "kind = \"app\"");
            StringAssert.Contains(text, "[project.scripts]");
        }

        [TestMethod]
        public void SetStartupTrue_Idempotent_DoesNotDuplicate()
        {
            string path = TempToml(Sample + "startup = true\n");
            PyProjectStartupWriter.SetStartup(path, true);
            string text = File.ReadAllText(path);
            int first = text.IndexOf("startup", System.StringComparison.Ordinal);
            int last = text.LastIndexOf("startup", System.StringComparison.Ordinal);
            Assert.AreEqual(first, last, "startup flag must not be duplicated");
        }

        [TestMethod]
        public void SetStartupTrue_AddsToolSection_WhenMissing()
        {
            string path = TempToml("[project]\nname = \"lib\"\ndependencies = []\n");
            Assert.IsTrue(PyProjectStartupWriter.SetStartup(path, true));
            var meta = PyProjectReader.Read(path);
            Assert.IsTrue(meta.ToolHelixStudio.TryGetValue("startup", out string v) && v == "true");
        }

        // ---- line endings: the file's own, never the host's ----------------------

        // The writer used to join with Environment.NewLine, so on Windows every startup toggle
        // rewrote an LF-authored pyproject.toml as CRLF. Python and TOML do not care, but it turns
        // a one-line change into a whole-file diff for anyone sharing the repo with Linux or macOS
        // — and "surgical, preserves every other line" stops being true at the byte level.

        [TestMethod]
        public void SetStartup_PreservesLfLineEndings()
        {
            string path = TempToml(Sample);                       // Sample is LF throughout
            Assert.IsTrue(PyProjectStartupWriter.SetStartup(path, true));

            string text = File.ReadAllText(path);
            StringAssert.Contains(text, "startup = true");
            Assert.IsFalse(text.Contains("\r"), "an LF file must stay LF, not become CRLF");
        }

        [TestMethod]
        public void SetStartup_PreservesCrLfLineEndings()
        {
            string path = TempToml(Sample.Replace("\n", "\r\n"));
            Assert.IsTrue(PyProjectStartupWriter.SetStartup(path, true));

            string text = File.ReadAllText(path);
            StringAssert.Contains(text, "startup = true");
            Assert.AreEqual(0, System.Text.RegularExpressions.Regex.Matches(text, "(?<!\r)\n").Count,
                "a CRLF file must stay CRLF — no bare LF may appear");
        }

        [TestMethod]
        public void SetStartup_RoundTrip_LeavesAnLfFileByteIdentical()
        {
            // The strongest form: set then clear must return the exact original bytes, so a
            // toggle-and-undo shows up as no diff at all.
            string path = TempToml(Sample);
            string before = File.ReadAllText(path);

            Assert.IsTrue(PyProjectStartupWriter.SetStartup(path, true));
            PyProjectStartupWriter.SetStartup(path, false);

            Assert.AreEqual(before, File.ReadAllText(path));
        }
    }
}
