using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 306: the naming dialogs (Rename, Node editor, Add nested type, Extract interface) now
    /// share one identifier validator instead of each carrying a copy. NodeEditorDialog previously
    /// only rejected an empty name. Guards the shared HelixPythonNamespace.App.Dialogs.Identifiers.IsValid.
    /// </summary>
    [TestClass]
    public class IdentifiersTests
    {
        private static bool IsValid(string s)
        {
            System.Type t = typeof(MainForm).Assembly.GetType("HelixPythonNamespace.App.Dialogs.Identifiers");
            Assert.IsNotNull(t, "Identifiers type expected");
            MethodInfo m = t.GetMethod("IsValid", BindingFlags.Static | BindingFlags.Public);
            Assert.IsNotNull(m, "Identifiers.IsValid expected");
            return (bool)m.Invoke(null, new object[] { s });
        }

        [TestMethod]
        public void Accepts_LegalIdentifiers()
        {
            Assert.IsTrue(IsValid("a"));
            Assert.IsTrue(IsValid("_"));
            Assert.IsTrue(IsValid("Foo"));
            Assert.IsTrue(IsValid("_private2"));
            Assert.IsTrue(IsValid("MyHTTPServer"));
            Assert.IsTrue(IsValid("café"));
        }

        [TestMethod]
        public void Rejects_NonIdentifiers()
        {
            Assert.IsFalse(IsValid(null));
            Assert.IsFalse(IsValid(""));
            Assert.IsFalse(IsValid("1"));
            Assert.IsFalse(IsValid("9lives"));
            Assert.IsFalse(IsValid("my class"));
            Assert.IsFalse(IsValid("a.b"));
            Assert.IsFalse(IsValid("foo-bar"));
            Assert.IsFalse(IsValid("x!"));
        }
    }
}
