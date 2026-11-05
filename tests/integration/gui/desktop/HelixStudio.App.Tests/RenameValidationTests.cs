using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 305: the Rename Symbol dialog accepted any text, so renaming to "123", "my name" or ""
    /// would produce uncompilable code. Rename is now disabled unless the new name is a valid
    /// identifier. Guards the validator and the OK-enable wiring.
    /// </summary>
    [TestClass]
    public class RenameValidationTests
    {
        private static bool Valid(string s)
        {
            MethodInfo m = typeof(RenameSymbolDialog).GetMethod(
                "IsValidIdentifier", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "IsValidIdentifier expected");
            return (bool)m.Invoke(null, new object[] { s });
        }

        [TestMethod]
        public void IsValidIdentifier_AcceptsIdentifiers_RejectsJunk()
        {
            Assert.IsTrue(Valid("foo"));
            Assert.IsTrue(Valid("_private"));
            Assert.IsTrue(Valid("foo2Bar"));
            Assert.IsTrue(Valid("café"), "Unicode letters are valid Python 3 identifiers");

            Assert.IsFalse(Valid(""), "empty is not valid");
            Assert.IsFalse(Valid("2foo"), "cannot start with a digit");
            Assert.IsFalse(Valid("my name"), "spaces are not allowed");
            Assert.IsFalse(Valid("foo-bar"), "punctuation is not allowed");
        }

        [TestMethod]
        public void RenameButton_DisablesForInvalidName()
        {
            using (var dlg = new RenameSymbolDialog("Class", "OldName"))
            {
                var box = Field<TextBox>(dlg, "_nameBox");
                var rename = Field<Button>(dlg, "renameButton");

                box.Text = "GoodName";
                Assert.IsTrue(rename.Enabled, "a valid name enables Rename");

                box.Text = "123";
                Assert.IsFalse(rename.Enabled, "an invalid name disables Rename");

                box.Text = "";
                Assert.IsFalse(rename.Enabled, "an empty name disables Rename");
            }
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }
    }
}
