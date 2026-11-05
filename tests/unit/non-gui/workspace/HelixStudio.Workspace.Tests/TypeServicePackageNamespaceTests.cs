using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// SRS §5.7 (Add Class/Interface under the exact clicked node): the file-node command derives
    /// the target package from the clicked file's directory. PackageNamespaceForFile is the inverse
    /// of TypeService.ComputeFilePath's ns → projectDir\src\ns-dirs mapping; null means "no package
    /// here — fall back to the project default namespace".
    /// </summary>
    [TestClass]
    public class TypeServicePackageNamespaceTests
    {
        private static ProjectModel Project()
        {
            var sln = new SolutionModel(@"D:\r\sol.toml", "sol");
            return new ProjectModel(@"D:\r\app\pyproject.toml", "app", sln);
        }

        [TestMethod]
        public void FileInsidePackage_YieldsDottedNamespace()
        {
            Assert.AreEqual("pkg", TypeService.PackageNamespaceForFile(
                Project(), @"D:\r\app\src\pkg\mod.py"));
        }

        [TestMethod]
        public void FileInsideNestedPackage_YieldsFullDottedPath()
        {
            Assert.AreEqual("pkg.sub", TypeService.PackageNamespaceForFile(
                Project(), @"D:\r\app\src\pkg\sub\mod.py"));
        }

        [TestMethod]
        public void FileInsideFlatPackage_YieldsDottedNamespace()
        {
            Assert.AreEqual("pkg", TypeService.PackageNamespaceForFile(
                Project(), @"D:\r\app\pkg\mod.py"));
            Assert.AreEqual("pkg.sub", TypeService.PackageNamespaceForFile(
                Project(), @"D:\r\app\pkg\sub\mod.py"));
        }

        [TestMethod]
        public void FileDirectlyInSrc_FallsBackToNull()
        {
            Assert.IsNull(TypeService.PackageNamespaceForFile(
                Project(), @"D:\r\app\src\mod.py"),
                "a file directly in src\\ has no package directory to join");
        }

        [TestMethod]
        public void FileOutsideSrcTree_FallsBackToNull()
        {
            Assert.IsNull(TypeService.PackageNamespaceForFile(
                Project(), @"D:\r\app\main.py"),
                "a loose root file is not in any src package");
            Assert.IsNull(TypeService.PackageNamespaceForFile(
                Project(), @"D:\elsewhere\src\pkg\mod.py"),
                "a file under a DIFFERENT project's src tree must not match");
        }

        [TestMethod]
        public void NullInputs_FallBackToNull()
        {
            Assert.IsNull(TypeService.PackageNamespaceForFile(null, @"D:\r\app\src\p\m.py"));
            Assert.IsNull(TypeService.PackageNamespaceForFile(Project(), null));
            Assert.IsNull(TypeService.PackageNamespaceForFile(Project(), ""));
        }
    }
}
