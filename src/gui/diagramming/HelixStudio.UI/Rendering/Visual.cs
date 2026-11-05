namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Drawing;

    public abstract class Visual : IRenderable, IHitTestable, IDisposable
    {
        private RectangleF _worldBounds;
        private int _zOrder;
        private bool _isDirty;

        protected Visual(RectangleF worldBounds, int zOrder)
        {
            _worldBounds = worldBounds;
            _zOrder = zOrder;
            _isDirty = true;
        }

        public RectangleF WorldBounds
        {
            get { return _worldBounds; }
            protected set
            {
                _worldBounds = value;
                _isDirty = true;
            }
        }

        public int ZOrder
        {
            get { return _zOrder; }
            set { _zOrder = value; }
        }

        public bool IsDirty
        {
            get { return _isDirty; }
            protected set { _isDirty = value; }
        }

        public abstract void Render(Graphics g, ViewportTransform viewport);

        public virtual bool HitTest(PointF worldPoint, float tolerance)
        {
            RectangleF inflated = RectangleF.Inflate(_worldBounds, tolerance, tolerance);
            return inflated.Contains(worldPoint);
        }

        public virtual RectangleF GetWorldBounds()
        {
            return _worldBounds;
        }

        /// <summary>
        /// Disposes instance-level resources. Subclasses should override to clean up
        /// any per-instance GDI+ objects. Static cached resources (fonts, brushes, pens)
        /// shared across all instances are intentionally not disposed here as they
        /// live for the application lifetime to avoid repeated allocation.
        /// </summary>
        public virtual void Dispose()
        {
        }
    }
}
