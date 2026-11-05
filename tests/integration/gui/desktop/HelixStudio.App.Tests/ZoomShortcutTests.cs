using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 310: canvas zoom had toolbar buttons + a preset dropdown (verified working) but no keyboard
    /// shortcuts. Ctrl++ / Ctrl+- / Ctrl+0 now zoom in / out / reset on the designer canvas. Guards the
    /// chord classifier (including numpad variants) and that near-misses map to None.
    /// </summary>
    [TestClass]
    public class ZoomShortcutTests
    {
        private static string Classify(Keys keyData)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "ClassifyZoomShortcut", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "ClassifyZoomShortcut expected");
            return m.Invoke(null, new object[] { keyData }).ToString();
        }

        [TestMethod]
        public void ZoomIn_Out_Reset_AreRecognised_IncludingNumpad()
        {
            Assert.AreEqual("In", Classify(Keys.Control | Keys.Oemplus));
            Assert.AreEqual("In", Classify(Keys.Control | Keys.Add));
            Assert.AreEqual("Out", Classify(Keys.Control | Keys.OemMinus));
            Assert.AreEqual("Out", Classify(Keys.Control | Keys.Subtract));
            Assert.AreEqual("Reset", Classify(Keys.Control | Keys.D0));
            Assert.AreEqual("Reset", Classify(Keys.Control | Keys.NumPad0));
        }

        [TestMethod]
        public void NearMisses_MapToNone()
        {
            Assert.AreEqual("None", Classify(Keys.Control | Keys.S));
            Assert.AreEqual("None", Classify(Keys.Oemplus), "no Ctrl -> not a zoom shortcut");
            Assert.AreEqual("None", Classify(Keys.Control | Keys.Shift | Keys.Oemplus), "extra modifier -> no");
            Assert.AreEqual("None", Classify(Keys.Control | Keys.D9));
        }
    }
}
