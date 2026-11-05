namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;

    public sealed class EdgeVisual : Visual, ISelectable
    {
        // --- Static cached GDI+ resources (R1.3) ---
        private static readonly Pen s_selectionHighlightPen = new Pen(Color.FromArgb(80, 0, 120, 215), 1f);
        private static readonly SolidBrush s_hollowTriangleFillBrush = new SolidBrush(Color.White);

        private readonly IEdgeStyleStrategy _styleStrategy;
        private readonly string _edgeId;
        private readonly string _sourceNodeId;
        private readonly string _targetNodeId;
        private bool _isSelected;

        // Route data
        private PointF _startPoint;
        private PointF _endPoint;
        private PointF[] _routePoints;

        // Crossing bridge arcs: segment index -> list of crossing world-points
        private Dictionary<int, List<PointF>> _crossings;

        public EdgeVisual(string edgeId, string sourceNodeId, string targetNodeId, IEdgeStyleStrategy styleStrategy)
            : base(RectangleF.Empty, ZOrders.Edge)
        {
            if (edgeId == null)
            {
                throw new ArgumentNullException("edgeId");
            }
            if (sourceNodeId == null)
            {
                throw new ArgumentNullException("sourceNodeId");
            }
            if (targetNodeId == null)
            {
                throw new ArgumentNullException("targetNodeId");
            }
            if (styleStrategy == null)
            {
                throw new ArgumentNullException("styleStrategy");
            }

            _edgeId = edgeId;
            _sourceNodeId = sourceNodeId;
            _targetNodeId = targetNodeId;
            _styleStrategy = styleStrategy;
            _routePoints = new PointF[0];
        }

        public string EdgeId
        {
            get { return _edgeId; }
        }

        public string SourceNodeId
        {
            get { return _sourceNodeId; }
        }

        public string TargetNodeId
        {
            get { return _targetNodeId; }
        }

        public IEdgeStyleStrategy Style
        {
            get { return _styleStrategy; }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; }
        }

        public PointF StartPoint
        {
            get { return _startPoint; }
        }

        public PointF EndPoint
        {
            get { return _endPoint; }
        }

        /// <summary>Returns the full path (start + route + end) in world coordinates.</summary>
        public List<PointF> GetFullPath()
        {
            return GetAllPoints();
        }

        /// <summary>Clears all crossing data from a previous routing pass.</summary>
        public void ClearCrossings()
        {
            _crossings = null;
        }

        /// <summary>Replaces the route with updated points (same start/end, modified intermediates).</summary>
        /// <summary>Read-only view of the routed poly-line waypoints (excluding start/end).</summary>
        public IReadOnlyList<PointF> RoutePoints
        {
            get
            {
                if (_routePoints == null) return new PointF[0];
                return System.Array.AsReadOnly(_routePoints);
            }
        }

        public void ReplaceRoutePoints(PointF[] newRoutePoints)
        {
            _routePoints = newRoutePoints ?? new PointF[0];

            float minX = _startPoint.X, minY = _startPoint.Y;
            float maxX = _startPoint.X, maxY = _startPoint.Y;
            if (_endPoint.X < minX) minX = _endPoint.X;
            if (_endPoint.Y < minY) minY = _endPoint.Y;
            if (_endPoint.X > maxX) maxX = _endPoint.X;
            if (_endPoint.Y > maxY) maxY = _endPoint.Y;
            for (int i = 0; i < _routePoints.Length; i++)
            {
                if (_routePoints[i].X < minX) minX = _routePoints[i].X;
                if (_routePoints[i].Y < minY) minY = _routePoints[i].Y;
                if (_routePoints[i].X > maxX) maxX = _routePoints[i].X;
                if (_routePoints[i].Y > maxY) maxY = _routePoints[i].Y;
            }
            float p = 10f;
            WorldBounds = new RectangleF(minX - p, minY - p, (maxX - minX) + p * 2, (maxY - minY) + p * 2);
            IsDirty = true;
        }

        /// <summary>Records a crossing on the given segment at the given world point.</summary>
        public void AddCrossing(int segmentIndex, PointF worldPoint)
        {
            if (_crossings == null)
                _crossings = new Dictionary<int, List<PointF>>();

            List<PointF> list;
            if (!_crossings.TryGetValue(segmentIndex, out list))
            {
                list = new List<PointF>();
                _crossings[segmentIndex] = list;
            }
            list.Add(worldPoint);
        }

        public void UpdateRoute(PointF start, PointF end, PointF[] routePoints)
        {
            _startPoint = start;
            _endPoint = end;
            _routePoints = routePoints ?? new PointF[0];

            // Recompute bounding rect from all points with padding
            float minX = start.X;
            float minY = start.Y;
            float maxX = start.X;
            float maxY = start.Y;

            if (end.X < minX) minX = end.X;
            if (end.Y < minY) minY = end.Y;
            if (end.X > maxX) maxX = end.X;
            if (end.Y > maxY) maxY = end.Y;

            for (int i = 0; i < _routePoints.Length; i++)
            {
                if (_routePoints[i].X < minX) minX = _routePoints[i].X;
                if (_routePoints[i].Y < minY) minY = _routePoints[i].Y;
                if (_routePoints[i].X > maxX) maxX = _routePoints[i].X;
                if (_routePoints[i].Y > maxY) maxY = _routePoints[i].Y;
            }

            float edgePadding = 10f;
            WorldBounds = new RectangleF(
                minX - edgePadding,
                minY - edgePadding,
                (maxX - minX) + edgePadding * 2,
                (maxY - minY) + edgePadding * 2);

            IsDirty = true;
        }

        public override bool HitTest(PointF worldPoint, float tolerance)
        {
            EdgeStyleInfo style = _styleStrategy.GetStyle(false);
            List<PointF> allPoints = GetAllPoints();

            if (style.UseSpline && allPoints.Count >= 2)
            {
                return HitTestSpline(worldPoint, allPoints, tolerance);
            }

            // Straight line hit test
            for (int i = 0; i < allPoints.Count - 1; i++)
            {
                float dist = DistancePointToSegment(worldPoint, allPoints[i], allPoints[i + 1]);
                if (dist < tolerance)
                {
                    return true;
                }
            }
            return false;
        }

        private bool HitTestSpline(PointF worldPoint, List<PointF> points, float tolerance)
        {
            PointF[] bezierPoints = GenerateBezierPoints(points.ToArray());

            if (bezierPoints.Length < 4)
            {
                // Fall back to straight line test
                for (int i = 0; i < points.Count - 1; i++)
                {
                    float dist = DistancePointToSegment(worldPoint, points[i], points[i + 1]);
                    if (dist < tolerance)
                    {
                        return true;
                    }
                }
                return false;
            }

            // Sample points along the spline and check distance
            int samplesPerSegment = 10;
            int numSegments = (bezierPoints.Length - 1) / 3;

            for (int seg = 0; seg < numSegments; seg++)
            {
                int baseIdx = seg * 3;
                PointF p0 = bezierPoints[baseIdx];
                PointF p1 = bezierPoints[baseIdx + 1];
                PointF p2 = bezierPoints[baseIdx + 2];
                PointF p3 = bezierPoints[baseIdx + 3];

                PointF prevSample = p0;
                for (int s = 1; s <= samplesPerSegment; s++)
                {
                    float t = s / (float)samplesPerSegment;
                    PointF sample = EvaluateBezier(p0, p1, p2, p3, t);

                    float dist = DistancePointToSegment(worldPoint, prevSample, sample);
                    if (dist < tolerance)
                    {
                        return true;
                    }

                    prevSample = sample;
                }
            }

            return false;
        }

        public override void Render(Graphics g, ViewportTransform viewport)
        {
            if (g == null || viewport == null)
            {
                return;
            }

            EdgeStyleInfo style = _styleStrategy.GetStyle(_isSelected);
            List<PointF> allPoints = GetAllPoints();

            if (allPoints.Count < 2)
            {
                return;
            }

            // Transform all points to screen coordinates
            PointF[] screenPoints = new PointF[allPoints.Count];
            for (int i = 0; i < allPoints.Count; i++)
            {
                screenPoints[i] = viewport.WorldToScreen(allPoints[i]);
            }

            if (style.UseSpline && screenPoints.Length >= 2)
            {
                RenderSpline(g, screenPoints, style);
            }
            else
            {
                RenderStraightLines(g, screenPoints, style);
            }

            // Draw arrowhead or hollow triangle at end
            if (screenPoints.Length >= 2)
            {
                PointF tip = screenPoints[screenPoints.Length - 1];
                PointF prev;

                if (style.UseSpline)
                {
                    // For splines, compute tangent direction at end point
                    prev = GetSplineTangentPoint(screenPoints, screenPoints.Length - 1);
                }
                else
                {
                    prev = screenPoints[screenPoints.Length - 2];
                }

                if (style.DrawHollowTriangle)
                {
                    DrawHollowTriangle(g, prev, tip, style.LineColor, style.LineWidth);
                }
                else if (style.DrawArrowhead)
                {
                    DrawFilledArrowhead(g, prev, tip, style.LineColor);
                }
            }
        }

        private void RenderStraightLines(Graphics g, PointF[] screenPoints, EdgeStyleInfo style)
        {
            List<PointF> worldPoints = GetAllPoints();

            using (Pen linePen = new Pen(style.LineColor, style.LineWidth))
            {
                linePen.DashStyle = style.LineDashStyle;

                for (int i = 0; i < screenPoints.Length - 1; i++)
                {
                    List<PointF> segCrossings = GetSegmentCrossings(i, worldPoints);

                    if (segCrossings == null || segCrossings.Count == 0)
                    {
                        g.DrawLine(linePen, screenPoints[i], screenPoints[i + 1]);
                    }
                    else
                    {
                        DrawSegmentWithBridges(g, linePen, screenPoints[i], screenPoints[i + 1],
                            segCrossings, worldPoints[i], worldPoints[i + 1]);
                    }
                }
            }

            if (_isSelected)
            {
                s_selectionHighlightPen.Width = style.LineWidth + 4f;
                for (int i = 0; i < screenPoints.Length - 1; i++)
                {
                    g.DrawLine(s_selectionHighlightPen, screenPoints[i], screenPoints[i + 1]);
                }
            }
        }

        /// <summary>
        /// Retrieves crossing points for a segment, sorted by distance along the segment.
        /// Returns screen-space crossing points.
        /// </summary>
        private List<PointF> GetSegmentCrossings(int segIndex, List<PointF> worldPoints)
        {
            if (_crossings == null) return null;
            List<PointF> pts;
            if (!_crossings.TryGetValue(segIndex, out pts)) return null;
            if (pts.Count == 0) return null;

            // Sort crossings by distance along segment direction
            PointF a = worldPoints[segIndex];
            PointF b = worldPoints[segIndex + 1];
            bool isHoriz = Math.Abs(a.Y - b.Y) < 0.5f;

            pts.Sort(delegate(PointF p1, PointF p2)
            {
                if (isHoriz)
                {
                    float d1 = (a.X < b.X) ? p1.X - a.X : a.X - p1.X;
                    float d2 = (a.X < b.X) ? p2.X - a.X : a.X - p2.X;
                    return d1.CompareTo(d2);
                }
                else
                {
                    float d1 = (a.Y < b.Y) ? p1.Y - a.Y : a.Y - p1.Y;
                    float d2 = (a.Y < b.Y) ? p2.Y - a.Y : a.Y - p2.Y;
                    return d1.CompareTo(d2);
                }
            });

            return pts;
        }

        /// <summary>
        /// Draws a segment with semicircular bridge arcs at each crossing point.
        /// </summary>
        private void DrawSegmentWithBridges(Graphics g, Pen pen,
            PointF screenA, PointF screenB,
            List<PointF> worldCrossings,
            PointF worldA, PointF worldB)
        {
            bool isHoriz = Math.Abs(worldA.Y - worldB.Y) < 0.5f;
            float arcRadius = 6f; // screen pixels

            // Convert crossing world coords to parametric t along the segment
            PointF cursor = screenA;
            float totalLen = Dist(screenA, screenB);
            if (totalLen < 1f)
            {
                g.DrawLine(pen, screenA, screenB);
                return;
            }

            float dirX = (screenB.X - screenA.X) / totalLen;
            float dirY = (screenB.Y - screenA.Y) / totalLen;

            // Perpendicular for the arc (always arc "upward" for horizontal, "leftward" for vertical)
            float perpX, perpY;
            if (isHoriz)
            {
                perpX = 0f;
                perpY = -1f; // arc goes upward
            }
            else
            {
                perpX = -1f; // arc goes leftward
                perpY = 0f;
            }

            SmoothingMode oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            for (int c = 0; c < worldCrossings.Count; c++)
            {
                PointF wc = worldCrossings[c];

                // Parametric position of crossing along the world segment
                float t;
                if (isHoriz)
                {
                    float wLen = worldB.X - worldA.X;
                    t = Math.Abs(wLen) > 0.01f ? (wc.X - worldA.X) / wLen : 0.5f;
                }
                else
                {
                    float wLen = worldB.Y - worldA.Y;
                    t = Math.Abs(wLen) > 0.01f ? (wc.Y - worldA.Y) / wLen : 0.5f;
                }

                float crossDist = t * totalLen;
                float arcStart = crossDist - arcRadius;
                float arcEnd = crossDist + arcRadius;
                if (arcStart < 0f) arcStart = 0f;
                if (arcEnd > totalLen) arcEnd = totalLen;

                // Draw line from cursor to arc start
                PointF pArcStart = new PointF(
                    screenA.X + dirX * arcStart,
                    screenA.Y + dirY * arcStart);
                g.DrawLine(pen, cursor, pArcStart);

                // Draw the semicircular bridge arc
                PointF pArcEnd = new PointF(
                    screenA.X + dirX * arcEnd,
                    screenA.Y + dirY * arcEnd);

                float midX = (pArcStart.X + pArcEnd.X) / 2f;
                float midY = (pArcStart.Y + pArcEnd.Y) / 2f;
                float r = arcRadius;

                // Build arc using 3 points: start, apex, end
                PointF apex = new PointF(midX + perpX * r, midY + perpY * r);

                using (GraphicsPath arcPath = new GraphicsPath())
                {
                    // Approximate semicircle with a bezier curve
                    float cx1 = pArcStart.X + perpX * r * 0.8f;
                    float cy1 = pArcStart.Y + perpY * r * 0.8f;
                    float cx2 = pArcEnd.X + perpX * r * 0.8f;
                    float cy2 = pArcEnd.Y + perpY * r * 0.8f;

                    arcPath.AddBezier(pArcStart,
                        new PointF(cx1, cy1),
                        new PointF(cx2, cy2),
                        pArcEnd);

                    g.DrawPath(pen, arcPath);
                }

                cursor = pArcEnd;
            }

            // Draw remaining line from cursor to end
            g.DrawLine(pen, cursor, screenB);
            g.SmoothingMode = oldSmoothing;
        }

        private static float Dist(PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private void RenderSpline(Graphics g, PointF[] screenPoints, EdgeStyleInfo style)
        {
            // Generate Bezier control points for smooth spline
            PointF[] bezierPoints = GenerateBezierPoints(screenPoints);

            if (bezierPoints.Length < 4)
            {
                // Fall back to straight line if not enough points
                RenderStraightLines(g, screenPoints, style);
                return;
            }

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddBeziers(bezierPoints);

                // Draw selection highlight first (behind the main line)
                if (_isSelected)
                {
                    s_selectionHighlightPen.Width = style.LineWidth + 4f;
                    g.DrawPath(s_selectionHighlightPen, path);
                }

                // Draw the spline
                using (Pen linePen = new Pen(style.LineColor, style.LineWidth))
                {
                    linePen.DashStyle = style.LineDashStyle;
                    g.DrawPath(linePen, path);
                }
            }
        }

        private static PointF[] GenerateBezierPoints(PointF[] points)
        {
            if (points.Length < 2)
            {
                return new PointF[0];
            }

            if (points.Length == 2)
            {
                // Simple case: create a gentle curve between two points
                PointF p0 = points[0];
                PointF p3 = points[1];
                float dx = p3.X - p0.X;
                float dy = p3.Y - p0.Y;

                // Control points at 1/3 and 2/3 with slight perpendicular offset
                PointF p1 = new PointF(p0.X + dx * 0.33f, p0.Y + dy * 0.33f);
                PointF p2 = new PointF(p0.X + dx * 0.67f, p0.Y + dy * 0.67f);

                return new PointF[] { p0, p1, p2, p3 };
            }

            // For multiple points, create smooth Catmull-Rom style bezier curves
            List<PointF> result = new List<PointF>();
            float tension = 0.5f;

            for (int i = 0; i < points.Length - 1; i++)
            {
                PointF p0 = points[i];
                PointF p3 = points[i + 1];

                // Get tangent vectors at each point
                PointF tangent0, tangent1;

                if (i == 0)
                {
                    // First segment: tangent points toward next point
                    tangent0 = new PointF(
                        (points[i + 1].X - points[i].X) * tension,
                        (points[i + 1].Y - points[i].Y) * tension);
                }
                else
                {
                    // Middle point: tangent is average of neighboring segments
                    tangent0 = new PointF(
                        (points[i + 1].X - points[i - 1].X) * tension * 0.5f,
                        (points[i + 1].Y - points[i - 1].Y) * tension * 0.5f);
                }

                if (i == points.Length - 2)
                {
                    // Last segment: tangent points from previous point
                    tangent1 = new PointF(
                        (points[i + 1].X - points[i].X) * tension,
                        (points[i + 1].Y - points[i].Y) * tension);
                }
                else
                {
                    // Middle point: tangent is average of neighboring segments
                    tangent1 = new PointF(
                        (points[i + 2].X - points[i].X) * tension * 0.5f,
                        (points[i + 2].Y - points[i].Y) * tension * 0.5f);
                }

                // Convert to Bezier control points
                PointF p1 = new PointF(p0.X + tangent0.X, p0.Y + tangent0.Y);
                PointF p2 = new PointF(p3.X - tangent1.X, p3.Y - tangent1.Y);

                if (i == 0)
                {
                    result.Add(p0);
                }
                result.Add(p1);
                result.Add(p2);
                result.Add(p3);
            }

            return result.ToArray();
        }

        private static PointF GetSplineTangentPoint(PointF[] points, int endIndex)
        {
            // Get a point slightly before the end point for arrow direction
            if (points.Length < 2)
            {
                return points[0];
            }

            PointF[] bezierPoints = GenerateBezierPoints(points);
            if (bezierPoints.Length < 4)
            {
                return points[points.Length - 2];
            }

            // Sample the curve slightly before t=1 to get tangent direction
            int lastSegmentStart = bezierPoints.Length - 4;
            PointF p0 = bezierPoints[lastSegmentStart];
            PointF p1 = bezierPoints[lastSegmentStart + 1];
            PointF p2 = bezierPoints[lastSegmentStart + 2];
            PointF p3 = bezierPoints[lastSegmentStart + 3];

            // Evaluate Bezier at t=0.9
            float t = 0.9f;
            return EvaluateBezier(p0, p1, p2, p3, t);
        }

        private static PointF EvaluateBezier(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            float u = 1f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            float x = uuu * p0.X + 3f * uu * t * p1.X + 3f * u * tt * p2.X + ttt * p3.X;
            float y = uuu * p0.Y + 3f * uu * t * p1.Y + 3f * u * tt * p2.Y + ttt * p3.Y;

            return new PointF(x, y);
        }

        private List<PointF> GetAllPoints()
        {
            List<PointF> points = new List<PointF>();
            points.Add(_startPoint);
            for (int i = 0; i < _routePoints.Length; i++)
            {
                points.Add(_routePoints[i]);
            }
            points.Add(_endPoint);
            return points;
        }

        private static float DistancePointToSegment(PointF point, PointF segA, PointF segB)
        {
            float dx = segB.X - segA.X;
            float dy = segB.Y - segA.Y;
            float lengthSq = dx * dx + dy * dy;

            if (lengthSq < 0.0001f)
            {
                float px = point.X - segA.X;
                float py = point.Y - segA.Y;
                return (float)Math.Sqrt(px * px + py * py);
            }

            float t = ((point.X - segA.X) * dx + (point.Y - segA.Y) * dy) / lengthSq;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            float closestX = segA.X + t * dx;
            float closestY = segA.Y + t * dy;
            float distX = point.X - closestX;
            float distY = point.Y - closestY;
            return (float)Math.Sqrt(distX * distX + distY * distY);
        }

        private static void DrawFilledArrowhead(Graphics g, PointF from, PointF tip, Color color)
        {
            float arrowLength = 12f;
            float arrowWidth = 6f;

            float dx = tip.X - from.X;
            float dy = tip.Y - from.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            if (length < 0.001f)
            {
                return;
            }

            float ux = dx / length;
            float uy = dy / length;

            PointF baseCenter = new PointF(
                tip.X - ux * arrowLength,
                tip.Y - uy * arrowLength);

            float perpX = -uy;
            float perpY = ux;

            PointF left = new PointF(
                baseCenter.X + perpX * arrowWidth,
                baseCenter.Y + perpY * arrowWidth);
            PointF right = new PointF(
                baseCenter.X - perpX * arrowWidth,
                baseCenter.Y - perpY * arrowWidth);

            PointF[] arrowHead = new PointF[] { tip, left, right };
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, arrowHead);
            }
        }

        private static void DrawHollowTriangle(Graphics g, PointF from, PointF tip, Color color, float lineWidth)
        {
            float arrowLength = 14f;
            float arrowWidth = 7f;

            float dx = tip.X - from.X;
            float dy = tip.Y - from.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            if (length < 0.001f)
            {
                return;
            }

            float ux = dx / length;
            float uy = dy / length;

            PointF baseCenter = new PointF(
                tip.X - ux * arrowLength,
                tip.Y - uy * arrowLength);

            float perpX = -uy;
            float perpY = ux;

            PointF left = new PointF(
                baseCenter.X + perpX * arrowWidth,
                baseCenter.Y + perpY * arrowWidth);
            PointF right = new PointF(
                baseCenter.X - perpX * arrowWidth,
                baseCenter.Y - perpY * arrowWidth);

            PointF[] triangle = new PointF[] { tip, left, right };
            g.FillPolygon(s_hollowTriangleFillBrush, triangle);
            using (Pen outlinePen = new Pen(color, lineWidth))
            {
                g.DrawPolygon(outlinePen, triangle);
            }
        }
    }
}
