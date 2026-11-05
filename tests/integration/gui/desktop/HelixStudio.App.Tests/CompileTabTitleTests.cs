using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 439: the Compile tab shows a "(N)" badge of the total issue count after a compile, so
    /// problems are noticeable from any tab. "Compile" when clean.
    /// </summary>
    [TestClass]
    public class CompileTabTitleTests
    {
        [TestMethod]
        public void Clean_IsPlainCompile()
        {
            Assert.AreEqual("Compile", MainForm.FormatCompileTabTitle(0, 0));
        }

        [TestMethod]
        public void ErrorsAndWarnings_AreSummed()
        {
            Assert.AreEqual("Compile (2)", MainForm.FormatCompileTabTitle(1, 1));
            Assert.AreEqual("Compile (3)", MainForm.FormatCompileTabTitle(3, 0));
            Assert.AreEqual("Compile (5)", MainForm.FormatCompileTabTitle(2, 3));
        }
    }
}
