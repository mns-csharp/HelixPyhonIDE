namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Renders the full scene graph to an SVG XML string for vector export.
    /// Does not rely on System.Drawing beyond colour / size types that translate
    /// straight to SVG attributes.
    /// </summary>
    public static class CanvasSvgRenderer
    {
        private const float Padding = 24f;
        private const string XmlNs = "http://www.w3.org/2000/svg";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Render(SceneGraph scene, Color background)
        {
            if (scene == null) throw new ArgumentNullException("scene");

            RectangleF contentBounds = scene.ComputeContentBounds();
            if (contentBounds.IsEmpty || contentBounds.Width <= 0 || contentBounds.Height <= 0)
            {
                contentBounds = new RectangleF(0, 0, 10, 10);
            }

            float width = contentBounds.Width + Padding * 2f;
            float height = contentBounds.Height + Padding * 2f;

            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n");
            sb.AppendFormat(Inv,
                "<svg xmlns=\"{0}\" width=\"{1}\" height=\"{2}\" viewBox=\"0 0 {1} {2}\">\n",
                XmlNs, F(width), F(height));

            sb.AppendFormat(Inv, "<rect x=\"0\" y=\"0\" width=\"{0}\" height=\"{1}\" fill=\"{2}\" />\n",
                F(width), F(height), Hex(background));

            sb.AppendFormat(Inv, "<g transform=\"translate({0},{1})\">\n",
                F(Padding - contentBounds.X), F(Padding - contentBounds.Y));

            // Edges first (behind nodes)
            for (int i = 0; i < scene.Edges.Count; i++)
            {
                EmitEdge(sb, scene.Edges[i]);
            }

            // Nodes on top
            for (int i = 0; i < scene.Nodes.Count; i++)
            {
                EmitNode(sb, scene.Nodes[i]);
            }

            sb.Append("</g>\n</svg>\n");
            return sb.ToString();
        }

        private static void EmitNode(StringBuilder sb, NodeVisual node)
        {
            if (node == null || node.Style == null) return;

            RectangleF bounds = node.WorldBounds;
            NodeStyleInfo style = node.Style.GetStyle(false);

            if (node.IsCircleMode)
            {
                float cx = bounds.X + bounds.Width / 2f;
                float cy = bounds.Y + bounds.Height / 2f;
                float r = Math.Min(bounds.Width, bounds.Height) / 2f;
                sb.AppendFormat(Inv,
                    "  <circle cx=\"{0}\" cy=\"{1}\" r=\"{2}\" fill=\"{3}\" stroke=\"{4}\" stroke-width=\"{5}\" />\n",
                    F(cx), F(cy), F(r), Hex(style.HeaderFillColor), Hex(style.BorderColor), F(style.BorderWidth));
                sb.AppendFormat(Inv,
                    "  <text x=\"{0}\" y=\"{1}\" fill=\"{2}\" text-anchor=\"middle\" font-family=\"Segoe UI\" font-size=\"11\">{3}</text>\n",
                    F(cx), F(cy + 4f), Hex(style.TextColor), XmlEscape(node.DisplayName));
                return;
            }

            // Body (rounded rect approximated with rx)
            sb.AppendFormat(Inv,
                "  <rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" rx=\"8\" ry=\"8\" fill=\"{4}\" stroke=\"{5}\" stroke-width=\"{6}\"",
                F(bounds.X), F(bounds.Y), F(bounds.Width), F(bounds.Height),
                Hex(style.BodyFillColor), Hex(style.BorderColor), F(style.BorderWidth));
            if (style.BorderDashStyle == DashStyle.Dash || style.BorderDashStyle == DashStyle.DashDot)
            {
                sb.Append(" stroke-dasharray=\"6,4\"");
            }
            sb.Append(" />\n");

            // Header band (top 22 px)
            float headerH = 22f;
            sb.AppendFormat(Inv,
                "  <path d=\"M {0},{1} H {2} V {3} H {0} Z\" fill=\"{4}\" />\n",
                F(bounds.X), F(bounds.Y + 8f), F(bounds.X + bounds.Width), F(bounds.Y + headerH),
                Hex(style.HeaderFillColor));

            // Kind label (italic)
            float textX = bounds.X + bounds.Width / 2f;
            float cursorY = bounds.Y + 12f;
            if (!string.IsNullOrEmpty(node.TypeKindLabel))
            {
                sb.AppendFormat(Inv,
                    "  <text x=\"{0}\" y=\"{1}\" fill=\"{2}\" text-anchor=\"middle\" font-family=\"Segoe UI\" font-size=\"9\" font-style=\"italic\">{3}</text>\n",
                    F(textX), F(cursorY), Hex(style.TextColor), XmlEscape(node.TypeKindLabel));
                cursorY += 12f;
            }

            // Name + optional generics
            string title = node.DisplayName;
            if (!string.IsNullOrEmpty(node.GenericParams))
            {
                title = title + "<" + node.GenericParams + ">";
            }
            sb.AppendFormat(Inv,
                "  <text x=\"{0}\" y=\"{1}\" fill=\"{2}\" text-anchor=\"middle\" font-family=\"Segoe UI\" font-size=\"10\" font-weight=\"bold\">{3}</text>\n",
                F(textX), F(cursorY + 6f), Hex(style.TextColor), XmlEscape(title));
            cursorY += 18f;

            // Divider
            sb.AppendFormat(Inv,
                "  <line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{1}\" stroke=\"{3}\" stroke-width=\"0.5\" />\n",
                F(bounds.X + 4f), F(cursorY), F(bounds.X + bounds.Width - 4f), Hex(style.BorderColor));
            cursorY += 6f;

            // Members
            IList<string> lines = node.MemberLines;
            float lineHeight = 11f;
            float textLeftX = bounds.X + 8f;
            float textBottomLimit = bounds.Y + bounds.Height - 4f;
            for (int i = 0; i < lines.Count && cursorY + lineHeight < textBottomLimit; i++)
            {
                sb.AppendFormat(Inv,
                    "  <text x=\"{0}\" y=\"{1}\" fill=\"{2}\" font-family=\"Segoe UI\" font-size=\"8.5\">{3}</text>\n",
                    F(textLeftX), F(cursorY + lineHeight), Hex(style.TextColor), XmlEscape(lines[i]));
                cursorY += lineHeight;
            }
        }

        private static void EmitEdge(StringBuilder sb, EdgeVisual edge)
        {
            if (edge == null || edge.Style == null) return;

            EdgeStyleInfo style = edge.Style.GetStyle(false);
            PointF start = edge.StartPoint;
            PointF end = edge.EndPoint;
            IReadOnlyList<PointF> routePoints = edge.RoutePoints;

            StringBuilder points = new StringBuilder();
            points.AppendFormat(Inv, "{0},{1}", F(start.X), F(start.Y));
            for (int i = 0; i < routePoints.Count; i++)
            {
                points.AppendFormat(Inv, " {0},{1}", F(routePoints[i].X), F(routePoints[i].Y));
            }
            points.AppendFormat(Inv, " {0},{1}", F(end.X), F(end.Y));

            string dashAttr = string.Empty;
            if (style.LineDashStyle == DashStyle.Dash)
            {
                dashAttr = " stroke-dasharray=\"6,4\"";
            }

            sb.AppendFormat(Inv,
                "  <polyline points=\"{0}\" fill=\"none\" stroke=\"{1}\" stroke-width=\"{2}\"{3} />\n",
                points, Hex(style.LineColor), F(style.LineWidth), dashAttr);

            if (style.DrawArrowhead || style.DrawHollowTriangle)
            {
                EmitArrowHead(sb, style, routePoints, start, end);
            }
        }

        private static void EmitArrowHead(StringBuilder sb, EdgeStyleInfo style,
            IReadOnlyList<PointF> routePoints, PointF start, PointF end)
        {
            // Determine the segment entering the target (last waypoint → end).
            PointF prev = routePoints.Count > 0 ? routePoints[routePoints.Count - 1] : start;

            float dx = end.X - prev.X;
            float dy = end.Y - prev.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;

            float ux = dx / len;
            float uy = dy / len;
            float px = -uy;
            float py = ux;

            float size = Math.Max(8f, style.LineWidth * 4f);
            PointF tip = end;
            PointF baseL = new PointF(end.X - ux * size + px * size * 0.5f,
                                      end.Y - uy * size + py * size * 0.5f);
            PointF baseR = new PointF(end.X - ux * size - px * size * 0.5f,
                                      end.Y - uy * size - py * size * 0.5f);

            string fill = style.DrawHollowTriangle ? "#FFFFFF" : Hex(style.LineColor);

            sb.AppendFormat(Inv,
                "  <polygon points=\"{0},{1} {2},{3} {4},{5}\" fill=\"{6}\" stroke=\"{7}\" stroke-width=\"{8}\" />\n",
                F(tip.X), F(tip.Y), F(baseL.X), F(baseL.Y), F(baseR.X), F(baseR.Y),
                fill, Hex(style.LineColor), F(style.LineWidth));
        }

        private static string F(float v)
        {
            return v.ToString("0.##", Inv);
        }

        private static string Hex(Color c)
        {
            return string.Format(Inv, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }
}
