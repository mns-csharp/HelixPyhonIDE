using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixTextEditorNET.Tests
{
    [TestClass]
    public class AutoCFontLifetimeTests
    {
        [TestMethod]
        public void PopupKeepsDrawingAfterTheEditorsBorrowedFontIsDisposed()
        {
            using (var borrowed = new Font("Consolas", 10f))
            using (var popup = new AutoCListWindow(new Dictionary<int, Bitmap>()))
            {
                popup.SetItems("class?0 clear?0", ' ', '?', true);
                Assert.IsTrue(popup.Filter("cl", false));
                popup.SizeToContent(12, 0, borrowed);

                // GdiSurface.Reset does this while a completion popup can still be visible.
                borrowed.Dispose();

                popup.CreateControl();
                using (var image = new Bitmap(popup.Width, popup.Height))
                    popup.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            }
        }

        [TestMethod]
        public void QueuedOwnerDrawDoesNotUseItsStaleDisposedEventFont()
        {
            using (var popup = new AutoCListWindow(new Dictionary<int, Bitmap>()))
            using (var source = new Font("Consolas", 10f))
            using (var stale = new Font("Consolas", 10f))
            using (var image = new Bitmap(300, 30))
            using (Graphics graphics = Graphics.FromImage(image))
            {
                popup.SetItems("import?0", ' ', '?', true);
                Assert.IsTrue(popup.Filter("im", false));
                popup.SizeToContent(12, 0, source);
                stale.Dispose();

                var args = new DrawItemEventArgs(graphics, stale, new Rectangle(0, 0, 300, 20),
                    0, DrawItemState.Selected);
                MethodInfo draw = typeof(AutoCListWindow).GetMethod("OnDrawItem",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(draw);
                draw.Invoke(popup, new object[] { popup, args });
            }
        }
    }
}
