using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.GuiDesigner.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The form drawn on the canvas wears the toolkit's own mark.
    ///
    /// <para>A Tk window wears the Tk quill on its title bar when it runs. The designer drew a small
    /// white box with the red letters "Tk" in it instead — a stand-in for a logo we do in fact have.
    /// The simulated caption is there to look like what the user is going to get, so it shows the
    /// real thing.</para>
    ///
    /// <para>Also pinned here: the yellow "Not yet linked …" band that used to sit over the canvas
    /// from the moment the designer opened. It warned about a write nobody had asked for; the
    /// question is put at the first edit instead (see <see cref="GuiDesignerTakeOverTests"/>), and
    /// until there is something to report the bar takes no space.</para>
    /// </summary>
    [TestClass]
    public class GuiDesignerCaptionLogoTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static string AssetIcoPath()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int hop = 0; dir != null && hop < 10; hop++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "assets", "product", "app", "gui_designer.ico");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static Bitmap PaintChrome(GuiDesignerForm designer, Rectangle formRect)
        {
            var bmp = new Bitmap(formRect.Right + 8, formRect.Bottom + 8);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                typeof(GuiDesignerForm).GetMethod("DrawFormChrome", Private)
                    .Invoke(designer, new object[] { g, formRect });
            }
            return bmp;
        }

        // The 16x16 seat the caption gives a toolkit logo: 5px in from the frame, centred.
        private static Rectangle LogoSeat(Rectangle formRect)
        {
            const int titleBar = 22;
            var caption = new Rectangle(formRect.X + 1, formRect.Y + 1, formRect.Width - 1, titleBar - 1);
            return new Rectangle(caption.X + 5, caption.Y + (caption.Height - 16) / 2, 16, 16);
        }

        [TestMethod]
        public void TheSimulatedCaptionWearsTheTkQuill()
        {
            string path = AssetIcoPath();
            Assert.IsNotNull(path, "assets/product/app/gui_designer.ico is missing from the repository");

            var model = new GuiForm
            {
                FormClassName = "MainWindow", Title = "demo",
                Width = 320, Height = 200, Framework = GuiFramework.Tkinter,
            };
            using (var designer = new GuiDesignerForm(model, null))
            {
                var formRect = new Rectangle(20, 20, 320, 200);
                using (Bitmap painted = PaintChrome(designer, formRect))
                {
                    Rectangle seat = LogoSeat(formRect);

                    // What the checked-in artwork looks like drawn into that seat over the
                    // caption's own colour. Anything else in the seat — a white box with letters,
                    // say — is a different picture, which is the whole point of measuring.
                    using (var icon = new Icon(path, 16, 16))
                    using (Bitmap frame = icon.ToBitmap())
                    using (var expected = new Bitmap(seat.Width, seat.Height))
                    {
                        using (Graphics g = Graphics.FromImage(expected))
                        {
                            g.Clear(painted.GetPixel(seat.Right + 3, seat.Y + 8));   // caption face
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(frame, new Rectangle(0, 0, seat.Width, seat.Height));
                        }

                        int off = 0;
                        for (int y = 0; y < seat.Height; y++)
                        {
                            for (int x = 0; x < seat.Width; x++)
                            {
                                Color a = expected.GetPixel(x, y);
                                Color b = painted.GetPixel(seat.X + x, seat.Y + y);
                                if (Math.Abs(a.R - b.R) > 6 || Math.Abs(a.G - b.G) > 6
                                    || Math.Abs(a.B - b.B) > 6) off++;
                            }
                        }
                        Assert.IsTrue(off <= 8,
                            off + " of 256 pixels in the caption's icon seat do not match the Tk "
                            + "quill under assets/ — the caption is drawing something else.");
                    }
                }
            }
        }

        /// <summary>A toolkit we ship no logo for still says which one it is.</summary>
        [TestMethod]
        public void AToolkitWithNoLogoKeepsItsLetteredBadge()
        {
            var model = new GuiForm
            {
                FormClassName = "MainWindow", Title = "demo",
                Width = 320, Height = 200, Framework = GuiFramework.PyQt6,
            };
            using (var designer = new GuiDesignerForm(model, null))
            {
                object logo = typeof(GuiDesignerForm).GetMethod("FrameworkLogo", Private)
                    .Invoke(designer, null);
                Assert.IsNull(logo, "a Qt form was given the Tk feather");

                var formRect = new Rectangle(20, 20, 320, 200);
                using (Bitmap painted = PaintChrome(designer, formRect))
                {
                    Rectangle seat = LogoSeat(formRect);
                    bool anyWhite = false;
                    for (int y = 0; y < seat.Height && !anyWhite; y++)
                    {
                        for (int x = 0; x < seat.Width && !anyWhite; x++)
                        {
                            Color c = painted.GetPixel(seat.X + x, seat.Y + y);
                            anyWhite = c.R > 240 && c.G > 240 && c.B > 240;
                        }
                    }
                    Assert.IsTrue(anyWhite, "the lettered badge is gone from a Qt form's caption");
                }
            }
        }

        [TestMethod]
        public void NothingOnTheCanvasSaysNotYetLinked()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-banner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "main.py");
            File.WriteAllText(path, "class MainWindow:" + Environment.NewLine + "    pass" + Environment.NewLine);
            try
            {
                using (var designer = new GuiDesignerForm(new GuiForm { FormClassName = "MainWindow" }, path))
                {
                    var bar = (Panel)typeof(GuiDesignerForm).GetField("_syncBar", Private).GetValue(designer);
                    Assert.IsFalse(bar.Visible, "the sync bar is showing before there is anything to sync");

                    const string gone = "Not yet linked";
                    foreach (Control control in Tree(designer))
                    {
                        string text = control.Text ?? string.Empty;
                        Assert.IsFalse(text.IndexOf(gone, StringComparison.OrdinalIgnoreCase) >= 0,
                            gone + " is still written on a " + control.GetType().Name);
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static IEnumerable<Control> Tree(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls)
                foreach (Control found in Tree(child))
                    yield return found;
        }
    }
}
