using System.Drawing;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 282 (screenshot audit): the debug toolbar's Step-Over button rendered a stale "?"
    /// placeholder — both the .resx blob and debug_step_over.png were never real artwork. It is now a
    /// drawn "hop over a statement" glyph. Guards that ToolbarIcons.DebugStepOver() produces a wide
    /// arrow-shaped glyph (ink spanning left AND right edges), not the narrow centred "?".
    /// </summary>
    [TestClass]
    public class DebugStepOverIconTests
    {
        [TestMethod]
        public void DebugStepOver_IsAWideDrawnGlyph_NotTheCentredPlaceholder()
        {
            // ToolbarIcons lives in the add-in SDK assembly (HelixStudio.AddIns) — reach it via reflection.
            System.Type t = typeof(HelixPythonNamespace.AddIns.IHelixAddIn).Assembly.GetType("HelixPythonNamespace.AddIns.Kit.ToolbarIcons");
            Assert.IsNotNull(t, "HelixPythonNamespace.AddIns.Kit.ToolbarIcons type expected");
            MethodInfo m = t.GetMethod("DebugStepOver", BindingFlags.Static | BindingFlags.Public);
            Assert.IsNotNull(m, "DebugStepOver() expected");

            using (var bmp = (Bitmap)m.Invoke(null, null))
            {
                Assert.AreEqual(16, bmp.Width);
                Assert.AreEqual(16, bmp.Height);

                // The drawn hop-arrow spans nearly the full width (arc starts at the left edge, the
                // arrowhead lands at the right). The old "?" placeholder was centred, with the left-
                // and right-most columns blank. Assert ink at BOTH edges.
                Assert.IsTrue(HasInk(bmp, 0, 3), "expected glyph ink near the LEFT edge (arc start)");
                Assert.IsTrue(HasInk(bmp, 12, 15), "expected glyph ink near the RIGHT edge (arrowhead)");
            }
        }

        private static bool HasInk(Bitmap bmp, int xFrom, int xTo)
        {
            for (int x = xFrom; x <= xTo && x < bmp.Width; x++)
                for (int y = 0; y < bmp.Height; y++)
                    if (bmp.GetPixel(x, y).A > 20) return true;
            return false;
        }
    }
}
