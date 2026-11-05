using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Iter 221: the View ▸ Minimap checkmark is kept in sync via the form's VisibleChanged event,
    /// which relies on MinimapForm HIDING (not disposing) when the user closes it with the title-bar
    /// X. This locks that contract so the checkmark-sync assumption can't silently regress.
    /// </summary>
    [TestClass]
    public class MinimapFormTests
    {
        [TestMethod]
        public void UserClosing_HidesInsteadOfDisposing()
        {
            var form = new MinimapForm();
            var args = new FormClosingEventArgs(CloseReason.UserClosing, false);

            MethodInfo onClosing = typeof(MinimapForm).GetMethod(
                "OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(onClosing, "MinimapForm.OnFormClosing should exist.");
            onClosing.Invoke(form, new object[] { args });

            Assert.IsTrue(args.Cancel, "User-closing the minimap must cancel the close (hide instead).");
            Assert.IsFalse(form.IsDisposed, "Minimap must not be disposed on user close, so its "
                + "VisibleChanged handler keeps updating the View ▸ Minimap checkmark.");
            form.Dispose();
        }
    }
}
