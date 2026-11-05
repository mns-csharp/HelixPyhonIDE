using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using HelixPythonNamespace.App;
using HelixPythonNamespace.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The GUI Designer Canvas wears its own mark — the quill — rather than the shell's tile.
    ///
    /// <para>Every LunaForm is handed the application icon by its base constructor, so "the designer
    /// has an icon" is true whatever happens and proves nothing. These tests check the thing that
    /// can actually break: that the artwork checked in under <c>assets/</c> is what a window ends
    /// up carrying. An icon resolved by name from an assembly manifest fails SILENTLY — that is how
    /// every window in this product once lost its icon without a single error — so the failure this
    /// pins is a quiet one by nature.</para>
    /// </summary>
    [TestClass]
    public class GuiDesignerIconTests
    {
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

        private static string SourceArtworkPath()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int hop = 0; dir != null && hop < 10; hop++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "assets", "source", "sheets", "ide",
                                                "gui-designer-feather-source.png");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        [TestMethod]
        public void TheDesignerDoesNotWearTheShellsIcon()
        {
            using (var designer = new GuiDesignerForm())
            {
                Assert.IsNotNull(designer.Icon, "the designer has no icon at all");
                Assert.AreNotSame(LunaForm.GetAppIcon(), designer.Icon,
                    "the designer is still wearing the application icon its base class hands out");
            }
        }

        /// <summary>
        /// The window's icon IS the checked-in file, pixel for pixel. Drawn artwork that merely
        /// resembles it would pass a "has an icon" test and is exactly what this rules out.
        /// </summary>
        [TestMethod]
        public void TheIconIsTheArtworkUnderAssets()
        {
            string path = AssetIcoPath();
            Assert.IsNotNull(path, "assets/product/app/gui_designer.ico is missing from the repository");

            using (var designer = new GuiDesignerForm())
            using (var onDisk = new Icon(path, 32, 32))
            using (var expected = new Icon(onDisk, 32, 32))
            using (var actual = new Icon(designer.Icon, 32, 32))
            using (Bitmap a = expected.ToBitmap())
            using (Bitmap b = actual.ToBitmap())
            {
                Assert.AreEqual(a.Size, b.Size, "the 32px frame is a different size");
                int differing = 0;
                for (int y = 0; y < a.Height; y++)
                    for (int x = 0; x < a.Width; x++)
                        if (a.GetPixel(x, y) != b.GetPixel(x, y)) differing++;
                Assert.AreEqual(0, differing,
                    differing + " of " + (a.Width * a.Height) + " pixels differ from the asset — the "
                    + "window is not showing the file under assets/");
            }
        }

        /// <summary>
        /// The frames DECODE, through the same System.Drawing path the product paints with.
        ///
        /// <para>This is not pedantry about file format. The .ico was first written with frames
        /// stored as embedded PNGs — which Explorer renders happily and
        /// <see cref="Icon.ToBitmap"/> turns into a field of random pixels. Nothing threw; the
        /// caption simply painted noise. A test that compares the window against the file would
        /// have passed, because both sides were the same garbage, so what is measured here is the
        /// decoded frame against the ARTWORK it was made from.</para>
        /// </summary>
        [TestMethod]
        public void TheFramesDecodeToTheArtworkAndNotToNoise()
        {
            string ico = AssetIcoPath();
            string art = SourceArtworkPath();
            Assert.IsNotNull(ico);
            Assert.IsNotNull(art, "assets/source/sheets/ide/gui-designer-feather-source.png is missing");

            using (var icon = new Icon(ico, 32, 32))
            using (Bitmap frame = icon.ToBitmap())
            using (Image source = Image.FromFile(art))
            using (var expected = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(expected))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(source, new Rectangle(0, 0, 32, 32));
                }

                // Composited the same way, then compared as a whole. Two resamplers disagreeing
                // (the file is built with Lanczos, GDI+ reduces the artwork with bicubic) costs
                // about 12 levels per channel on artwork this contrasty; the PNG-framed file this
                // replaced measured 30 where it decoded at all, and threw outright where it did
                // not. The bar sits between them.
                double total = 0;
                using (var actual = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(actual))
                    {
                        g.Clear(Color.White);
                        g.DrawImage(frame, new Rectangle(0, 0, 32, 32));
                    }
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            Color a = expected.GetPixel(x, y);
                            Color b = actual.GetPixel(x, y);
                            total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                        }
                }

                double perChannel = total / (32 * 32 * 3);
                Assert.IsTrue(perChannel < 20,
                    "the 32px frame averages " + perChannel.ToString("0.0") + " levels per channel "
                    + "away from the artwork it was built from — it is not decoding to the feather.");
            }
        }

        /// <summary>
        /// Small frames are built into the file rather than left to be squeezed out of the 256 at
        /// paint time — a caption asks for 18-24px, and a box-filtered reduction of a 256px feather
        /// is the grey smudge this avoids.
        /// </summary>
        [TestMethod]
        public void TheIconCarriesTheSizesACaptionAsksFor()
        {
            string path = AssetIcoPath();
            Assert.IsNotNull(path);
            foreach (int size in new[] { 16, 24, 32, 48 })
                using (var frame = new Icon(path, size, size))
                    Assert.AreEqual(new Size(size, size), frame.Size,
                        "the .ico has no " + size + "px frame, so that size is a reduction of a bigger one");
        }
    }
}
