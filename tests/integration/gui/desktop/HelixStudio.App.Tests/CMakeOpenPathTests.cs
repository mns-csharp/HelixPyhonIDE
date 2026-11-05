using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class CMakeOpenPathTests
    {
        [TestMethod]
        public void NormalOpenWorkflowRecognizesRootCMakeLists()
        {
            Assert.IsTrue(MainForm.IsProjectPath(@"C:\work\solver\CMakeLists.txt"));
            Assert.IsTrue(MainForm.IsProjectPath(@"C:\work\solver\cmakelists.TXT"));
            Assert.IsFalse(MainForm.IsProjectPath(@"C:\work\solver\module.cmake"));
        }
    }
}
