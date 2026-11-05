using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 248: the File > Recent Solutions menu showed Path.GetFileName(path), but every HelixStudio
    /// solution is a "pyproject.toml", so the menu was a list of identical "pyproject.toml" rows.
    /// RecentFilesManager.DisplayName disambiguates by using the containing folder for pyproject.toml
    /// (the project's real identity) and the file name for anything else (e.g. a named .sln).
    /// </summary>
    [TestClass]
    public class RecentFilesDisplayNameTests
    {
        [TestMethod]
        public void Pyproject_ShowsContainingFolder_NotTheGenericFileName()
        {
            Assert.AreEqual("multi-package-workspace",
                RecentFilesManager.DisplayName(@"D:\code\multi-package-workspace\pyproject.toml"));
            Assert.AreEqual("MyApp",
                RecentFilesManager.DisplayName(@"C:\Users\me\Documents\MyApp\pyproject.toml"));
            // Case-insensitive on the file name.
            Assert.AreEqual("Proj",
                RecentFilesManager.DisplayName(@"C:\x\Proj\PYPROJECT.TOML"));
        }

        [TestMethod]
        public void TwoPyprojects_InDifferentFolders_GetDistinctLabels()
        {
            string a = RecentFilesManager.DisplayName(@"D:\a\alpha\pyproject.toml");
            string b = RecentFilesManager.DisplayName(@"D:\a\beta\pyproject.toml");
            Assert.AreNotEqual(a, b, "recent entries in different project folders must be distinguishable");
            Assert.AreEqual("alpha", a);
            Assert.AreEqual("beta", b);
        }

        [TestMethod]
        public void NamedSolutionFile_KeepsItsFileName()
        {
            Assert.AreEqual("App.sln", RecentFilesManager.DisplayName(@"D:\proj\App.sln"));
            Assert.AreEqual("App.slnx", RecentFilesManager.DisplayName(@"D:\proj\App.slnx"));
        }

        [TestMethod]
        public void EmptyOrNull_DoesNotThrow()
        {
            Assert.AreEqual("", RecentFilesManager.DisplayName(""));
            Assert.AreEqual("", RecentFilesManager.DisplayName(null));
        }
    }
}
