namespace HelixPythonNamespace.UI.Rendering
{
    using System.Drawing;

    public sealed class GridVisual : IRenderable
    {
        private const float MinLinePixelSpacing = 4f;

        private readonly Color _gridColor;
        private Pen _cachedGridPen;

        public GridVisual(Color gridColor)
        {
            _gridColor = gridColor;
        }

        // Kept for source compatibility with old call-sites that still pass a spacing arg.
        // The value is ignored — grid spacing is read from LayoutConstants.GridSpacing.
        public GridVisual(float unusedSpacing, Color gridColor)
            : this(gridColor)
        {
        }

        public int ZOrder
        {
            get { return ZOrders.Background; }
        }

        public void Render(Graphics g, ViewportTransform viewport)
        {
            if (g == null || viewport == null)
            {
                return;
            }

            float gridSpacing = LayoutConstants.GridSpacing;
            if (gridSpacing <= 0f)
            {
                return;
            }

            // R1.7: only draw lines within the visible viewport
            RectangleF visibleWorld = viewport.GetVisibleWorldRect();

            // Adaptive thinning: when the grid would render as a solid grey block,
            // skip lines by a power-of-two stride until the visible spacing is >= MinLinePixelSpacing.
            float zoom = viewport.Zoom;
            int stride = 1;
            while (gridSpacing * zoom * stride < MinLinePixelSpacing)
            {
                stride *= 2;
                if (stride > 1024) break;
            }
            float step = gridSpacing * stride;

            float startX = (float)System.Math.Floor(visibleWorld.Left / step) * step;
            float startY = (float)System.Math.Floor(visibleWorld.Top / step) * step;
            float endX = visibleWorld.Right;
            float endY = visibleWorld.Bottom;

            if (_cachedGridPen == null)
            {
                _cachedGridPen = new Pen(_gridColor, 1f);
            }

            for (float wx = startX; wx <= endX; wx += step)
            {
                PointF top = viewport.WorldToScreen(new PointF(wx, visibleWorld.Top));
                PointF bottom = viewport.WorldToScreen(new PointF(wx, visibleWorld.Bottom));
                g.DrawLine(_cachedGridPen, top, bottom);
            }

            for (float wy = startY; wy <= endY; wy += step)
            {
                PointF left = viewport.WorldToScreen(new PointF(visibleWorld.Left, wy));
                PointF right = viewport.WorldToScreen(new PointF(visibleWorld.Right, wy));
                g.DrawLine(_cachedGridPen, left, right);
            }
        }
    }
}
