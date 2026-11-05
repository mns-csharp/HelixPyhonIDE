using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.GuiDesigner.Model;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// A form made smaller than its contents.
    ///
    /// At run time a widget cannot paint outside its window, so the designer must not either. It
    /// did: shrinking the form left its notebook and panel drawn out in open canvas beside the
    /// form they belong to, which reads as the designer having broken rather than as a form that
    /// is too small for what is on it.
    /// </summary>
    [TestClass]
    public class GuiDesignerClippingTests
    {
        private static GuiForm ModelWithOversizedChild()
        {
            var model = new GuiForm { Width = 200, Height = 160 };
            model.Controls.Add(new GuiControl
            {
                Kind = GuiControlKind.Panel, Name = "big",
                X = 10, Y = 10, Width = 600, Height = 400,
            });
            return model;
        }

        // Paints the designer's canvas into a bitmap the same way the control does.
        private static Bitmap PaintCanvas(GuiDesignerForm designer, out Rectangle formRect)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var canvas = (Control)typeof(GuiDesignerForm).GetField("_canvas", flags).GetValue(designer);
            formRect = (Rectangle)typeof(GuiDesignerForm)
                .GetProperty("FormRect", flags).GetValue(designer, null);

            var bmp = new Bitmap(Math.Max(1, canvas.Width), Math.Max(1, canvas.Height));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                var args = new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height));
                typeof(GuiDesignerForm).GetMethod("OnCanvasPaint", flags)
                    .Invoke(designer, new object[] { canvas, args });
            }
            return bmp;
        }

        [TestMethod]
        public void AControlTooBigForTheFormIsClippedToIt()
        {
            using (var designer = new GuiDesignerForm(ModelWithOversizedChild(), null))
            {
                designer.Width = 900;
                designer.Height = 620;
                TestWindow.ShowOffScreen(designer);
                Application.DoEvents();

                Rectangle formRect;
                using (Bitmap bmp = PaintCanvas(designer, out formRect))
                {
                    // Just outside the form's right edge, level with the oversized panel. The panel
                    // reaches 600px wide inside a 200px form, so without clipping this is the
                    // panel's own fill.
                    int probeX = Math.Min(bmp.Width - 1, formRect.Right + 12);
                    int probeY = Math.Min(bmp.Height - 1, formRect.Y + 60);
                    Color outside = bmp.GetPixel(probeX, probeY);

                    Assert.AreEqual(Color.White.ToArgb(), outside.ToArgb(),
                        "a control painted outside the form at " + probeX + "," + probeY
                        + " — got " + outside);
                }
                designer.Close();
            }
        }

        /// <summary>
        /// When content is clipped the designer says so. A control that has silently vanished is
        /// the difference between "my form is too small" and "the designer is broken" — which is
        /// exactly how this looked when the overflow was drawn out in open canvas instead.
        /// </summary>
        [TestMethod]
        public void ClippedContentIsCalledOut()
        {
            using (var designer = new GuiDesignerForm(ModelWithOversizedChild(), null))
            {
                designer.Width = 900;
                designer.Height = 620;
                TestWindow.ShowOffScreen(designer);
                Application.DoEvents();

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Rectangle formRect = (Rectangle)typeof(GuiDesignerForm)
                    .GetProperty("FormRect", flags).GetValue(designer, null);
                bool overflowing = (bool)typeof(GuiDesignerForm)
                    .GetMethod("AnyControlOverflows", flags)
                    .Invoke(designer, new object[] { formRect });

                Assert.IsTrue(overflowing,
                    "a 600px panel in a 200px form was not reported as overflowing");
                designer.Close();
            }
        }

        /// <summary>
        /// Dragging the form's corner resizes it, and nothing spills out of the frame when it does.
        /// This is the gesture that produced the broken-looking surface in the first place.
        /// </summary>
        [TestMethod]
        public void DraggingTheFormSmallerLeavesNothingOutsideIt()
        {
            var model = new GuiForm { Width = 420, Height = 320 };
            model.Controls.Add(new GuiControl
            {
                Kind = GuiControlKind.Panel, Name = "page",
                X = 8, Y = 8, Width = 380, Height = 250,
            });

            using (var designer = new GuiDesignerForm(model, null))
            {
                designer.Width = 900;
                designer.Height = 620;
                TestWindow.ShowOffScreen(designer);
                Application.DoEvents();

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Rectangle before = (Rectangle)typeof(GuiDesignerForm)
                    .GetProperty("FormRect", flags).GetValue(designer, null);

                // Grab the bottom-right corner and pull it up and to the left.
                var down = new MouseEventArgs(MouseButtons.Left, 1, before.Right - 2, before.Bottom - 2, 0);
                var move = new MouseEventArgs(MouseButtons.Left, 1, before.Right - 190, before.Bottom - 150, 0);
                typeof(GuiDesignerForm).GetMethod("OnCanvasMouseDown", flags)
                    .Invoke(designer, new object[] { null, down });
                typeof(GuiDesignerForm).GetMethod("OnCanvasMouseMove", flags)
                    .Invoke(designer, new object[] { null, move });
                Application.DoEvents();

                Assert.IsTrue(model.Width < 420 || model.Height < 320,
                    "dragging the corner did not resize the form (" + model.Width + "x" + model.Height + ")");

                Rectangle after;
                using (Bitmap bmp = PaintCanvas(designer, out after))
                {
                    int probeX = Math.Min(bmp.Width - 1, after.Right + 12);
                    int probeY = Math.Min(bmp.Height - 1, after.Y + 60);
                    Color outside = bmp.GetPixel(probeX, probeY);
                    Assert.AreEqual(Color.White.ToArgb(), outside.ToArgb(),
                        "content spilled outside the resized form at " + probeX + "," + probeY
                        + " — got " + outside);
                }
                designer.Close();
            }
        }
    }
}
