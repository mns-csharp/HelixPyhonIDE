using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 244: drag-drop must accept exactly what File > Open accepts. The Open dialog and
    /// SolutionService both handle pyproject.toml AND .sln/.slnx, but MainForm.IsProjectPath (the
    /// drag-drop gate) used to match only pyproject.toml — so a .sln you could Open by menu was
    /// silently rejected when dropped. This guards that the gate now recognizes all three.
    /// </summary>
    [TestClass]
    public class ProjectPathAcceptanceTests
    {
        private static bool IsProjectPath(string p)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "IsProjectPath", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.IsProjectPath(string) expected.");
            return (bool)m.Invoke(null, new object[] { p });
        }

        [TestMethod]
        public void IsProjectPath_AcceptsPyprojectAndSolutionFiles()
        {
            Assert.IsTrue(IsProjectPath(@"C:\proj\pyproject.toml"), "pyproject.toml is openable.");
            Assert.IsTrue(IsProjectPath(@"C:\proj\App.sln"), ".sln is openable (loads natively / via adjacent pyproject).");
            Assert.IsTrue(IsProjectPath(@"C:\proj\App.slnx"), ".slnx is openable.");
            Assert.IsTrue(IsProjectPath(@"C:\proj\PYPROJECT.TOML"), "matching is case-insensitive.");
            Assert.IsTrue(IsProjectPath(@"C:\proj\App.SLN"), "extension matching is case-insensitive.");
        }

        [TestMethod]
        public void IsProjectPath_RejectsNonProjectFiles()
        {
            Assert.IsFalse(IsProjectPath(@"C:\proj\module.py"), "a plain source file is not a project.");
            Assert.IsFalse(IsProjectPath(@"C:\proj\settings.toml"), "an arbitrary .toml is not a project (only pyproject.toml).");
            Assert.IsFalse(IsProjectPath(""), "empty path is not a project.");
            Assert.IsFalse(IsProjectPath(null), "null path is not a project.");
        }
    }
}
