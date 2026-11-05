namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Rendering;

    /// <summary>
    /// Miniature scene overview. Renders the full scene scaled to fit, with a
    /// translucent rectangle showing the main canvas's current viewport.
    /// Clicking or dragging on the minimap pans the main canvas so the mouse
    /// point maps to the viewport center.
    /// </summary>
    public sealed class MinimapControl : Control
    {
        private DiagramCanvas _canvas;
        private bool _dragging;
        private float _lastFitScale = 1f;
        private PointF _lastFitOrigin;

        public MinimapControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.DoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            BackColor = Color.FromArgb(250, 250, 250);
            MinimumSize = new Size(120, 90);
        }

        /// <summary>Bind to a DiagramCanvas. Pass null to clear.</summary>
        public void SetCanvas(DiagramCanvas canvas)
        {
            if (_canvas != null)
            {
                _canvas.Invalidated -= OnCanvasInvalidated;
                if (_canvas.Viewport != null)
                {
                    _canvas.Viewport.ZoomChanged -= OnCanvasZoomChanged;
                }
            }

            _canvas = canvas;

            if (_canvas != null)
            {
                _canvas.Invalidated += OnCanvasInvalidated;
                if (_canvas.Viewport != null)
                {
                    _canvas.Viewport.ZoomChanged += OnCanvasZoomChanged;
                }
            }

            Invalidate();
        }

        private void OnCanvasInvalidated(object sender, InvalidateEventArgs e)
        {
            if (IsDisposed) return;
            BeginInvokeSafe(() => Invalidate());
        }

        private void OnCanvasZoomChanged()
        {
            if (IsDisposed) return;
            BeginInvokeSafe(() => Invalidate());
        }

        private void BeginInvokeSafe(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch { }
            }
            else
            {
                action();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_canvas == null || _canvas.SceneGraph == null) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            RectangleF content = _canvas.SceneGraph.ComputeContentBounds();
            if (content.IsEmpty || content.Width <= 0 || content.Height <= 0) return;

            float padding = 6f;
            float availableW = Width - padding * 2;
            float availableH = Height - padding * 2;
            if (availableW <= 0 || availableH <= 0) return;

            float scale = Math.Min(availableW / content.Width, availableH / content.Height);
            _lastFitScale = scale;
            _lastFitOrigin = new PointF(
                padding + (availableW - content.Width * scale) / 2f - content.X * scale,
                padding + (availableH - content.Height * scale) / 2f - content.Y * scale);

            Matrix saved = g.Transform.Clone();
            try
            {
                Matrix m = new Matrix();
                m.Translate(_lastFitOrigin.X, _lastFitOrigin.Y);
                m.Scale(scale, scale);
                g.Transform = m;

                // Draw simplified node rectangles.
                foreach (var node in _canvas.SceneGraph.Nodes)
                {
                    if (node == null || node.Style == null) continue;
                    RectangleF b = node.WorldBounds;
                    NodeStyleInfo style = node.Style.GetStyle(false);
                    if (node.IsCircleMode)
                    {
                        using (Brush br = new SolidBrush(style.HeaderFillColor))
                        using (Pen pen = new Pen(style.BorderColor, 1f))
                        {
                            g.FillEllipse(br, b);
                            g.DrawEllipse(pen, b);
                        }
                    }
                    else
                    {
                        using (Brush br = new SolidBrush(style.BodyFillColor))
                        using (Pen pen = new Pen(style.BorderColor, 1f))
                        {
                            g.FillRectangle(br, b);
                            g.DrawRectangle(pen, b.X, b.Y, b.Width, b.Height);
                        }
                    }
                }

                // Draw edge lines (light strokes — detail is lost at this scale).
                foreach (var edge in _canvas.SceneGraph.Edges)
                {
                    if (edge == null || edge.Style == null) continue;
                    EdgeStyleInfo style = edge.Style.GetStyle(false);
                    using (Pen pen = new Pen(style.LineColor, 0.5f))
                    {
                        PointF prev = edge.StartPoint;
                        foreach (var p in edge.RoutePoints)
                        {
                            g.DrawLine(pen, prev, p);
                            prev = p;
                        }
                        g.DrawLine(pen, prev, edge.EndPoint);
                    }
                }
            }
            finally
            {
                g.Transform = saved;
            }

            // Overlay the main canvas viewport rectangle.
            RectangleF visibleWorld = _canvas.Viewport.GetVisibleWorldRect();
            RectangleF overlay = new RectangleF(
                _lastFitOrigin.X + visibleWorld.X * scale,
                _lastFitOrigin.Y + visibleWorld.Y * scale,
                visibleWorld.Width * scale,
                visibleWorld.Height * scale);

            using (Brush overlayFill = new SolidBrush(Color.FromArgb(40, 0, 120, 215)))
            using (Pen overlayBorder = new Pen(Color.FromArgb(0, 120, 215), 1.5f))
            {
                g.FillRectangle(overlayFill, overlay);
                g.DrawRectangle(overlayBorder, overlay.X, overlay.Y, overlay.Width, overlay.Height);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                PanCanvasToMouse(e.Location);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                PanCanvasToMouse(e.Location);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        private void PanCanvasToMouse(Point screen)
        {
            if (_canvas == null || _lastFitScale <= 0f) return;

            // Convert minimap point → world point.
            float worldX = (screen.X - _lastFitOrigin.X) / _lastFitScale;
            float worldY = (screen.Y - _lastFitOrigin.Y) / _lastFitScale;

            var vp = _canvas.Viewport;
            float zoom = vp.Zoom;
            float screenCx = vp.ViewportSize.Width / 2f;
            float screenCy = vp.ViewportSize.Height / 2f;

            vp.SetPanOffset(
                screenCx - worldX * zoom,
                screenCy - worldY * zoom);
            _canvas.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SetCanvas(null);
            }
            base.Dispose(disposing);
        }
    }
}
