using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 309: the Member editor accepted any non-empty name, so a method/field could be named
    /// "123" or "my method". OK is now disabled unless the name is a valid identifier. Guards the
    /// OK-enable wiring on a live dialog.
    /// </summary>
    [TestClass]
    public class MemberNameValidationTests
    {
        [TestMethod]
        public void Ok_TracksMemberNameValidity()
        {
            using (var dlg = new MemberEditorDialog())
            {
                var box = Field<TextBox>(dlg, "_nameTextBox");
                var ok = Field<Button>(dlg, "_okButton");

                box.Text = "myMethod";
                Assert.IsTrue(ok.Enabled, "a valid member name enables OK");

                box.Text = "123";
                Assert.IsFalse(ok.Enabled, "a name starting with a digit disables OK");

                box.Text = "my method";
                Assert.IsFalse(ok.Enabled, "a name with a space disables OK");

                box.Text = "";
                Assert.IsFalse(ok.Enabled, "an empty name disables OK");
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
