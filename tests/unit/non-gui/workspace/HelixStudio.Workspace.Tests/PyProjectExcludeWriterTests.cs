using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §5.9 (loop 530): Exclude/Include from Project is persisted into
    /// [tool.helixstudio].excluded, preserving every other line via TomlTableMerger
    /// — the same surgical-edit approach as PyProjectStartupWriter.
    /// </summary>
    [TestClass]
    public class PyProjectExcludeWriterTests
    {
        private static string TempToml(string content)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_exclude_" + System.Guid.NewGuid().ToString("N"));
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
            "[tool.helixstudio]\n" +
            "kind = \"app\"\n";

        [TestMethod]
        public void SetExcludedTrue_AddsEntry_AndPreservesEverythingElse()
        {
            string path = TempToml(Sample);
            PyProjectExcludeWriter.SetExcluded(path, "scratch/notes.py", true);

            var meta = PyProjectReader.Read(path);
            CollectionAssert.Contains(meta.Excluded, "scratch/notes.py");
            Assert.AreEqual("app_cli", meta.Name, "unrelated [project] content survives");
            Assert.AreEqual("app", meta.ToolHelixStudio["kind"], "unrelated [tool.helixstudio] keys survive");
        }

        [TestMethod]
        public void SetExcludedTrue_BackslashInput_IsNormalizedToForwardSlash()
        {
            string path = TempToml(Sample);
            PyProjectExcludeWriter.SetExcluded(path, @"scratch\notes.py", true);
            CollectionAssert.Contains(PyProjectReader.Read(path).Excluded, "scratch/notes.py");
        }

        [TestMethod]
        public void SetExcludedTrue_Twice_DoesNotDuplicate()
        {
            string path = TempToml(Sample);
            PyProjectExcludeWriter.SetExcluded(path, "a.py", true);
            PyProjectExcludeWriter.SetExcluded(path, "a.py", true);
            Assert.AreEqual(1, PyProjectReader.Read(path).Excluded.Count);
        }

        [TestMethod]
        public void SetExcludedFalse_RemovesOnlyThatEntry()
        {
            string path = TempToml(Sample);
            PyProjectExcludeWriter.SetExcluded(path, "a.py", true);
            PyProjectExcludeWriter.SetExcluded(path, "b.py", true);
            PyProjectExcludeWriter.SetExcluded(path, "a.py", false);

            var excluded = PyProjectReader.Read(path).Excluded;
            Assert.AreEqual(1, excluded.Count);
            CollectionAssert.Contains(excluded, "b.py");
            CollectionAssert.DoesNotContain(excluded, "a.py");
        }

        [TestMethod]
        public void SetExcludedFalse_OnAlreadyAbsentEntry_IsANoOp()
        {
            string path = TempToml(Sample);
            PyProjectExcludeWriter.SetExcluded(path, "never-added.py", false);
            Assert.AreEqual(0, PyProjectReader.Read(path).Excluded.Count);
        }

        [TestMethod]
        public void SetExcluded_OnFileWithNoToolTableYet_CreatesOne()
        {
            string path = TempToml("[project]\nname = \"solo\"\n");
            PyProjectExcludeWriter.SetExcluded(path, "pkg/legacy", true);
            var meta = PyProjectReader.Read(path);
            CollectionAssert.Contains(meta.Excluded, "pkg/legacy");
            Assert.AreEqual("solo", meta.Name);
        }
    }
}
