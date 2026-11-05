using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Regression cover for the code-generator designer forms (iter 209). Their read-only
    /// "Generated …" preview TextBox must stay out of the tab cycle: a read-only TextBox that
    /// becomes the initially-focused control selects its entire content, which rendered the
    /// whole generated listing as one solid blue selection block (seen in
    /// screenshots/rust-designer/77). TabStop=false prevents it from ever taking initial focus.
    /// </summary>
    [TestClass]
    public class DesignerPreviewBoxTests
    {
        // The Rust and C# designer forms moved to HelixRust / HelixCSharp with the product split;
        // this covers the generator designers HelixPython still ships.
        private static readonly Type[] DesignerForms =
        {
            typeof(TomlDesignerForm),
            // The current form-based designers expose generated-code previews.
            typeof(CMakeDesignerForm),
        };

        [TestMethod]
        public void GeneratedPreviewBox_IsReadOnly_AndNotAFocusGrabber()
        {
            foreach (Type formType in DesignerForms)
            {
                using (var form = (Form)Activator.CreateInstance(formType))
                {
                    FieldInfo f = formType.GetField("_codeBox", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(f, formType.Name + ": expected a _codeBox preview field.");
                    var box = (TextBox)f.GetValue(form);
                    Assert.IsNotNull(box, formType.Name + ": _codeBox was null.");
                    Assert.IsTrue(box.ReadOnly, formType.Name + ": preview must be read-only.");
                    Assert.IsFalse(box.TabStop,
                        formType.Name + ": read-only preview must have TabStop=false so it is never the "
                        + "initially-focused control (which would select all its text).");
                }
            }
        }
    }
}
