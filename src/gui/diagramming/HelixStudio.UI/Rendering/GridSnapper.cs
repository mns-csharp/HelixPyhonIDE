namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Drawing;

    /// <summary>
    /// Utility class for snapping coordinates to a grid.
    /// </summary>
    public static class GridSnapper
    {
        /// <summary>When false, SnapToGrid returns the input unchanged.</summary>
        public static bool Enabled = true;

        /// <summary>
        /// Snap a single value to the nearest grid interval.
        /// </summary>
        public static float SnapToGrid(float value, float interval)
        {
            if (!Enabled) return value;
            return (float)Math.Round(value / interval) * interval;
        }

        /// <summary>
        /// Snap a point to the nearest grid position.
        /// </summary>
        public static PointF SnapToGrid(PointF point, float interval)
        {
            if (!Enabled) return point;
            return new PointF(
                SnapToGrid(point.X, interval),
                SnapToGrid(point.Y, interval));
        }

        /// <summary>
        /// Snap a point to the default grid spacing.
        /// </summary>
        public static PointF SnapToGrid(PointF point)
        {
            if (!Enabled) return point;
            return SnapToGrid(point, LayoutConstants.GridSpacing);
        }
    }
}
