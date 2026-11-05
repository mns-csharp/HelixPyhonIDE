using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.Tests
{
    // LunaForm hand-draws the XP caption in OnPaint. If that throws, WinForms latches
    // STATE_EXCEPTIONWHILEPAINTING and paints the red-X placeholder over the window forever —
    // i.e. "the skin doesn't load", plus the global handler pops a crash dialog on launch.
    // These tests paint the real caption and assert it both survives and produces pixels.
    [TestClass]
    public class LunaFormCaptionPaintTests
    {
        private static void Paint(Form f, Bitmap target)
        {
            using (Graphics g = Graphics.FromImage(target))
            {
                MethodInfo onPaint = typeof(LunaForm).GetMethod(
                    "OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(onPaint, "LunaForm.OnPaint not found");
                try
                {
                    onPaint.Invoke(f, new object[]
                    {
                        new PaintEventArgs(g, new Rectangle(0, 0, target.Width, target.Height))
                    });
                }
                catch (TargetInvocationException ex)
                {
                    throw new AssertFailedException(
                        "LunaForm.OnPaint threw: " + ex.InnerException, ex.InnerException);
                }
            }
        }

        [TestMethod]
        public void OnPaint_DrawsTheCaption_WithoutThrowing()
        {
            using (var f = new LunaForm())
            using (var bmp = new Bitmap(900, 600))
            {
                f.Size = new Size(900, 600);
                f.Text = "HelixStudio";
                Paint(f, bmp);

                // The caption band must actually be filled — an unpainted bitmap stays fully
                // transparent, which is what the pre-fix crash left behind.
                Color mid = bmp.GetPixel(450, 8);
                Assert.AreNotEqual(0, mid.A, "caption band was not painted");
            }
        }

        [TestMethod]
        public void OnPaint_OnAnEmptyClientArea_IsANoOp()
        {
            // Minimize / early layout can hand OnPaint a zero-sized client area; every gradient
            // brush would throw on an empty rectangle and poison the form permanently.
            using (var f = new LunaForm())
            using (var bmp = new Bitmap(10, 10))
            {
                f.Size = new Size(0, 0);
                Paint(f, bmp);
            }
        }
    }
}
