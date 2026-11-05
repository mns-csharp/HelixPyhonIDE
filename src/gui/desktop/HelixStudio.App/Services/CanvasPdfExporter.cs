namespace HelixPythonNamespace.App.Services
{
    using System;
    using System.Drawing;
    using HelixPythonNamespace.UI.Rendering;
    using PdfSharp.Drawing;
    using PdfSharp.Pdf;

    /// <summary>
    /// Writes the scene graph into a single-page PDF by rasterizing it at 2× scale
    /// via CanvasImageRenderer and embedding the bitmap at page-fill size.
    /// Vector-native PDF drawing would require porting scene rendering onto XGraphics;
    /// that's a future enhancement.
    /// </summary>
    public static class CanvasPdfExporter
    {
        public static void Export(SceneGraph scene, string path)
        {
            // Guard against missing inputs before doing any work.
            if (scene == null) throw new ArgumentNullException("scene");
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required");

            // Rasterize the scene to a white-background bitmap at 2x for print sharpness.
            using (Bitmap bmp = CanvasImageRenderer.Render(scene, 2f, Color.White))
            {
                // Persist the bitmap to a temp PNG since PdfSharp 1.50 reliably
                // loads XImage from file/stream — its direct-GDI overload is
                // not exposed in every build variant.
                string tempPng = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "csooptool_pdf_" + Guid.NewGuid().ToString("N") + ".png");

                try
                {
                    // Write the raster to the temp PNG that XImage will load from.
                    bmp.Save(tempPng, System.Drawing.Imaging.ImageFormat.Png);

                    // Build a single-page document sized to hold the whole image.
                    using (PdfDocument document = new PdfDocument())
                    {
                        PdfPage page = document.AddPage();

                        // Page in points (PDF unit). At 2× raster scale, 1 pt = 2 px,
                        // so divide by 2 to get correct print size.
                        double pageWidthPt = bmp.Width / 2.0;
                        double pageHeightPt = bmp.Height / 2.0;
                        // Enforce a 1-inch (72pt) minimum so tiny scenes still yield a valid page.
                        if (pageWidthPt < 72) pageWidthPt = 72;
                        if (pageHeightPt < 72) pageHeightPt = 72;
                        page.Width = pageWidthPt;
                        page.Height = pageHeightPt;

                        // Draw the PNG to fill the whole page, then write the PDF to disk.
                        using (XGraphics gfx = XGraphics.FromPdfPage(page))
                        using (XImage img = XImage.FromFile(tempPng))
                        {
                            gfx.DrawImage(img, 0, 0, page.Width, page.Height);
                        }

                        document.Save(path);
                    }
                }
                // Always clean up the temp PNG, whether or not the PDF save succeeded.
                finally
                {
                    try { if (System.IO.File.Exists(tempPng)) System.IO.File.Delete(tempPng); }
                    catch { /* best effort */ }
                }
            }
        }
    }
}
