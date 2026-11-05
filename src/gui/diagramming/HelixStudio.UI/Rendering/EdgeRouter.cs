namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;

    /// <summary>
    /// Routes edges using orthogonal A* pathfinding on a waypoint grid,
    /// modelled after DBeaver/Eclipse Draw2d ShortestPathRouter.
    /// Key features:
    ///   - Indentation stubs: edges exit entities perpendicular before routing
    ///   - Obstacle avoidance via grid-based A*
    ///   - Overlap penalty: later edges are pushed away from earlier ones
    ///   - Point collision shifting at shared entity borders
    ///   - Bridge arcs at crossing points
    /// </summary>
    public sealed class EdgeRouter
    {
        private const float Margin = 20f;
        private const float PortMargin = 12f;
        private const float Indentation = 28f;
        private const float CircleIndentation = 14f;
        private const float OverlapPenalty = 800f;
        private const float NearDist = 14f;
        private const int PointDistance = 7;

        // Feature 4: trunk routing
        private const float TrunkGap = 16f;
        private const float LaneWidth = 8f;
        private const int MaxLaneTries = 5;

        /// <summary>
        /// Maximum number of edges before A* routing is skipped in favor of
        /// simplified Z-shaped fallback routing for all edges.
        /// </summary>
        private const int AStarEdgeThreshold = 150;

        /// <summary>
        /// Maximum grid cell count (W * H) before A* is skipped for a
        /// single edge and the simplified fallback is used instead.
        /// </summary>
        private const int AStarGridCellCap = 10000;

        // ----------------------------------------------------------------
        //  Single-edge routing (no obstacle info)
        // ----------------------------------------------------------------

        public void RouteEdge(EdgeVisual edge, RectangleF sourceBounds, RectangleF targetBounds)
        {
            RouteEdge(edge, sourceBounds, targetBounds, false, false);
        }

        public void RouteEdge(EdgeVisual edge, RectangleF sourceBounds, RectangleF targetBounds,
            bool sourceIsCircle, bool targetIsCircle)
        {
            if (edge == null) throw new ArgumentNullException("edge");

            if (!string.IsNullOrEmpty(edge.SourceNodeId)
                && edge.SourceNodeId == edge.TargetNodeId)
            {
                RouteSelfLoop(edge, sourceBounds);
                return;
            }

            int exitSide, entrySide;
            ChooseConnectionSides(sourceBounds, targetBounds, out exitSide, out entrySide);

            PointF start = GetSideCenter(sourceBounds, exitSide);
            PointF end = GetSideCenter(targetBounds, entrySide);

            float srcIndent = sourceIsCircle ? CircleIndentation : Indentation;
            float tgtIndent = targetIsCircle ? CircleIndentation : Indentation;

            PointF stub1 = GetStubPoint(start, exitSide, srcIndent);
            PointF stub2 = GetStubPoint(end, entrySide, tgtIndent);

            // For circle nodes, snap start/end to the circle perimeter
            if (sourceIsCircle)
            {
                start = GetCircleBorderIntersection(sourceBounds, stub1);
            }
            if (targetIsCircle)
            {
                end = GetCircleBorderIntersection(targetBounds, stub2);
            }

            edge.UpdateRoute(start, end, new PointF[] { stub1, stub2 });
        }

        private void RouteSelfLoop(EdgeVisual edge, RectangleF bounds)
        {
            // Arc off the top-right corner: exit from top, return to right side.
            float loopOffset = 30f;
            PointF start = new PointF(bounds.Right - 20f, bounds.Top);
            PointF end = new PointF(bounds.Right, bounds.Top + 20f);
            PointF cornerOut = new PointF(start.X, start.Y - loopOffset);
            PointF cornerFar = new PointF(end.X + loopOffset, start.Y - loopOffset);
            PointF cornerIn = new PointF(end.X + loopOffset, end.Y);

            edge.UpdateRoute(start, end, new PointF[] { cornerOut, cornerFar, cornerIn });
        }

        // ----------------------------------------------------------------
        //  Batch routing: indentation stubs + A* + overlap penalty
        // ----------------------------------------------------------------

        public void RouteAllEdges(SceneGraph scene)
        {
            if (scene == null) throw new ArgumentNullException("scene");

            for (int i = 0; i < scene.Edges.Count; i++)
                scene.Edges[i].ClearCrossings();

            // Collect node bounds and circle flags
            var nodeBounds = new Dictionary<string, RectangleF>();
            var isCircle = new Dictionary<string, bool>();
            for (int i = 0; i < scene.Nodes.Count; i++)
            {
                nodeBounds[scene.Nodes[i].NodeId] = scene.Nodes[i].GetWorldBounds();
                isCircle[scene.Nodes[i].NodeId] = scene.Nodes[i].IsCircleMode;
            }

            // Determine connection sides
            var infos = new List<EdgeInfo>();
            for (int i = 0; i < scene.Edges.Count; i++)
            {
                EdgeVisual edge = scene.Edges[i];
                RectangleF srcB, tgtB;
                if (!TryGetBounds(nodeBounds, edge, out srcB, out tgtB)) continue;

                // Self-loops route independently of the A* pipeline
                if (!string.IsNullOrEmpty(edge.SourceNodeId)
                    && edge.SourceNodeId == edge.TargetNodeId)
                {
                    RouteSelfLoop(edge, srcB);
                    continue;
                }

                int exitSide, entrySide;
                ChooseConnectionSides(srcB, tgtB, out exitSide, out entrySide);

                infos.Add(new EdgeInfo
                {
                    Edge = edge, SrcBounds = srcB, TgtBounds = tgtB,
                    ExitSide = exitSide, EntrySide = entrySide
                });
            }

            // Port distribution groups
            var srcGroups = new Dictionary<string, List<int>>();
            var tgtGroups = new Dictionary<string, List<int>>();
            for (int i = 0; i < infos.Count; i++)
            {
                AddToGroup(srcGroups, infos[i].Edge.SourceNodeId + "|" + infos[i].ExitSide, i);
                AddToGroup(tgtGroups, infos[i].Edge.TargetNodeId + "|" + infos[i].EntrySide, i);
            }

            // Compute port positions for every edge
            var starts = new PointF[infos.Count];
            var ends = new PointF[infos.Count];
            for (int i = 0; i < infos.Count; i++)
            {
                string srcKey = infos[i].Edge.SourceNodeId + "|" + infos[i].ExitSide;
                string tgtKey = infos[i].Edge.TargetNodeId + "|" + infos[i].EntrySide;
                starts[i] = GetDistributedPort(infos[i].SrcBounds, infos[i].ExitSide, srcGroups[srcKey], i);
                ends[i] = GetDistributedPort(infos[i].TgtBounds, infos[i].EntrySide, tgtGroups[tgtKey], i);
            }

            // Compute indentation stub points for every edge
            // Use shorter indentation for circle nodes (they are 60x60 vs 200x150)
            var stubs1 = new PointF[infos.Count];
            var stubs2 = new PointF[infos.Count];
            for (int i = 0; i < infos.Count; i++)
            {
                bool srcCircle = false;
                bool tgtCircle = false;
                isCircle.TryGetValue(infos[i].Edge.SourceNodeId, out srcCircle);
                isCircle.TryGetValue(infos[i].Edge.TargetNodeId, out tgtCircle);

                float srcIndent = srcCircle ? CircleIndentation : Indentation;
                float tgtIndent = tgtCircle ? CircleIndentation : Indentation;

                stubs1[i] = GetStubPoint(starts[i], infos[i].ExitSide, srcIndent);
                stubs2[i] = GetStubPoint(ends[i], infos[i].EntrySide, tgtIndent);
            }

            // For circle nodes, snap start/end points to the circle perimeter
            // instead of the rectangular bounding box side
            for (int i = 0; i < infos.Count; i++)
            {
                bool srcCircle = false;
                bool tgtCircle = false;
                isCircle.TryGetValue(infos[i].Edge.SourceNodeId, out srcCircle);
                isCircle.TryGetValue(infos[i].Edge.TargetNodeId, out tgtCircle);

                if (srcCircle)
                {
                    starts[i] = GetCircleBorderIntersection(infos[i].SrcBounds, stubs1[i]);
                }
                if (tgtCircle)
                {
                    ends[i] = GetCircleBorderIntersection(infos[i].TgtBounds, stubs2[i]);
                }
            }

            // Build master coordinate grid
            var xSet = new List<float>();
            var ySet = new List<float>();
            foreach (var kvp in nodeBounds)
            {
                RectangleF r = kvp.Value;
                // Obstacle corners with margin
                AddUnique(xSet, r.Left - Margin);
                AddUnique(xSet, r.Right + Margin);
                AddUnique(ySet, r.Top - Margin);
                AddUnique(ySet, r.Bottom + Margin);
                // Obstacle edges (for routing close to entities)
                AddUnique(xSet, r.Left);
                AddUnique(xSet, r.Right);
                AddUnique(ySet, r.Top);
                AddUnique(ySet, r.Bottom);
            }
            // Add all port and stub coordinates
            for (int i = 0; i < infos.Count; i++)
            {
                AddUnique(xSet, starts[i].X);
                AddUnique(xSet, ends[i].X);
                AddUnique(ySet, starts[i].Y);
                AddUnique(ySet, ends[i].Y);
                AddUnique(xSet, stubs1[i].X);
                AddUnique(xSet, stubs2[i].X);
                AddUnique(ySet, stubs1[i].Y);
                AddUnique(ySet, stubs2[i].Y);
            }
            // Add midpoint coordinates between adjacent obstacles for routing in gaps
            AddGapMidpoints(xSet, ySet, nodeBounds);

            xSet.Sort();
            ySet.Sort();
            float[] xs = xSet.ToArray();
            float[] ys = ySet.ToArray();

            // Determine whether to skip A* entirely due to edge count threshold
            bool skipAStar = infos.Count > AStarEdgeThreshold;

            // Sort edges: shorter Manhattan distance first (more constrained)
            var order = new int[infos.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate(int a, int b)
            {
                float da = ManhattanDist(infos[a].SrcBounds, infos[a].TgtBounds);
                float db = ManhattanDist(infos[b].SrcBounds, infos[b].TgtBounds);
                return da.CompareTo(db);
            });

            // Route each edge: stub1 → A* → stub2 (or simplified fallback)
            var occupied = new List<RoutedSeg>();

            // Cache all routed points for point-collision detection
            var routedPoints = new List<List<PointF>>();
            for (int i = 0; i < infos.Count; i++)
                routedPoints.Add(null);

            // Precompute grid cell count for the per-edge cap check
            long gridCellCount = (long)xs.Length * (long)ys.Length;

            // Feature 4: first pass — route inheritance/implements groups as merged trunks
            // so N children converging on the same parent share a single visible line.
            // The same A*-affordability check that gates the main per-edge loop below (line ~306)
            // must gate this pass too: FindPath allocates and searches the FULL W*H coordinate grid
            // with a linear-scan open-list, so it's O((W*H)^2)-ish per call — calling it once per
            // blocked trunk member with no size cap at all, on a solution big enough to build a
            // large grid, turned a 166-node/43-edge rebuild into 75+ seconds (empirically measured).
            bool allowAStarForTrunks = !skipAStar && gridCellCount <= AStarGridCellCap;
            bool[] trunkRouted = new bool[infos.Count];
            RouteInheritanceTrunks(infos, starts, ends, stubs1, stubs2,
                nodeBounds, occupied, routedPoints, trunkRouted, xs, ys, allowAStarForTrunks);

            for (int oi = 0; oi < order.Length; oi++)
            {
                int idx = order[oi];
                if (trunkRouted[idx]) continue;
                EdgeInfo info = infos[idx];

                PointF start = starts[idx];
                PointF end = ends[idx];
                PointF s1 = stubs1[idx];
                PointF s2 = stubs2[idx];

                // Point collision detection: shift start/end if overlapping
                // with already-routed connections (DBeaver POINT_DISTANCE check)
                ShiftForCollisions(ref start, ref s1, info.ExitSide, routedPoints);
                ShiftForCollisions(ref end, ref s2, info.EntrySide, routedPoints);

                // If stub is inside an obstacle (other than src/tgt), reduce it
                var obs = new List<RectangleF>();
                foreach (var kvp in nodeBounds)
                {
                    if (kvp.Key != info.Edge.SourceNodeId && kvp.Key != info.Edge.TargetNodeId)
                        obs.Add(kvp.Value);
                }

                if (IsInsideObstacle(s1.X, s1.Y, obs))
                    s1 = GetStubPoint(start, info.ExitSide, Indentation / 3f);
                if (IsInsideObstacle(s2.X, s2.Y, obs))
                    s2 = GetStubPoint(end, info.EntrySide, Indentation / 3f);

                PointF[] innerRoute;

                // Use simplified routing if edge count exceeds threshold
                // or if the A* grid is too large for this edge
                if (skipAStar || gridCellCount > AStarGridCellCap)
                {
                    innerRoute = SimplifiedRouteWithLane(s1, s2, info.ExitSide, info.EntrySide, occupied, obs);
                }
                else
                {
                    // Route from stub1 to stub2 using A*
                    innerRoute = FindPath(s1, s2, xs, ys, obs, occupied,
                        info.ExitSide, info.EntrySide);
                }

                // Build full route: stub1 + inner + stub2
                var fullRoute = new List<PointF>();
                fullRoute.Add(s1);
                for (int j = 0; j < innerRoute.Length; j++)
                    fullRoute.Add(innerRoute[j]);
                fullRoute.Add(s2);

                // Simplify collinear points in the full route
                fullRoute = SimplifyPath(fullRoute);

                PointF[] routeArr = fullRoute.ToArray();
                info.Edge.UpdateRoute(start, end, routeArr);

                // Register all segments (including stubs)
                RegisterSegments(occupied, start, end, routeArr);

                // Store routed points for collision detection
                var allPts = new List<PointF>();
                allPts.Add(start);
                for (int j = 0; j < routeArr.Length; j++) allPts.Add(routeArr[j]);
                allPts.Add(end);
                routedPoints[idx] = allPts;
            }

            // Detect crossings for bridge arcs
            ComputeCrossings(infos);
        }

        // ----------------------------------------------------------------
        //  Indentation stub computation
        // ----------------------------------------------------------------

        private static PointF GetStubPoint(PointF port, int side, float indent)
        {
            switch (side)
            {
                case 0: return new PointF(port.X, port.Y - indent); // top exit → go up
                case 1: return new PointF(port.X + indent, port.Y); // right exit → go right
                case 2: return new PointF(port.X, port.Y + indent); // bottom exit → go down
                case 3: return new PointF(port.X - indent, port.Y); // left exit → go left
                default: return port;
            }
        }

        // ----------------------------------------------------------------
        //  Point collision detection (DBeaver-style)
        //  If start/end of this edge overlaps with a point of an already-
        //  routed edge (within PointDistance on same axis), shift it.
        // ----------------------------------------------------------------

        private static void ShiftForCollisions(ref PointF port, ref PointF stub,
            int side, List<List<PointF>> routedPoints)
        {
            // Shift direction perpendicular to the exit direction
            float shiftX = 0f, shiftY = 0f;
            bool isVerticalExit = (side == 0 || side == 2);

            for (int ei = 0; ei < routedPoints.Count; ei++)
            {
                List<PointF> pts = routedPoints[ei];
                if (pts == null) continue;

                for (int pi = 0; pi < pts.Count; pi++)
                {
                    PointF p = pts[pi];

                    if (isVerticalExit)
                    {
                        // For vertical exit, check if same X and close Y
                        if (Math.Abs(port.X - p.X) < 1f && Math.Abs(port.Y - p.Y) < PointDistance)
                        {
                            // Shift along X (perpendicular to exit direction)
                            shiftX += (port.X > p.X) ? PointDistance : -PointDistance;
                        }
                    }
                    else
                    {
                        // For horizontal exit, check if same Y and close X
                        if (Math.Abs(port.Y - p.Y) < 1f && Math.Abs(port.X - p.X) < PointDistance)
                        {
                            shiftY += (port.Y > p.Y) ? PointDistance : -PointDistance;
                        }
                    }
                }
            }

            if (Math.Abs(shiftX) > 0.5f || Math.Abs(shiftY) > 0.5f)
            {
                port = new PointF(port.X + shiftX, port.Y + shiftY);
                stub = new PointF(stub.X + shiftX, stub.Y + shiftY);
            }
        }

        // ----------------------------------------------------------------
        //  Gap midpoint calculation — add routing channels in gaps
        // ----------------------------------------------------------------

        private static void AddGapMidpoints(List<float> xSet, List<float> ySet,
            Dictionary<string, RectangleF> nodeBounds)
        {
            // Collect all obstacle horizontal edges
            var xEdges = new List<float>();
            var yEdges = new List<float>();
            foreach (var kvp in nodeBounds)
            {
                RectangleF r = kvp.Value;
                xEdges.Add(r.Left);
                xEdges.Add(r.Right);
                yEdges.Add(r.Top);
                yEdges.Add(r.Bottom);
            }
            xEdges.Sort();
            yEdges.Sort();

            // Add midpoints of gaps between adjacent edges (if gap > 2*Margin)
            for (int i = 0; i < xEdges.Count - 1; i++)
            {
                float gap = xEdges[i + 1] - xEdges[i];
                if (gap > 2 * Margin)
                    AddUnique(xSet, (xEdges[i] + xEdges[i + 1]) / 2f);
            }
            for (int i = 0; i < yEdges.Count - 1; i++)
            {
                float gap = yEdges[i + 1] - yEdges[i];
                if (gap > 2 * Margin)
                    AddUnique(ySet, (yEdges[i] + yEdges[i + 1]) / 2f);
            }
        }

        // ----------------------------------------------------------------
        //  A* pathfinding on orthogonal waypoint grid
        //  Constrained: first move must match exitSide direction,
        //  last move must match entrySide direction.
        // ----------------------------------------------------------------

        private PointF[] FindPath(PointF start, PointF end,
            float[] xs, float[] ys,
            List<RectangleF> obstacles, List<RoutedSeg> occupied,
            int exitSide, int entrySide)
        {
            int W = xs.Length, H = ys.Length;
            if (W == 0 || H == 0) return new PointF[0];

            int sxi = FindNearest(xs, start.X);
            int syi = FindNearest(ys, start.Y);
            int exi = FindNearest(xs, end.X);
            int eyi = FindNearest(ys, end.Y);

            if (sxi == exi && syi == eyi)
                return new PointF[0];

            int total = W * H;
            float[] gScore = new float[total];
            float[] fScore = new float[total];
            int[] prev = new int[total];
            bool[] closed = new bool[total];

            for (int i = 0; i < total; i++)
            {
                gScore[i] = float.MaxValue;
                fScore[i] = float.MaxValue;
                prev[i] = -1;
            }

            int startNode = syi * W + sxi;
            int endNode = eyi * W + exi;

            gScore[startNode] = 0f;
            fScore[startNode] = Heuristic(xs, ys, sxi, syi, exi, eyi);

            var open = new List<int>();
            open.Add(startNode);

            while (open.Count > 0)
            {
                // Pop node with lowest fScore
                int bestOi = 0;
                for (int k = 1; k < open.Count; k++)
                {
                    if (fScore[open[k]] < fScore[open[bestOi]])
                        bestOi = k;
                }
                int cur = open[bestOi];
                open[bestOi] = open[open.Count - 1];
                open.RemoveAt(open.Count - 1);

                if (cur == endNode) break;
                if (closed[cur]) continue;
                closed[cur] = true;

                int cx = cur % W;
                int cy = cur / W;

                TryNeighbors(xs, ys, W, H, cx, cy, exi, eyi,
                    obstacles, occupied,
                    gScore, fScore, prev, closed, open, cur);
            }

            if (prev[endNode] < 0 && endNode != startNode)
                return FallbackRoute(start, end, exitSide, entrySide);

            // Reconstruct path
            var path = new List<PointF>();
            int c = endNode;
            while (c >= 0)
            {
                path.Add(new PointF(xs[c % W], ys[c / W]));
                c = prev[c];
            }
            path.Reverse();

            // Snap first/last to exact start/end
            if (path.Count > 0) path[0] = start;
            if (path.Count > 1) path[path.Count - 1] = end;

            // Simplify collinear
            var simplified = SimplifyPath(path);

            // Return intermediate route points only
            if (simplified.Count <= 2) return new PointF[0];
            var route = new PointF[simplified.Count - 2];
            for (int i = 1; i < simplified.Count - 1; i++)
                route[i - 1] = simplified[i];
            return route;
        }

        private void TryNeighbors(float[] xs, float[] ys, int W, int H,
            int cx, int cy, int exi, int eyi,
            List<RectangleF> obstacles, List<RoutedSeg> occupied,
            float[] gScore, float[] fScore, int[] prev, bool[] closed,
            List<int> open, int curNode)
        {
            int[] dxArr = { 1, -1, 0, 0 };
            int[] dyArr = { 0, 0, 1, -1 };

            for (int d = 0; d < 4; d++)
            {
                int dx = dxArr[d];
                int dy = dyArr[d];

                int nx = cx + dx;
                int ny = cy + dy;

                // Walk in this direction, considering visible grid cells
                while (nx >= 0 && nx < W && ny >= 0 && ny < H)
                {
                    if (IsInsideObstacle(xs[nx], ys[ny], obstacles))
                        break;

                    if (IsSegmentBlocked(xs[cx], ys[cy], xs[nx], ys[ny], obstacles))
                        break;

                    int next = ny * W + nx;
                    if (!closed[next])
                    {
                        float segLen = Math.Abs(xs[nx] - xs[cx]) + Math.Abs(ys[ny] - ys[cy]);
                        float penalty = GetOverlapPenalty(xs[cx], ys[cy], xs[nx], ys[ny], occupied);

                        // Bend penalty: prefer fewer turns
                        float bendPenalty = 0f;
                        if (prev[curNode] >= 0)
                        {
                            int px = prev[curNode] % W;
                            int py = prev[curNode] / W;
                            bool prevWasH = (py == cy);
                            bool curIsH = (dy == 0);
                            if (prevWasH != curIsH) bendPenalty = 12f;
                        }

                        float newG = gScore[curNode] + segLen + penalty + bendPenalty;
                        if (newG < gScore[next])
                        {
                            gScore[next] = newG;
                            fScore[next] = newG + Heuristic(xs, ys, nx, ny, exi, eyi);
                            prev[next] = curNode;
                            open.Add(next);
                        }
                    }

                    nx += dx;
                    ny += dy;
                }
            }
        }

        private static float Heuristic(float[] xs, float[] ys, int xi, int yi, int exi, int eyi)
        {
            return Math.Abs(xs[exi] - xs[xi]) + Math.Abs(ys[eyi] - ys[yi]);
        }

        // ----------------------------------------------------------------
        //  Segment / obstacle checks
        // ----------------------------------------------------------------

        private static bool IsSegmentBlocked(float x1, float y1, float x2, float y2,
            List<RectangleF> obstacles)
        {
            bool isH = Math.Abs(y1 - y2) < 0.5f;

            if (isH)
            {
                float y = (y1 + y2) / 2f;
                float xMin = Math.Min(x1, x2);
                float xMax = Math.Max(x1, x2);
                for (int i = 0; i < obstacles.Count; i++)
                {
                    RectangleF obs = obstacles[i];
                    if (y > obs.Top + 0.5f && y < obs.Bottom - 0.5f &&
                        obs.Left < xMax - 0.5f && obs.Right > xMin + 0.5f)
                        return true;
                }
            }
            else
            {
                float x = (x1 + x2) / 2f;
                float yMin = Math.Min(y1, y2);
                float yMax = Math.Max(y1, y2);
                for (int i = 0; i < obstacles.Count; i++)
                {
                    RectangleF obs = obstacles[i];
                    if (x > obs.Left + 0.5f && x < obs.Right - 0.5f &&
                        obs.Top < yMax - 0.5f && obs.Bottom > yMin + 0.5f)
                        return true;
                }
            }
            return false;
        }

        private static bool IsInsideObstacle(float x, float y, List<RectangleF> obstacles)
        {
            for (int i = 0; i < obstacles.Count; i++)
            {
                RectangleF obs = obstacles[i];
                if (x > obs.Left + 0.5f && x < obs.Right - 0.5f &&
                    y > obs.Top + 0.5f && y < obs.Bottom - 0.5f)
                    return true;
            }
            return false;
        }

        private static float GetOverlapPenalty(float x1, float y1, float x2, float y2,
            List<RoutedSeg> occupied)
        {
            if (occupied.Count == 0) return 0f;

            bool isH = Math.Abs(y1 - y2) < 0.5f;
            float penalty = 0f;

            if (isH)
            {
                float y = (y1 + y2) / 2f;
                float xMin = Math.Min(x1, x2);
                float xMax = Math.Max(x1, x2);
                for (int i = 0; i < occupied.Count; i++)
                {
                    RoutedSeg seg = occupied[i];
                    if (!seg.IsH) continue;
                    if (Math.Abs(seg.Pos - y) >= NearDist) continue;
                    if (seg.Max > xMin + 1f && seg.Min < xMax - 1f)
                        penalty += OverlapPenalty;
                }
            }
            else
            {
                float x = (x1 + x2) / 2f;
                float yMin = Math.Min(y1, y2);
                float yMax = Math.Max(y1, y2);
                for (int i = 0; i < occupied.Count; i++)
                {
                    RoutedSeg seg = occupied[i];
                    if (seg.IsH) continue;
                    if (Math.Abs(seg.Pos - x) >= NearDist) continue;
                    if (seg.Max > yMin + 1f && seg.Min < yMax - 1f)
                        penalty += OverlapPenalty;
                }
            }

            return penalty;
        }

        // ----------------------------------------------------------------
        //  Segment registration
        // ----------------------------------------------------------------

        private static void RegisterSegments(List<RoutedSeg> occupied,
            PointF start, PointF end, PointF[] route)
        {
            var pts = new List<PointF>();
            pts.Add(start);
            for (int i = 0; i < route.Length; i++) pts.Add(route[i]);
            pts.Add(end);

            for (int i = 0; i < pts.Count - 1; i++)
            {
                PointF a = pts[i];
                PointF b = pts[i + 1];
                float len = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
                if (len < 2f) continue;

                bool isH = Math.Abs(a.Y - b.Y) < 0.5f;
                bool isV = Math.Abs(a.X - b.X) < 0.5f;

                if (isH)
                {
                    occupied.Add(new RoutedSeg
                    {
                        IsH = true,
                        Pos = (a.Y + b.Y) / 2f,
                        Min = Math.Min(a.X, b.X),
                        Max = Math.Max(a.X, b.X)
                    });
                }
                else if (isV)
                {
                    occupied.Add(new RoutedSeg
                    {
                        IsH = false,
                        Pos = (a.X + b.X) / 2f,
                        Min = Math.Min(a.Y, b.Y),
                        Max = Math.Max(a.Y, b.Y)
                    });
                }
            }
        }

        // ----------------------------------------------------------------
        //  Path simplification — remove collinear intermediate points
        // ----------------------------------------------------------------

        private static List<PointF> SimplifyPath(List<PointF> path)
        {
            if (path.Count <= 2) return path;

            var result = new List<PointF>();
            result.Add(path[0]);

            for (int i = 1; i < path.Count - 1; i++)
            {
                PointF prev = path[i - 1];
                PointF cur = path[i];
                PointF next = path[i + 1];

                bool collinearH = Math.Abs(prev.Y - cur.Y) < 0.5f && Math.Abs(cur.Y - next.Y) < 0.5f;
                bool collinearV = Math.Abs(prev.X - cur.X) < 0.5f && Math.Abs(cur.X - next.X) < 0.5f;

                if (!collinearH && !collinearV)
                    result.Add(cur);
            }

            result.Add(path[path.Count - 1]);
            return result;
        }

        // ----------------------------------------------------------------
        //  Simplified routing — Z-shaped fallback used when A* is skipped
        //  due to performance thresholds.  Preserves connection sides and
        //  port distribution but avoids the expensive grid-based search.
        // ----------------------------------------------------------------

        private static PointF[] SimplifiedRoute(PointF stub1, PointF stub2,
            int exitSide, int entrySide)
        {
            bool exitV = (exitSide == 0 || exitSide == 2);
            bool entryV = (entrySide == 0 || entrySide == 2);

            // Both exit and entry are vertical — use Z-shape with horizontal middle
            if (exitV && entryV)
            {
                float midY = (stub1.Y + stub2.Y) / 2f;
                return new PointF[]
                {
                    new PointF(stub1.X, midY),
                    new PointF(stub2.X, midY)
                };
            }

            // Both exit and entry are horizontal — use Z-shape with vertical middle
            if (!exitV && !entryV)
            {
                float midX = (stub1.X + stub2.X) / 2f;
                return new PointF[]
                {
                    new PointF(midX, stub1.Y),
                    new PointF(midX, stub2.Y)
                };
            }

            // Mixed: one vertical, one horizontal — use L-shape
            if (exitV)
            {
                // Exit is vertical, entry is horizontal
                return new PointF[]
                {
                    new PointF(stub1.X, stub2.Y)
                };
            }
            else
            {
                // Exit is horizontal, entry is vertical
                return new PointF[]
                {
                    new PointF(stub2.X, stub1.Y)
                };
            }
        }

        // ----------------------------------------------------------------
        //  Feature 4: Lane-aware simplified routing
        //  When the base midY / midX would overlap an already-routed segment,
        //  nudge by a multiple of LaneWidth until the overlap clears.
        // ----------------------------------------------------------------

        private static PointF[] SimplifiedRouteWithLane(PointF stub1, PointF stub2,
            int exitSide, int entrySide, List<RoutedSeg> occupied, List<RectangleF> obstacles)
        {
            bool exitV = (exitSide == 0 || exitSide == 2);
            bool entryV = (entrySide == 0 || entrySide == 2);

            if (exitV && entryV)
            {
                float baseMid = (stub1.Y + stub2.Y) / 2f;
                float mid = FindFreeLane(baseMid, true, stub1.X, stub2.X, occupied, obstacles);
                return new PointF[]
                {
                    new PointF(stub1.X, mid),
                    new PointF(stub2.X, mid)
                };
            }

            if (!exitV && !entryV)
            {
                float baseMid = (stub1.X + stub2.X) / 2f;
                float mid = FindFreeLane(baseMid, false, stub1.Y, stub2.Y, occupied, obstacles);
                return new PointF[]
                {
                    new PointF(mid, stub1.Y),
                    new PointF(mid, stub2.Y)
                };
            }

            // Mixed vertical/horizontal — simple L has no shared mid-run to collide, reuse original logic.
            if (exitV)
            {
                return new PointF[] { new PointF(stub1.X, stub2.Y) };
            }
            else
            {
                return new PointF[] { new PointF(stub2.X, stub1.Y) };
            }
        }

        private static float FindFreeLane(float baseCoord, bool horizontalRun,
            float spanA, float spanB, List<RoutedSeg> occupied, List<RectangleF> obstacles)
        {
            bool hasOccupied = occupied != null && occupied.Count > 0;
            bool hasObstacles = obstacles != null && obstacles.Count > 0;
            if (!hasOccupied && !hasObstacles) return baseCoord;

            float best = baseCoord;
            for (int attempt = 0; attempt < MaxLaneTries; attempt++)
            {
                // Try alternating sides: 0, +lane, -lane, +2lane, -2lane...
                int n = (attempt + 1) / 2;
                float sign = (attempt % 2 == 0) ? 1f : -1f;
                float candidate = baseCoord + sign * n * LaneWidth;

                // Build a test segment at this candidate coord
                float x1, y1, x2, y2;
                if (horizontalRun)
                {
                    x1 = Math.Min(spanA, spanB); x2 = Math.Max(spanA, spanB);
                    y1 = candidate; y2 = candidate;
                }
                else
                {
                    y1 = Math.Min(spanA, spanB); y2 = Math.Max(spanA, spanB);
                    x1 = candidate; x2 = candidate;
                }

                bool overlapsOtherEdge = hasOccupied && GetOverlapPenalty(x1, y1, x2, y2, occupied) > 0.01f;
                bool cutsThroughNode = hasObstacles && IsSegmentBlocked(x1, y1, x2, y2, obstacles);
                if (!overlapsOtherEdge && !cutsThroughNode)
                {
                    return candidate;
                }
                best = candidate;
            }
            return best;
        }

        // ----------------------------------------------------------------
        //  Feature 4: Inheritance trunk routing
        //  Groups of 2+ edges that share a target node AND a style kind
        //  (Inherits or Implements) are drawn as a merged UML "fork":
        //  children converge on a shared line perpendicular to the parent's
        //  entry side before entering via a single arrowhead.
        // ----------------------------------------------------------------

        private void RouteInheritanceTrunks(
            List<EdgeInfo> infos,
            PointF[] starts, PointF[] ends,
            PointF[] stubs1, PointF[] stubs2,
            Dictionary<string, RectangleF> nodeBounds,
            List<RoutedSeg> occupied,
            List<List<PointF>> routedPoints,
            bool[] trunkRouted,
            float[] xs, float[] ys,
            bool allowAStarFallback)
        {
            // Group (targetId, styleKind) → list of edge indices.
            // Per user decision: Inherits and Implements get *separate* trunks.
            var groups = new Dictionary<string, List<int>>();
            for (int i = 0; i < infos.Count; i++)
            {
                EdgeVisual edge = infos[i].Edge;
                string kind = GetTrunkKind(edge);
                if (kind == null) continue;
                string key = edge.TargetNodeId + "|" + kind;
                List<int> lst;
                if (!groups.TryGetValue(key, out lst))
                {
                    lst = new List<int>();
                    groups[key] = lst;
                }
                lst.Add(i);
            }

            foreach (var kvp in groups)
            {
                List<int> members = kvp.Value;
                if (members.Count < 2) continue;

                string targetId = infos[members[0]].Edge.TargetNodeId;
                RectangleF tgtBounds;
                if (!nodeBounds.TryGetValue(targetId, out tgtBounds)) continue;

                int entrySide = PickMajorityEntrySide(infos, members);
                PointF sharedTgtPort = GetSideCenter(tgtBounds, entrySide);
                PointF sharedTgtStub = GetStubPoint(sharedTgtPort, entrySide, Indentation);
                bool verticalEntry = (entrySide == 0 || entrySide == 2);

                // Obstacles for this group's routing exclude every member's own source (siblings
                // converging on the same trunk legitimately sit close to the shared line and to
                // each other — that's the point of a trunk) and the shared target.
                var memberSourceIds = new HashSet<string>();
                for (int m2 = 0; m2 < members.Count; m2++)
                    memberSourceIds.Add(infos[members[m2]].Edge.SourceNodeId);

                var groupObs = new List<RectangleF>();
                foreach (var nb in nodeBounds)
                {
                    if (nb.Key != targetId && !memberSourceIds.Contains(nb.Key))
                        groupObs.Add(nb.Value);
                }

                // The shared trunk line spans every participating member's stub coordinate (not
                // just one), so it has to be checked — and cleared — across that whole span, not
                // just a single point. Two unrelated trunks (e.g. Parrot's Trainable trunk and
                // GuideDog's Trainable trunk, or a completely different target's trunk) can easily
                // pick the same "Indentation + TrunkGap * n" offset and stack directly on top of
                // each other; searching for a lane that's actually clear of both node obstacles
                // and already-registered edge segments (from earlier-processed groups in this same
                // pass) prevents that, instead of just numbering trunks in dictionary-iteration order.
                float spanMin = verticalEntry ? sharedTgtStub.X : sharedTgtStub.Y;
                float spanMax = spanMin;
                for (int m2 = 0; m2 < members.Count; m2++)
                {
                    EdgeInfo mi = infos[members[m2]];
                    if (mi.EntrySide != entrySide || mi.Edge.SourceNodeId == mi.Edge.TargetNodeId) continue;
                    float coord = verticalEntry ? stubs1[members[m2]].X : stubs1[members[m2]].Y;
                    if (coord < spanMin) spanMin = coord;
                    if (coord > spanMax) spanMax = coord;
                }

                PointF trunkFar = FindClearTrunkFar(sharedTgtPort, entrySide, spanMin, spanMax, groupObs, occupied);

                for (int m = 0; m < members.Count; m++)
                {
                    int idx = members[m];
                    EdgeInfo info = infos[idx];

                    // Children approaching from a different side fall through to
                    // normal per-edge routing — don't force them into this trunk.
                    if (info.EntrySide != entrySide) continue;
                    if (info.Edge.SourceNodeId == info.Edge.TargetNodeId) continue;

                    PointF srcStart = starts[idx];
                    PointF srcStub = stubs1[idx];

                    PointF corner1, corner2;
                    if (verticalEntry)
                    {
                        corner1 = new PointF(srcStub.X, trunkFar.Y);
                        corner2 = new PointF(sharedTgtStub.X, trunkFar.Y);
                    }
                    else
                    {
                        corner1 = new PointF(trunkFar.X, srcStub.Y);
                        corner2 = new PointF(trunkFar.X, sharedTgtStub.Y);
                    }

                    // The straight elbow above assumes nothing else sits between the child
                    // and the shared trunk line. When some unrelated node (not part of this
                    // trunk) is in the way, fall back to obstacle-aware A* for just this
                    // child's own approach segment — it still converges on sharedTgtStub, so
                    // the merged-fork look at the target is preserved.
                    bool blocked =
                        IsSegmentBlocked(srcStub.X, srcStub.Y, corner1.X, corner1.Y, groupObs) ||
                        IsSegmentBlocked(corner1.X, corner1.Y, corner2.X, corner2.Y, groupObs) ||
                        IsSegmentBlocked(corner2.X, corner2.Y, sharedTgtStub.X, sharedTgtStub.Y, groupObs);

                    List<PointF> route = new List<PointF>();
                    route.Add(srcStub);
                    if (blocked && allowAStarFallback)
                    {
                        PointF[] avoided = FindPath(srcStub, sharedTgtStub, xs, ys, groupObs, occupied,
                            info.ExitSide, entrySide);
                        for (int r = 0; r < avoided.Length; r++) route.Add(avoided[r]);
                    }
                    else
                    {
                        // Either genuinely clear, or the grid's too large to afford full A* here —
                        // either way, degrade to the straight elbow rather than pay for FindPath's
                        // linear-open-list A* search over the whole W*H coordinate grid.
                        route.Add(corner1);
                        route.Add(corner2);
                    }
                    route.Add(sharedTgtStub);

                    route = SimplifyPath(route);
                    PointF[] routeArr = route.ToArray();
                    info.Edge.UpdateRoute(srcStart, sharedTgtPort, routeArr);

                    RegisterSegments(occupied, srcStart, sharedTgtPort, routeArr);

                    var allPts = new List<PointF>();
                    allPts.Add(srcStart);
                    for (int j = 0; j < routeArr.Length; j++) allPts.Add(routeArr[j]);
                    allPts.Add(sharedTgtPort);
                    routedPoints[idx] = allPts;

                    trunkRouted[idx] = true;
                }
            }
        }

        // Searches increasing TrunkGap offsets from the target for a trunk line whose full span
        // (spanMin..spanMax on the perpendicular axis) clears both node obstacles and already-
        // registered edge segments, so two unrelated trunks converging on the same target side
        // (e.g. Parrot's and GuideDog's separate "Trainable" trunks) don't stack their shared
        // lines directly on top of each other. Falls back to the farthest tried offset if nothing
        // fully clears — never blocks rendering, just degrades to the old fixed-offset behavior.
        private static PointF FindClearTrunkFar(PointF sharedTgtPort, int entrySide,
            float spanMin, float spanMax, List<RectangleF> obstacles, List<RoutedSeg> occupied)
        {
            PointF best = sharedTgtPort;
            bool vertical = (entrySide == 0 || entrySide == 2);
            for (int attempt = 1; attempt <= MaxLaneTries; attempt++)
            {
                PointF candidate = GetStubPoint(sharedTgtPort, entrySide, Indentation + TrunkGap * attempt);
                best = candidate;

                float x1, y1, x2, y2;
                if (vertical) { x1 = spanMin; x2 = spanMax; y1 = candidate.Y; y2 = candidate.Y; }
                else { y1 = spanMin; y2 = spanMax; x1 = candidate.X; x2 = candidate.X; }

                bool blocked = IsSegmentBlocked(x1, y1, x2, y2, obstacles)
                    || GetOverlapPenalty(x1, y1, x2, y2, occupied) > 0.01f;
                if (!blocked) return candidate;
            }
            return best;
        }

        private static string GetTrunkKind(EdgeVisual edge)
        {
            if (edge == null || edge.Style == null) return null;
            string name = edge.Style.GetType().Name;
            if (name == "InheritsEdgeStyle") return "Inherits";
            if (name == "ImplementsEdgeStyle") return "Implements";
            return null;
        }

        private static int PickMajorityEntrySide(List<EdgeInfo> infos, List<int> members)
        {
            int[] counts = new int[4];
            for (int i = 0; i < members.Count; i++)
            {
                int side = infos[members[i]].EntrySide;
                if (side >= 0 && side < 4) counts[side]++;
            }
            int best = 0;
            for (int i = 1; i < 4; i++) if (counts[i] > counts[best]) best = i;
            return best;
        }

        private static PointF[] FallbackRoute(PointF start, PointF end,
            int exitSide, int entrySide)
        {
            // Simple L-shaped route with stubs
            PointF s1 = GetStubPoint(start, exitSide, Indentation);
            PointF s2 = GetStubPoint(end, entrySide, Indentation);
            bool exitV = (exitSide == 0 || exitSide == 2);

            if (exitV)
            {
                float midY = (s1.Y + s2.Y) / 2f;
                return new PointF[]
                {
                    s1,
                    new PointF(s1.X, midY),
                    new PointF(s2.X, midY),
                    s2
                };
            }
            else
            {
                float midX = (s1.X + s2.X) / 2f;
                return new PointF[]
                {
                    s1,
                    new PointF(midX, s1.Y),
                    new PointF(midX, s2.Y),
                    s2
                };
            }
        }

        // ----------------------------------------------------------------
        //  Connection side selection  (0=top, 1=right, 2=bottom, 3=left)
        // ----------------------------------------------------------------

        private static void ChooseConnectionSides(RectangleF src, RectangleF tgt,
            out int exitSide, out int entrySide)
        {
            PointF sc = CenterOf(src);
            PointF tc = CenterOf(tgt);
            float dx = tc.X - sc.X;
            float dy = tc.Y - sc.Y;

            if (Math.Abs(dy) > Math.Abs(dx))
            {
                if (dy > 0) { exitSide = 2; entrySide = 0; }
                else        { exitSide = 0; entrySide = 2; }
            }
            else
            {
                if (dx > 0) { exitSide = 1; entrySide = 3; }
                else        { exitSide = 3; entrySide = 1; }
            }
        }

        // ----------------------------------------------------------------
        //  Crossing detection
        // ----------------------------------------------------------------

        private void ComputeCrossings(List<EdgeInfo> infos)
        {
            var allSegs = new List<SegRef>();
            for (int ei = 0; ei < infos.Count; ei++)
            {
                List<PointF> pts = infos[ei].Edge.GetFullPath();
                for (int si = 0; si < pts.Count - 1; si++)
                {
                    allSegs.Add(new SegRef
                    {
                        EdgeIndex = ei,
                        SegIndex = si,
                        A = pts[si],
                        B = pts[si + 1],
                        IsHorizontal = Math.Abs(pts[si].Y - pts[si + 1].Y) < 0.5f
                    });
                }
            }

            for (int i = 0; i < allSegs.Count; i++)
            {
                for (int j = i + 1; j < allSegs.Count; j++)
                {
                    if (allSegs[i].EdgeIndex == allSegs[j].EdgeIndex) continue;
                    if (allSegs[i].IsHorizontal == allSegs[j].IsHorizontal) continue;

                    SegRef hSeg = allSegs[i].IsHorizontal ? allSegs[i] : allSegs[j];
                    SegRef vSeg = allSegs[i].IsHorizontal ? allSegs[j] : allSegs[i];

                    float hY = hSeg.A.Y;
                    float vX = vSeg.A.X;

                    float hMinX = Math.Min(hSeg.A.X, hSeg.B.X);
                    float hMaxX = Math.Max(hSeg.A.X, hSeg.B.X);
                    float vMinY = Math.Min(vSeg.A.Y, vSeg.B.Y);
                    float vMaxY = Math.Max(vSeg.A.Y, vSeg.B.Y);

                    if (vX > hMinX + 2f && vX < hMaxX - 2f &&
                        hY > vMinY + 2f && hY < vMaxY - 2f)
                    {
                        PointF crossPt = new PointF(vX, hY);
                        infos[hSeg.EdgeIndex].Edge.AddCrossing(hSeg.SegIndex, crossPt);
                    }
                }
            }
        }

        // ----------------------------------------------------------------
        //  Port distribution
        // ----------------------------------------------------------------

        private static PointF GetDistributedPort(RectangleF bounds, int side,
            List<int> group, int currentIndex)
        {
            int count = group.Count;
            int slot = group.IndexOf(currentIndex);
            if (slot < 0) slot = 0;

            bool horiz = (side == 0 || side == 2);
            float length = horiz ? bounds.Width : bounds.Height;
            float usable = length - 2f * PortMargin;
            float spacing = count > 1 ? usable / (count - 1) : 0f;
            float offset = count > 1 ? PortMargin + slot * spacing : length / 2f;

            switch (side)
            {
                case 0: return new PointF(bounds.Left + offset, bounds.Top);
                case 1: return new PointF(bounds.Right, bounds.Top + offset);
                case 2: return new PointF(bounds.Left + offset, bounds.Bottom);
                case 3: return new PointF(bounds.Left, bounds.Top + offset);
                default: return CenterOf(bounds);
            }
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        private static float ManhattanDist(RectangleF a, RectangleF b)
        {
            PointF ca = CenterOf(a);
            PointF cb = CenterOf(b);
            return Math.Abs(ca.X - cb.X) + Math.Abs(ca.Y - cb.Y);
        }

        private static PointF CenterOf(RectangleF r)
        {
            return new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        }

        private static PointF GetSideCenter(RectangleF r, int side)
        {
            switch (side)
            {
                case 0: return new PointF(r.X + r.Width / 2f, r.Top);
                case 1: return new PointF(r.Right, r.Y + r.Height / 2f);
                case 2: return new PointF(r.X + r.Width / 2f, r.Bottom);
                case 3: return new PointF(r.Left, r.Y + r.Height / 2f);
                default: return CenterOf(r);
            }
        }

        private static int FindNearest(float[] arr, float value)
        {
            int best = 0;
            float bestDist = Math.Abs(arr[0] - value);
            for (int i = 1; i < arr.Length; i++)
            {
                float d = Math.Abs(arr[i] - value);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private static void AddUnique(List<float> list, float value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Math.Abs(list[i] - value) < 0.5f) return;
            }
            list.Add(value);
        }

        private static bool TryGetBounds(Dictionary<string, RectangleF> map, EdgeVisual edge,
            out RectangleF srcB, out RectangleF tgtB)
        {
            srcB = tgtB = RectangleF.Empty;
            return map.TryGetValue(edge.SourceNodeId, out srcB)
                && map.TryGetValue(edge.TargetNodeId, out tgtB);
        }

        private static void AddToGroup(Dictionary<string, List<int>> groups, string key, int idx)
        {
            List<int> list;
            if (!groups.TryGetValue(key, out list))
            {
                list = new List<int>();
                groups[key] = list;
            }
            list.Add(idx);
        }

        // ----------------------------------------------------------------
        //  Inner types
        // ----------------------------------------------------------------

        private struct EdgeInfo
        {
            public EdgeVisual Edge;
            public RectangleF SrcBounds, TgtBounds;
            public int ExitSide, EntrySide;
        }

        private struct SegRef
        {
            public int EdgeIndex;
            public int SegIndex;
            public PointF A, B;
            public bool IsHorizontal;
        }

        private struct RoutedSeg
        {
            public bool IsH;
            public float Pos;
            public float Min, Max;
        }

        // ----------------------------------------------------------------
        //  Circle border intersection
        // ----------------------------------------------------------------

        /// <summary>
        /// Computes the point on the circle perimeter (inscribed in the given bounds)
        /// along the line from the circle center to the specified external point.
        /// If the external point coincides with the center, defaults to the right side.
        /// </summary>
        public static PointF GetCircleBorderIntersection(RectangleF bounds, PointF externalPoint)
        {
            float cx = bounds.X + bounds.Width / 2f;
            float cy = bounds.Y + bounds.Height / 2f;
            float radius = bounds.Width / 2f;

            float dx = externalPoint.X - cx;
            float dy = externalPoint.Y - cy;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            // Edge case: external point is at the center; default to right
            if (length < 0.0001f)
            {
                return new PointF(cx + radius, cy);
            }

            // Normalize direction and multiply by radius
            float nx = dx / length;
            float ny = dy / length;

            return new PointF(cx + nx * radius, cy + ny * radius);
        }

        // ----------------------------------------------------------------
        //  Border intersection (used externally)
        // ----------------------------------------------------------------

        public static PointF GetBorderIntersection(RectangleF rect, PointF inside, PointF outside)
        {
            float dx = outside.X - inside.X;
            float dy = outside.Y - inside.Y;
            if (Math.Abs(dx) < 0.0001f && Math.Abs(dy) < 0.0001f) return inside;

            float tMin = float.MaxValue;

            if (Math.Abs(dx) > 0.0001f)
            {
                float t = (rect.Left - inside.X) / dx;
                if (t > 0 && t < tMin) { float y = inside.Y + t * dy; if (y >= rect.Top && y <= rect.Bottom) tMin = t; }
                t = (rect.Right - inside.X) / dx;
                if (t > 0 && t < tMin) { float y = inside.Y + t * dy; if (y >= rect.Top && y <= rect.Bottom) tMin = t; }
            }
            if (Math.Abs(dy) > 0.0001f)
            {
                float t = (rect.Top - inside.Y) / dy;
                if (t > 0 && t < tMin) { float x = inside.X + t * dx; if (x >= rect.Left && x <= rect.Right) tMin = t; }
                t = (rect.Bottom - inside.Y) / dy;
                if (t > 0 && t < tMin) { float x = inside.X + t * dx; if (x >= rect.Left && x <= rect.Right) tMin = t; }
            }

            if (tMin < float.MaxValue)
                return new PointF(inside.X + tMin * dx, inside.Y + tMin * dy);
            return inside;
        }
    }
}
