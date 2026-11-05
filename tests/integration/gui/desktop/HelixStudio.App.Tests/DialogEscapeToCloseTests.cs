using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 243: modal dialogs must close on Escape — the standard expectation for any dialog opened
    /// with ShowDialog. Two used dialogs lacked it: KeyBindingsDialog (had AcceptButton but no
    /// CancelButton) and ManagePackagesDialog (no Cancel button and no dialog-key handling at all).
    /// </summary>
    [TestClass]
    public class DialogEscapeToCloseTests
    {
        [TestMethod]
        public void KeyBindingsDialog_ClosesOnEscape()
        {
            var mgr = new KeybindingManager(new UserSettingsManager());
            using (var dlg = new KeyBindingsDialog(mgr))
            {
                Assert.IsNotNull(dlg.CancelButton,
                    "Escape must close the Keybindings dialog — CancelButton has to be wired.");
            }
        }

        [TestMethod]
        public void ManagePackagesDialog_HandlesEscapeAsADialogKey()
        {
            using (var dlg = new ManagePackagesDialog())
            {
                MethodInfo pdk = typeof(ManagePackagesDialog).GetMethod(
                    "ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(pdk, "ProcessDialogKey override expected.");
                bool handled = (bool)pdk.Invoke(dlg, new object[] { Keys.Escape });
                Assert.IsTrue(handled, "Escape must be handled (closes the dialog).");
            }
        }
    }
}
