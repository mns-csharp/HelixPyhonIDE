namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;

    /// <summary>
    /// Renders the entire scene to an off-screen bitmap, independent of the live canvas.
    /// Used for PNG export and clipboard-as-image.
    /// </summary>
    public static class CanvasImageRenderer
    {
        private const float Padding = 24f;

        /// <summary>
        /// Render the scene at the given pixel scale, sized to fit all content plus a margin.
        /// </summary>
        /// <param name="scene">Scene to render.</param>
        /// <param name="scale">Pixel scale (1 = 1 world unit per px, 2 = crisp 2x for print).</param>
        /// <param name="background">Background fill colour.</param>
        /// <returns>A Bitmap sized to fit the scene; caller owns the bitmap.</returns>
        public static Bitmap Render(SceneGraph scene, float scale, Color background)
        {
            if (scene == null) throw new ArgumentNullException("scene");
            if (scale <= 0f) scale = 1f;

            RectangleF contentBounds = scene.ComputeContentBounds();
            if (contentBounds.IsEmpty || contentBounds.Width <= 0 || contentBounds.Height <= 0)
            {
                // Empty scene — return a small placeholder so callers don't crash.
                Bitmap empty = new Bitmap(1, 1);
                using (Graphics g = Graphics.FromImage(empty)) g.Clear(background);
                return empty;
            }

            int widthPx = (int)Math.Ceiling((contentBounds.Width + Padding * 2) * scale);
            int heightPx = (int)Math.Ceiling((contentBounds.Height + Padding * 2) * scale);
            if (widthPx < 1) widthPx = 1;
            if (heightPx < 1) heightPx = 1;

            Bitmap bmp = new Bitmap(widthPx, heightPx);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(background);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    ViewportTransform vp = new ViewportTransform();
                    vp.ViewportSize = new SizeF(widthPx, heightPx);
                    vp.SetZoom(scale, PointF.Empty);
                    vp.SetPanOffset(-contentBounds.X * scale + Padding * scale,
                                    -contentBounds.Y * scale + Padding * scale);

                    scene.RenderAll(g, vp);
                }
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
            return bmp;
        }
    }
}
