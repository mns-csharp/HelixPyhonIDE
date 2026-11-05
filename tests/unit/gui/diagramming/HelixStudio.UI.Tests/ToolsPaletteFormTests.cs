using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Iter 222: closing the floating tools palette with its title-bar X must hide the window AND
    /// raise UserClosed, so the host can clear the "enabled" intent. Without the event the palette
    /// reappears on the next canvas-tab activation (visibility = enabled AND canvas-active) and the
    /// X appears to do nothing.
    /// </summary>
    [TestClass]
    public class ToolsPaletteFormTests
    {
        [TestMethod]
        public void UserClosing_HidesAndRaisesUserClosed()
        {
            var form = new ToolsPaletteForm();
            bool raised = false;
            form.UserClosed += (s, e) => raised = true;

            var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
            MethodInfo onClosing = typeof(ToolsPaletteForm).GetMethod(
                "OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(onClosing, "ToolsPaletteForm.OnFormClosing should exist.");
            onClosing.Invoke(form, new object[] { args });

            Assert.IsTrue(args.Cancel, "User-closing the palette must cancel the close (hide instead).");
            Assert.IsFalse(form.IsDisposed, "Palette must not be disposed on user close.");
            Assert.IsTrue(raised, "UserClosed must fire so the host can drop the enabled intent.");
            form.Dispose();
        }
    }
}
