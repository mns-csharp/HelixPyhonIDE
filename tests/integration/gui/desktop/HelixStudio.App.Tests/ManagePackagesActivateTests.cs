using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 296: a search result had to be selected and then installed via the Install button — no
    /// activate. Double-click / Enter on a result now installs it. These guard the safety condition:
    /// activating with nothing selected is a no-op and doesn't swallow the key (so the install side
    /// effect can't misfire). The positive install path is the same OnInstallPackage the button uses.
    /// </summary>
    [TestClass]
    public class ManagePackagesActivateTests
    {
        [TestMethod]
        public void Activate_WithNoSelection_IsANoOp_AndDoesNotConsumeEnter()
        {
            using (var dlg = new ManagePackagesDialog())
            {
                // Double-click handler: no selection => must not throw / install.
                Invoke(dlg, "OnSearchResultActivated", null, EventArgs.Empty);

                // Enter with no selection: must not be consumed (nothing to install).
                var enter = new KeyEventArgs(Keys.Enter);
                Invoke(dlg, "OnSearchResultsKeyDown", null, enter);
                Assert.IsFalse(enter.Handled, "Enter with no selection must not be consumed");

                // A non-Enter key must never be consumed.
                var other = new KeyEventArgs(Keys.A);
                Invoke(dlg, "OnSearchResultsKeyDown", null, other);
                Assert.IsFalse(other.Handled, "a non-Enter key must not be consumed");
            }
        }

        private static void Invoke(object obj, string method, params object[] args)
        {
            MethodInfo m = obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, method + " expected");
            m.Invoke(obj, args);
        }
    }
}
