using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.PyToCpp;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class MsvcToolchainTests
    {
        [TestMethod]
        public void LocateVsInstall_ReturnsPath_WhenVsWhereExists()
        {
            if (!File.Exists(MsvcToolchain.VswherePath))
            {
                Assert.Inconclusive("vswhere not present on this build machine.");
                return;
            }
            string path = MsvcToolchain.LocateVsInstall();
            Assert.IsFalse(string.IsNullOrEmpty(path), "vswhere returned an empty installation path.");
            Assert.IsTrue(Directory.Exists(path), "installation path does not exist on disk.");
            // Confirm VC toolset is present under the install.
            string vcvars = Path.Combine(path, @"VC\Auxiliary\Build\vcvars64.bat");
            Assert.IsTrue(File.Exists(vcvars), "expected vcvars64.bat at " + vcvars);
        }
    }
}
