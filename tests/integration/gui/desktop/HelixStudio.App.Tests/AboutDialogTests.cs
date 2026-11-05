using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 253: the About box gained a copyright line (a standard commercial detail). Guards that it
    /// shows "© {current year} HelixPython" so the year never goes stale.
    /// </summary>
    [TestClass]
    public class AboutDialogTests
    {
        [TestMethod]
        public void About_ShowsCopyright_WithCurrentYearAndProduct()
        {
            using (var dlg = new AboutDialog())
            {
                FieldInfo f = typeof(AboutDialog).GetField(
                    "copyrightLabel", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(f, "copyrightLabel field expected");
                var label = (Label)f.GetValue(dlg);

                StringAssert.Contains(label.Text, "©", "a copyright symbol is expected");
                StringAssert.Contains(label.Text, "HelixPython",
                    "the copyright line carries the current product name");
                StringAssert.Contains(label.Text, DateTime.Now.Year.ToString(),
                    "the copyright year must be the current year, not a hard-coded stale one");
            }
        }
    }
}
