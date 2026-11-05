using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Run arguments are persisted surgically into a package's pyproject.toml under
    /// <c>[tool.helixstudio].run-args</c>, preserving every other line — the same contract as
    /// <see cref="PyProjectStartupWriter"/>, and read back by <see cref="PyProjectReader"/> so the
    /// value survives a round trip. The escaping cases matter most: run arguments are usually
    /// Windows paths, so a value like <c>--out "C:\data\x.tsv"</c> must not corrupt the manifest.
    /// </summary>
    [TestClass]
    public class PyProjectRunArgsWriterTests
    {
        private static string TempToml(string content)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_runargs_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(path, content);
            return path;
        }

        private const string Sample =
            "[project]\n" +
            "name = \"prepare-sample-tsv\"\n" +
            "dependencies = [\"sample-common\"]\n\n" +
            "[project.scripts]\n" +
            "prepare = \"prepare_sample_tsv.main:main\"\n\n" +
            "[tool.helixstudio]\n" +
            "interface-style = \"protocol\"\n" +
            "kind = \"app\"\n" +
            "startup = true\n";

        private static string ReadBack(string path)
        {
            PyProjectMetadata meta = PyProjectReader.Read(path);
            string v;
            return meta.ToolHelixStudio.TryGetValue("run-args", out v) ? v : null;
        }

        [TestMethod]
        public void SetRunArgs_AddsKey_AndPreservesEverythingElse()
        {
            string path = TempToml(Sample);
            Assert.IsTrue(PyProjectRunArgsWriter.SetRunArgs(path, "input.tsv output.tsv"));

            string text = File.ReadAllText(path);
            StringAssert.Contains(text, "run-args = \"input.tsv output.tsv\"");
            StringAssert.Contains(text, "[project.scripts]");
            StringAssert.Contains(text, "prepare = \"prepare_sample_tsv.main:main\"");
            StringAssert.Contains(text, "startup = true");
            StringAssert.Contains(text, "kind = \"app\"");
        }

        [TestMethod]
        public void SetRunArgs_ReplacesAnExistingValue_WithoutDuplicating()
        {
            string path = TempToml(Sample);
            PyProjectRunArgsWriter.SetRunArgs(path, "first.tsv");
            PyProjectRunArgsWriter.SetRunArgs(path, "second.tsv");

            string text = File.ReadAllText(path);
            Assert.AreEqual("second.tsv", ReadBack(path));
            int occurrences = text.Split(new[] { "run-args" }, System.StringSplitOptions.None).Length - 1;
            Assert.AreEqual(1, occurrences, "the key must be replaced in place, not appended again");
        }

        [TestMethod]
        public void SetRunArgs_BlankClearsTheKey()
        {
            string path = TempToml(Sample);
            PyProjectRunArgsWriter.SetRunArgs(path, "input.tsv");
            PyProjectRunArgsWriter.SetRunArgs(path, "   ");

            StringAssert.DoesNotMatch(File.ReadAllText(path), new System.Text.RegularExpressions.Regex("run-args"));
            Assert.IsNull(ReadBack(path));
        }

        [TestMethod]
        public void SetRunArgs_ClearingWhenAbsent_IsANoOp()
        {
            string path = TempToml(Sample);
            string before = File.ReadAllText(path);
            Assert.IsTrue(PyProjectRunArgsWriter.SetRunArgs(path, null));
            Assert.AreEqual(before, File.ReadAllText(path));
        }

        [TestMethod]
        public void SetRunArgs_CreatesTheToolTableWhenMissing()
        {
            string path = TempToml("[project]\nname = \"solo\"\n");
            PyProjectRunArgsWriter.SetRunArgs(path, "--fast");

            string text = File.ReadAllText(path);
            StringAssert.Contains(text, "[tool.helixstudio]");
            Assert.AreEqual("--fast", ReadBack(path));
        }

        [TestMethod]
        public void WindowsPathsWithQuotesAndBackslashes_RoundTripExactly()
        {
            // The case this feature is actually for: a quoted absolute path as an argument.
            const string args = "input.tsv \"C:\\My Data\\rows.tsv\"";
            string path = TempToml(Sample);
            PyProjectRunArgsWriter.SetRunArgs(path, args);

            Assert.AreEqual(args, ReadBack(path),
                "an unescaped backslash would either break the parse or become an escape sequence");
        }

        [TestMethod]
        public void AValueContainingAHash_IsNotTruncatedAsAComment()
        {
            const string args = "--label \"batch #7\"";
            string path = TempToml(Sample);
            PyProjectRunArgsWriter.SetRunArgs(path, args);

            Assert.AreEqual(args, ReadBack(path));
        }

        [TestMethod]
        public void MissingFile_ReturnsFalse()
        {
            Assert.IsFalse(PyProjectRunArgsWriter.SetRunArgs(
                Path.Combine(Path.GetTempPath(), "helix_nope_" + System.Guid.NewGuid().ToString("N"), "pyproject.toml"),
                "x"));
        }

        // ---- line endings: the file's own, never the host's ----------------------
        // Same fix and same reason as PyProjectStartupWriter — joining with Environment.NewLine
        // CRLF-ified an LF manifest on every edit, so "preserves every other line" was true of the
        // text but not of the bytes, and every save produced a whole-file diff.

        [TestMethod]
        public void SetRunArgs_PreservesLfLineEndings()
        {
            string path = TempToml(Sample);
            Assert.IsTrue(PyProjectRunArgsWriter.SetRunArgs(path, "--verbose"));

            string text = File.ReadAllText(path);
            StringAssert.Contains(text, "--verbose");
            Assert.IsFalse(text.Contains("\r"), "an LF file must stay LF, not become CRLF");
        }

        [TestMethod]
        public void SetRunArgs_PreservesCrLfLineEndings()
        {
            string path = TempToml(Sample.Replace("\n", "\r\n"));
            Assert.IsTrue(PyProjectRunArgsWriter.SetRunArgs(path, "--verbose"));

            string text = File.ReadAllText(path);
            StringAssert.Contains(text, "--verbose");
            Assert.AreEqual(0, System.Text.RegularExpressions.Regex.Matches(text, "(?<!\r)\n").Count,
                "a CRLF file must stay CRLF — no bare LF may appear");
        }

        [TestMethod]
        public void SetRunArgs_RoundTrip_LeavesAnLfFileByteIdentical()
        {
            string path = TempToml(Sample);
            string before = File.ReadAllText(path);

            Assert.IsTrue(PyProjectRunArgsWriter.SetRunArgs(path, "--verbose"));
            PyProjectRunArgsWriter.SetRunArgs(path, null);   // clear

            Assert.AreEqual(before, File.ReadAllText(path));
        }
    }
}
