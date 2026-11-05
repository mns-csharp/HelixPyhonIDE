namespace HelixPythonNamespace.UI.Rendering
{
    using System;

    /// <summary>
    /// Centralized layout constants for consistent node sizing and grid alignment.
    /// </summary>
    public static class LayoutConstants
    {
        // Fixed node dimensions
        public const float NodeWidth = 200f;
        public const float NodeHeight = 150f;

        // Circle node dimensions (for secondary types on filtered tabs)
        public const float CircleDiameter = 60f;

        // Grid snapping interval — mutable at runtime so users can tune snap fineness.
        private static float _gridSpacing = 10f;

        public static event Action GridSpacingChanged;

        public static float GridSpacing
        {
            get { return _gridSpacing; }
            set
            {
                float clamped = value;
                if (clamped < 2f) clamped = 2f;
                if (clamped > 200f) clamped = 200f;
                if (_gridSpacing == clamped) return;
                _gridSpacing = clamped;
                Action handler = GridSpacingChanged;
                if (handler != null) handler();
            }
        }

        // Auto-layout spacing
        public const float XSpacing = 250f;
        public const float YSpacing = 200f;
        public const float StartX = 50f;
        public const float StartY = 50f;

        // Vertical gap between type-category bands (Interface / Abstract / Concrete)
        public const float BandGap = 100f;

        /// <summary>
        /// Calculate columns for a matrix-shaped cluster: N=1→1x1, N=2→1x2, N=3-4→2x2,
        /// N=5-6→3x2, N=7-8→4x2, N=9→3x3, N=10-12→4x3, N=13→5x3, etc. (rows x cols).
        ///
        /// Column count is <c>floor(sqrt(N))</c>, bumped up by one once that many columns
        /// can no longer hold N items within twice as many rows (<c>N &gt; 2*cols^2</c>) — i.e.
        /// rows are allowed to grow up to double the column count before the cluster widens.
        /// N=2 is the one case that formula alone doesn't cover (it would give a tall 2x1
        /// instead of the desired single 1x2 row), so it's called out explicitly.
        /// </summary>
        public static int CalculateColumnsForSquareGrid(int itemCount)
        {
            if (itemCount <= 0)
            {
                return 1;
            }
            if (itemCount == 2)
            {
                return 2;
            }
            int columns = (int)Math.Floor(Math.Sqrt(itemCount));
            if (columns < 1) columns = 1;
            if (itemCount > 2 * columns * columns) columns++;
            return columns;
        }
    }
}
