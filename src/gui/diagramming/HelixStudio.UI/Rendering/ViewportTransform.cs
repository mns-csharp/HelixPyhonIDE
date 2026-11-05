namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Drawing;

    public sealed class ViewportTransform
    {
        public const float MinZoom = 0.1f;
        public const float MaxZoom = 5.0f;

        private PointF _panOffset;
        private float _zoom;
        private SizeF _viewportSize;

        public ViewportTransform()
        {
            _panOffset = PointF.Empty;
            _zoom = 1.0f;
            _viewportSize = SizeF.Empty;
        }

        /// <summary>Fires whenever the zoom level changes.</summary>
        public event Action ZoomChanged;

        public float Zoom
        {
            get { return _zoom; }
        }

        private void RaiseZoomChanged()
        {
            Action handler = ZoomChanged;
            if (handler != null) handler();
        }

        public PointF PanOffset
        {
            get { return _panOffset; }
        }

        public SizeF ViewportSize
        {
            get { return _viewportSize; }
            set { _viewportSize = value; }
        }

        public void SetZoom(float zoom, PointF screenAnchor)
        {
            float clampedZoom = zoom;
            if (clampedZoom < MinZoom)
            {
                clampedZoom = MinZoom;
            }
            if (clampedZoom > MaxZoom)
            {
                clampedZoom = MaxZoom;
            }

            // Find the world point under the screen anchor before zooming
            PointF worldAnchor = ScreenToWorld(screenAnchor);

            bool changed = _zoom != clampedZoom;
            _zoom = clampedZoom;

            // Adjust pan so that the same world point stays under the cursor
            _panOffset = new PointF(
                screenAnchor.X - worldAnchor.X * _zoom,
                screenAnchor.Y - worldAnchor.Y * _zoom);

            if (changed) RaiseZoomChanged();
        }

        public void Pan(float screenDx, float screenDy)
        {
            _panOffset = new PointF(
                _panOffset.X + screenDx,
                _panOffset.Y + screenDy);
        }

        public void SetPanOffset(float x, float y)
        {
            _panOffset = new PointF(x, y);
        }

        public PointF WorldToScreen(PointF world)
        {
            return new PointF(
                world.X * _zoom + _panOffset.X,
                world.Y * _zoom + _panOffset.Y);
        }

        public RectangleF WorldToScreen(RectangleF world)
        {
            PointF topLeft = WorldToScreen(new PointF(world.X, world.Y));
            return new RectangleF(
                topLeft.X,
                topLeft.Y,
                world.Width * _zoom,
                world.Height * _zoom);
        }

        public PointF ScreenToWorld(PointF screen)
        {
            return new PointF(
                (screen.X - _panOffset.X) / _zoom,
                (screen.Y - _panOffset.Y) / _zoom);
        }

        public RectangleF GetVisibleWorldRect()
        {
            PointF topLeft = ScreenToWorld(new PointF(0, 0));
            PointF bottomRight = ScreenToWorld(new PointF(_viewportSize.Width, _viewportSize.Height));
            return new RectangleF(
                topLeft.X,
                topLeft.Y,
                bottomRight.X - topLeft.X,
                bottomRight.Y - topLeft.Y);
        }

        /// <summary>
        /// Adjust zoom and pan to fit the specified world bounds within the viewport.
        /// </summary>
        /// <param name="worldBounds">The world-space bounds to fit.</param>
        /// <param name="padding">Padding in screen pixels around the content.</param>
        public void ZoomToFit(RectangleF worldBounds, float padding)
        {
            ZoomToFit(worldBounds, padding, float.MaxValue);
        }

        /// <summary>
        /// Fit content to the viewport, centred. <paramref name="maxZoom"/> caps how far the fit may
        /// zoom IN: pass 1.0 for an on-load fit so a sparse diagram (one small node) isn't blown up to
        /// fill the window — it stays at natural size, centred. The default lets the fit enlarge freely
        /// (up to the viewport's own MaxZoom), which is what the manual "Zoom to Fit" command wants.
        /// </summary>
        public void ZoomToFit(RectangleF worldBounds, float padding, float maxZoom)
        {
            ZoomToFit(worldBounds, padding, maxZoom, MinZoom);
        }

        /// <summary>
        /// Fit like the three-argument overload, but never zoom out below
        /// <paramref name="minReadableZoom"/>. When the content is too large to fit above that floor
        /// — e.g. drilling into a project with hundreds of namespace boxes — the view stops at the
        /// floor and anchors the content's top-left corner (padded) so the user starts reading at
        /// the first row and scrolls, instead of centring a postage-stamp grid nobody can read.
        /// The manual Zoom-to-Fit command passes the default floor (<see cref="MinZoom"/>) so it can
        /// still show everything at once.
        /// </summary>
        public void ZoomToFit(RectangleF worldBounds, float padding, float maxZoom, float minReadableZoom)
        {
            if (_viewportSize.Width <= 0 || _viewportSize.Height <= 0)
            {
                return;
            }

            if (worldBounds.Width <= 0 || worldBounds.Height <= 0)
            {
                return;
            }

            // Calculate available size after padding
            float availableWidth = _viewportSize.Width - padding * 2;
            float availableHeight = _viewportSize.Height - padding * 2;

            if (availableWidth <= 0 || availableHeight <= 0)
            {
                return;
            }

            // Calculate zoom to fit both dimensions
            float zoomX = availableWidth / worldBounds.Width;
            float zoomY = availableHeight / worldBounds.Height;
            float fitZoom = System.Math.Min(zoomX, zoomY);
            float newZoom = fitZoom;

            // Caller cap first (e.g. 1.0 = never enlarge sparse content on load), then the readable
            // floor, then the viewport's own hard limits.
            if (newZoom > maxZoom)
            {
                newZoom = maxZoom;
            }
            if (newZoom < minReadableZoom)
            {
                newZoom = minReadableZoom;
            }
            if (newZoom < MinZoom)
            {
                newZoom = MinZoom;
            }
            if (newZoom > MaxZoom)
            {
                newZoom = MaxZoom;
            }

            bool changed = _zoom != newZoom;
            _zoom = newZoom;

            float contentScreenWidth = worldBounds.Width * _zoom;
            float contentScreenHeight = worldBounds.Height * _zoom;

            float offsetX, offsetY;
            // If the readable floor kept us from fitting everything (content is wider/taller than the
            // viewport at this zoom), anchor the top-left corner with padding rather than centring —
            // centring would push the first rows and columns off-screen. Otherwise centre as usual.
            bool floored = newZoom > fitZoom + 0.0001f;
            if (floored && (contentScreenWidth > _viewportSize.Width || contentScreenHeight > _viewportSize.Height))
            {
                offsetX = padding - worldBounds.X * _zoom;
                offsetY = padding - worldBounds.Y * _zoom;
            }
            else
            {
                offsetX = (_viewportSize.Width - contentScreenWidth) / 2 - worldBounds.X * _zoom;
                offsetY = (_viewportSize.Height - contentScreenHeight) / 2 - worldBounds.Y * _zoom;
            }

            _panOffset = new PointF(offsetX, offsetY);

            if (changed) RaiseZoomChanged();
        }

        /// <summary>
        /// Reset to default view (zoom 1.0, no pan).
        /// </summary>
        public void Reset()
        {
            bool changed = _zoom != 1.0f;
            _zoom = 1.0f;
            _panOffset = PointF.Empty;
            if (changed) RaiseZoomChanged();
        }
    }
}
