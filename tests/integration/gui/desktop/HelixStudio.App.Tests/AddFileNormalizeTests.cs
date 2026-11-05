using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 303: adding a file now opens it in the editor (previously it only appeared in the tree).
    /// The open itself reuses OpenInEditor (covered by EditorOpensFileTests); this guards the
    /// filename normalisation that runs first — a user-typed name always becomes a .py file.
    /// </summary>
    [TestClass]
    public class AddFileNormalizeTests
    {
        private static string Normalize(string raw)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "NormalizeNewFilePath", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.NormalizeNewFilePath expected");
            return (string)m.Invoke(null, new object[] { raw });
        }

        [TestMethod]
        public void KeepsDotPy_AddsWhenMissing_ReplacesWrongExtension()
        {
            Assert.AreEqual(@"C:\p\mod.py", Normalize(@"C:\p\mod.py"), "an existing .py is kept");
            Assert.AreEqual(@"C:\p\mod.py", Normalize(@"C:\p\mod"), ".py is appended when missing");
            Assert.AreEqual(@"C:\p\mod.py", Normalize(@"C:\p\mod.txt"), "a wrong extension is replaced");
        }

        [TestMethod]
        public void DotPy_IsCaseInsensitive()
        {
            Assert.AreEqual(@"C:\p\MOD.PY", Normalize(@"C:\p\MOD.PY"), "an uppercase .PY is already Python");
        }
    }
}
