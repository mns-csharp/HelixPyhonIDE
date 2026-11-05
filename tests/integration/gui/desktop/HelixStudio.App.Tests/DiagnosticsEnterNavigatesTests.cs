using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 283 (keyboard workflow): the Analysis- and Build-results lists jumped to source on
    /// double-click but ignored Enter — the Find References / Call Hierarchy lists already navigate
    /// on Enter, so this was an inconsistency. Enter now routes to the same jump-to-source handler.
    /// Guards that the results lists handle Enter (and don't swallow unrelated keys).
    /// </summary>
    [TestClass]
    public class DiagnosticsEnterNavigatesTests
    {
        [TestMethod]
        public void AnalysisResults_EnterIsHandled_ForJumpToSource()
        {
            using (var dlg = new AnalysisResultsDialog())
                AssertEnterHandled(dlg);
        }

        [TestMethod]
        public void BuildResults_EnterIsHandled_ForJumpToSource()
        {
            using (var dlg = new BuildResultsDialog())
                AssertEnterHandled(dlg);
        }

        // Invokes the dialog's OnListViewKeyDown with Enter (no row selected, so the jump handler
        // returns early — no window opens) and asserts the key was consumed. A plain key is left
        // alone, proving the handling is Enter-specific.
        private static void AssertEnterHandled(Form dlg)
        {
            MethodInfo handler = dlg.GetType().GetMethod(
                "OnListViewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler, "OnListViewKeyDown expected on " + dlg.GetType().Name);

            var enter = new KeyEventArgs(Keys.Enter);
            handler.Invoke(dlg, new object[] { dlg, enter });
            Assert.IsTrue(enter.SuppressKeyPress,
                "Enter must be handled (routed to jump-to-source) in " + dlg.GetType().Name);

            var plain = new KeyEventArgs(Keys.B);
            handler.Invoke(dlg, new object[] { dlg, plain });
            Assert.IsFalse(plain.SuppressKeyPress,
                "an unrelated key must not be swallowed in " + dlg.GetType().Name);
        }
    }
}
