using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 304: creating a project via the wizard now opens its entry file (main.py) so a new project
    /// lands on editable code (the open reuses OpenInEditor, covered elsewhere). These guard the
    /// entry-file resolution that picks WHICH file to open — __main__.py > main.py > *_main.py, none
    /// for a library.
    /// </summary>
    [TestClass]
    public class ProjectEntryFileTests
    {
        [TestMethod]
        public void FindsMainPy_AndPrefersDunderMain()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "main.py"), "print('hi')");
                var proj = new ProjectModel(Path.Combine(dir, "pyproject.toml"), "mypkg", new SolutionModel(Path.Combine(dir, "pyproject.toml"), "sln"))
                {
                    Kind = ProjectKind.App
                };

                Assert.IsTrue(ProjectModel.TryFindEntryFile(proj, out string entry), "main.py should be found");
                StringAssert.EndsWith(entry, "main.py");

                File.WriteAllText(Path.Combine(dir, "__main__.py"), "");
                Assert.IsTrue(ProjectModel.TryFindEntryFile(proj, out entry));
                StringAssert.EndsWith(entry, "__main__.py", "__main__.py should be preferred over main.py");
            }
            finally { Cleanup(dir); }
        }

        [TestMethod]
        public void ReturnsFalse_ForLibrary_WithNoEntry()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "utils.py"), "");
                var proj = new ProjectModel(Path.Combine(dir, "pyproject.toml"), "lib", new SolutionModel(Path.Combine(dir, "pyproject.toml"), "sln"));

                Assert.IsFalse(ProjectModel.TryFindEntryFile(proj, out string entry),
                    "a library (no main/__main__/*_main) has no entry file");
                Assert.IsNull(entry);
            }
            finally { Cleanup(dir); }
        }

        [TestMethod]
        public void ReturnsFalse_ForLibrary_EvenWhenEntryNamedFileExists()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "__main__.py"), "");
                var proj = new ProjectModel(
                    Path.Combine(dir, "pyproject.toml"),
                    "lib",
                    new SolutionModel(Path.Combine(dir, "pyproject.toml"), "sln"))
                {
                    Kind = ProjectKind.ClassLibrary
                };

                Assert.IsFalse(ProjectModel.TryFindEntryFile(proj, out string entry));
                Assert.IsNull(entry);
            }
            finally { Cleanup(dir); }
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_entry_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }
    }
}
