using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixTextEditorNET.Tests
{
    /// <summary>
    /// Caret appearance knobs: the pixel width exposed for SCI_SETCARETWIDTH parity
    /// (the IDE sets 2 for visibility; the renderer draws the caret pen at this width).
    /// </summary>
    [TestClass]
    public class CaretAppearanceTests
    {
        [TestMethod]
        public void PhantomMouseMove_SameCoordinates_DoesNotEndTheDwell()
        {
            // Windows regenerates WM_MOUSEMOVE with UNCHANGED coordinates whenever the
            // window under the cursor repaints; this control repaints on the caret-blink
            // timer, so hover tooltips (dwell) were cancelled twice a second. A move event
            // at the same location must not reset the dwell.
            using (var sci = new Scintilla())
            {
                sci.Text = "hello world";
                var t = typeof(Scintilla);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var lastPos = t.GetField("_lastMousePos", flags);
                var dwelling = t.GetField("_dwelling", flags);
                var onMove = t.GetMethod("OnMouseMove", flags);

                bool ended = false;
                sci.DwellEnd += (s, e) => ended = true;

                lastPos.SetValue(sci, new System.Drawing.Point(30, 10));
                dwelling.SetValue(sci, true);

                // Phantom move (repaint-induced): same coordinates — dwell must survive.
                onMove.Invoke(sci, new object[] {
                    new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 30, 10, 0) });
                Assert.IsFalse(ended, "a repaint-induced same-position move must not end the dwell");
                Assert.IsTrue((bool)dwelling.GetValue(sci));

                // Real move: dwell ends as before.
                onMove.Invoke(sci, new object[] {
                    new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 31, 10, 0) });
                Assert.IsTrue(ended, "a genuine movement still ends the dwell");
                Assert.IsFalse((bool)dwelling.GetValue(sci));
            }
        }

        [TestMethod]
        public void CaretWidth_DefaultsTo1_AndClampsTo1Through20()
        {
            using (var sci = new Scintilla())
            {
                Assert.AreEqual(1, sci.CaretWidth, "library default stays the native 1px");
                sci.CaretWidth = 2;
                Assert.AreEqual(2, sci.CaretWidth);
                sci.CaretWidth = 0;
                Assert.AreEqual(1, sci.CaretWidth, "0/negative clamps up — an invisible caret is never valid");
                sci.CaretWidth = 99;
                Assert.AreEqual(20, sci.CaretWidth, "SCI_SETCARETWIDTH clamps at 20");
            }
        }
    }
}
