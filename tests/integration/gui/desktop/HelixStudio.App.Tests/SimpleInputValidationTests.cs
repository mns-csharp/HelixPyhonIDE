using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 295: SimpleInputDialog accepted blank input and returned it untrimmed, so a rename/extract
    /// with an empty (or all-spaces) name clicked OK and then silently did nothing. OK is now disabled
    /// while the input is blank (unless AllowEmpty is set, e.g. the tracepoint "blank clears"), and
    /// Value is trimmed. Guards both.
    /// </summary>
    [TestClass]
    public class SimpleInputValidationTests
    {
        [TestMethod]
        public void Ok_IsDisabled_ForBlankInput_ByDefault()
        {
            using (var dlg = new SimpleInputDialog("t", "p", ""))
            {
                var ok = Field<Button>(dlg, "_okButton");
                var tb = Field<TextBox>(dlg, "_textBox");

                Assert.IsFalse(ok.Enabled, "OK should be disabled for an empty initial value");

                tb.Text = "Foo";
                Assert.IsTrue(ok.Enabled, "OK should enable once the input is non-empty");

                tb.Text = "   ";
                Assert.IsFalse(ok.Enabled, "OK should disable for whitespace-only input");
            }
        }

        [TestMethod]
        public void AllowEmpty_KeepsOkEnabled_ForBlank()
        {
            using (var dlg = new SimpleInputDialog("t", "p", "") { AllowEmpty = true })
            {
                var ok = Field<Button>(dlg, "_okButton");
                Assert.IsTrue(ok.Enabled, "AllowEmpty must keep OK enabled for a blank input");
            }
        }

        [TestMethod]
        public void Value_IsTrimmed()
        {
            using (var dlg = new SimpleInputDialog("t", "p", "  Foo  "))
            {
                Assert.AreEqual("Foo", dlg.Value, "Value should be trimmed");
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
