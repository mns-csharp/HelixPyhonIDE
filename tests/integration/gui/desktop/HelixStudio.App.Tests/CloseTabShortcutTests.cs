using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 285: the main tab area could be closed with the X button and middle-click, but not from
    /// the keyboard. ProcessCmdKey now closes the active document tab on Ctrl+F4 (Visual Studio) and
    /// Ctrl+W (VS Code). Guards the exact key-set that triggers a close (and that near-misses don't).
    /// </summary>
    [TestClass]
    public class CloseTabShortcutTests
    {
        [TestMethod]
        public void CtrlW_AndCtrlF4_CloseTheActiveTab()
        {
            Assert.IsTrue(IsCloseShortcut(Keys.Control | Keys.W), "Ctrl+W (VS Code) should close the tab");
            Assert.IsTrue(IsCloseShortcut(Keys.Control | Keys.F4), "Ctrl+F4 (Visual Studio) should close the tab");
        }

        [TestMethod]
        public void NearMisses_DoNotCloseTheTab()
        {
            Assert.IsFalse(IsCloseShortcut(Keys.W), "W alone must not close a tab");
            Assert.IsFalse(IsCloseShortcut(Keys.F4), "bare F4 must not close a tab");
            Assert.IsFalse(IsCloseShortcut(Keys.Control | Keys.S), "Ctrl+S is save, not close");
            Assert.IsFalse(IsCloseShortcut(Keys.Control | Keys.Shift | Keys.W), "an extra modifier must not match");
            Assert.IsFalse(IsCloseShortcut(Keys.Alt | Keys.F4), "Alt+F4 closes the window, not a tab");
        }

        private static bool IsCloseShortcut(Keys k)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "IsCloseTabShortcut", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.IsCloseTabShortcut(Keys) expected");
            return (bool)m.Invoke(null, new object[] { k });
        }
    }
}
