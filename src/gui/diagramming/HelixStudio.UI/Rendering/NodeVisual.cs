namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Text;

    using HelixPythonNamespace.UI.Controls;
    public sealed class NodeVisual : Visual, ISelectable
    {
        // --- Static cached GDI+ resources (R1.1, R1.3) ---
        // Fonts used for layout measurement (fixed size, not zoom-dependent)
        private static readonly Font s_layoutHeaderFont = new Font(LunaFonts.Ui.FontFamily, 8f, FontStyle.Bold);
        private static readonly Font s_layoutMemberFont = new Font(LunaFonts.Ui.FontFamily, 7f, FontStyle.Regular);

        // Brushes that never change color
        private static readonly SolidBrush s_shadowBrush = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
        private static readonly SolidBrush s_grayBrush = new SolidBrush(Color.Gray);
        private static readonly Pen s_selectionGlowPen = new Pen(Color.FromArgb(80, 0, 120, 215), 1f);

        // StringFormat objects (reusable, thread-safe for reading)
        private static readonly StringFormat s_centerFormat;
        private static readonly StringFormat s_leftFormat;

        static NodeVisual()
        {
            s_centerFormat = new StringFormat();
            s_centerFormat.Alignment = StringAlignment.Center;
            s_centerFormat.LineAlignment = StringAlignment.Near;
            s_centerFormat.Trimming = StringTrimming.EllipsisCharacter;

            s_leftFormat = new StringFormat();
            s_leftFormat.Alignment = StringAlignment.Near;
            s_leftFormat.LineAlignment = StringAlignment.Near;
            s_leftFormat.Trimming = StringTrimming.EllipsisCharacter;
        }

        // Cached zoom-dependent fonts (regenerated when zoom changes)
        private static float s_lastHeaderFontSize;
        private static float s_lastItalicFontSize;
        private static float s_lastMemberFontSize;
        private static float s_lastCircleFontSize;
        private static Font s_cachedHeaderFont;
        private static Font s_cachedItalicFont;
        private static Font s_cachedMemberFont;
        private static Font s_cachedCircleFont;

        private INodeStyleStrategy _styleStrategy;
        private readonly string _nodeId;
        private bool _isSelected;
        private bool _isCircleMode;

        /// <summary>Number of SOLID-analysis violations currently attached to this node.</summary>
        public int ViolationCount { get; set; }

        /// <summary>Short multi-line summary of violation messages (for tooltip).</summary>
        public string ViolationSummary { get; set; }

        // Container node data for hierarchical navigation
        private HelixPythonNamespace.UI.Projection.ContainerNodeData _containerData;

        // Cached layout data
        private string _displayName;
        private string _typeKindLabel;
        private string _genericParams;
        private List<string> _memberLines;
        private float _headerHeight;
        private float _memberAreaHeight;

        public NodeVisual(string nodeId, RectangleF worldBounds, INodeStyleStrategy styleStrategy)
            : base(worldBounds, ZOrders.Node)
        {
            if (nodeId == null)
            {
                throw new ArgumentNullException("nodeId");
            }
            if (styleStrategy == null)
            {
                throw new ArgumentNullException("styleStrategy");
            }

            _nodeId = nodeId;
            _styleStrategy = styleStrategy;
            _displayName = string.Empty;
            _typeKindLabel = string.Empty;
            _genericParams = string.Empty;
            _memberLines = new List<string>();
        }

        public string NodeId
        {
            get { return _nodeId; }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; }
        }

        /// <summary>
        /// Container data for hierarchical navigation nodes (solution, project, namespace).
        /// Null for regular type nodes.
        /// </summary>
        public HelixPythonNamespace.UI.Projection.ContainerNodeData ContainerData
        {
            get { return _containerData; }
            set { _containerData = value; }
        }

        /// <summary>
        /// When true, the node renders as a compact circle instead of a full UML box.
        /// Used on filtered category tabs for types that are not the primary category.
        /// </summary>
        public bool IsCircleMode
        {
            get { return _isCircleMode; }
            set
            {
                if (_isCircleMode != value)
                {
                    _isCircleMode = value;

                    // Resize WorldBounds immediately rather than waiting for MeasureLayout (which
                    // needs a live Graphics and only runs during paint). EdgeRouter.RouteAllEdges
                    // runs right after node creation, before the first paint — routing against the
                    // stale full-size rectangle here left every route pointing at where the old,
                    // larger box's edge used to be once the node actually shrank to a circle on
                    // screen, showing up as arrows that visibly don't reach the circle.
                    RectangleF current = WorldBounds;
                    if (_isCircleMode)
                    {
                        float d = LayoutConstants.CircleDiameter;
                        WorldBounds = new RectangleF(current.X, current.Y, d, d);
                    }
                    else
                    {
                        WorldBounds = new RectangleF(current.X, current.Y, LayoutConstants.NodeWidth, LayoutConstants.NodeHeight);
                    }

                    IsDirty = true;
                }
            }
        }

        public void SetStyle(INodeStyleStrategy styleStrategy)
        {
            if (styleStrategy == null)
            {
                throw new ArgumentNullException("styleStrategy");
            }
            _styleStrategy = styleStrategy;
            IsDirty = true;
        }

        public void SetPosition(float x, float y)
        {
            RectangleF current = WorldBounds;
            WorldBounds = new RectangleF(x, y, current.Width, current.Height);
        }

        /// <summary>
        /// Replace the full bounds (x / y / width / height). Used by the nested-type
        /// layout pass when a parent needs to grow to enclose its children.
        /// </summary>
        public void SetBounds(RectangleF bounds)
        {
            WorldBounds = bounds;
        }

        /// <summary>
        /// Gets the full text content for tooltip display.
        /// </summary>
        public string GetTooltipText()
        {
            var sb = new System.Text.StringBuilder();

            // Type name with generics
            string nameDisplay = _displayName;
            if (!string.IsNullOrEmpty(_genericParams))
            {
                nameDisplay = string.Format("{0}<{1}>", _displayName, _genericParams);
            }

            if (!string.IsNullOrEmpty(_typeKindLabel))
            {
                sb.AppendLine(_typeKindLabel);
            }
            sb.AppendLine(nameDisplay);

            if (_memberLines.Count > 0)
            {
                sb.AppendLine("─────────────");
                for (int i = 0; i < _memberLines.Count; i++)
                {
                    sb.AppendLine(_memberLines[i]);
                }
            }

            if (ViolationCount > 0 && !string.IsNullOrEmpty(ViolationSummary))
            {
                sb.AppendLine("─────────────");
                sb.AppendLine(string.Format("⚠ {0} analysis violation(s):", ViolationCount));
                sb.AppendLine(ViolationSummary);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Gets member lines for external access.
        /// </summary>
        public IList<string> MemberLines
        {
            get { return _memberLines.AsReadOnly(); }
        }

        /// <summary>Short display name (e.g. "Foo"), excluding generic parameters.</summary>
        public string DisplayName
        {
            get { return _displayName ?? string.Empty; }
        }

        /// <summary>Italicized kind label (e.g. "«interface»"); may be empty.</summary>
        public string TypeKindLabel
        {
            get { return _typeKindLabel ?? string.Empty; }
        }

        /// <summary>Comma-separated generic type parameter names, excluding angle brackets.</summary>
        public string GenericParams
        {
            get { return _genericParams ?? string.Empty; }
        }

        /// <summary>Current node style strategy (used by offscreen renderers).</summary>
        public INodeStyleStrategy Style
        {
            get { return _styleStrategy; }
        }

        public void UpdateFromModel(string displayName, string typeKindLabel, string genericParams, IEnumerable<string> memberLines)
        {
            _displayName = displayName ?? string.Empty;
            _typeKindLabel = typeKindLabel ?? string.Empty;
            _genericParams = genericParams ?? string.Empty;
            _memberLines = new List<string>();
            if (memberLines != null)
            {
                foreach (string line in memberLines)
                {
                    _memberLines.Add(line);
                }
            }
            IsDirty = true;
        }

        public void MeasureLayout(Graphics g, Font headerFont, Font memberFont)
        {
            if (g == null)
            {
                return;
            }

            if (_isCircleMode)
            {
                float d = LayoutConstants.CircleDiameter;
                RectangleF current = WorldBounds;
                WorldBounds = new RectangleF(current.X, current.Y, d, d);
                IsDirty = false;
                return;
            }

            float padding = 8f;
            float lineSpacing = 2f;

            // Calculate header height for proper text layout within fixed bounds
            _headerHeight = padding;

            if (!string.IsNullOrEmpty(_typeKindLabel))
            {
                SizeF kindSize = g.MeasureString(_typeKindLabel, headerFont);
                _headerHeight += kindSize.Height + lineSpacing;
            }

            string nameDisplay = _displayName;
            if (!string.IsNullOrEmpty(_genericParams))
            {
                nameDisplay = string.Format("{0}<{1}>", _displayName, _genericParams);
            }

            SizeF nameSize = g.MeasureString(nameDisplay, headerFont);
            _headerHeight += nameSize.Height + padding;

            // Calculate member area height (remaining space in fixed bounds)
            _memberAreaHeight = LayoutConstants.NodeHeight - _headerHeight;
            if (_memberAreaHeight < 20f)
            {
                _memberAreaHeight = 20f;
            }

            // Use fixed dimensions instead of dynamic sizing
            RectangleF current2 = WorldBounds;
            WorldBounds = new RectangleF(current2.X, current2.Y,
                LayoutConstants.NodeWidth, LayoutConstants.NodeHeight);
            IsDirty = false;
        }

        /// <summary>
        /// Returns cached zoom-dependent fonts (header bold, italic, member regular).
        /// Fonts are regenerated only when the effective size changes.
        /// </summary>
        private static void GetZoomFonts(float zoom, out Font headerFont, out Font italicFont, out Font memberFont)
        {
            float hSize = Math.Max(8f * zoom, 4f);
            float sSize = Math.Max(7f * zoom, 3.5f);

            // Only recreate when the computed size actually changes
            if (s_cachedHeaderFont == null || Math.Abs(hSize - s_lastHeaderFontSize) > 0.01f)
            {
                if (s_cachedHeaderFont != null) { s_cachedHeaderFont.Dispose(); }
                s_cachedHeaderFont = new Font(LunaFonts.Ui.FontFamily, hSize, FontStyle.Bold);
                s_lastHeaderFontSize = hSize;
            }
            if (s_cachedItalicFont == null || Math.Abs(sSize - s_lastItalicFontSize) > 0.01f)
            {
                if (s_cachedItalicFont != null) { s_cachedItalicFont.Dispose(); }
                s_cachedItalicFont = new Font(LunaFonts.Ui.FontFamily, sSize, FontStyle.Italic);
                s_lastItalicFontSize = sSize;
            }
            if (s_cachedMemberFont == null || Math.Abs(sSize - s_lastMemberFontSize) > 0.01f)
            {
                if (s_cachedMemberFont != null) { s_cachedMemberFont.Dispose(); }
                s_cachedMemberFont = new Font(LunaFonts.Ui.FontFamily, sSize, FontStyle.Regular);
                s_lastMemberFontSize = sSize;
            }
            // Circle mode name font: same small size but Bold
            if (s_cachedCircleFont == null || Math.Abs(sSize - s_lastCircleFontSize) > 0.01f)
            {
                if (s_cachedCircleFont != null) { s_cachedCircleFont.Dispose(); }
                s_cachedCircleFont = new Font(LunaFonts.Ui.FontFamily, sSize, FontStyle.Bold);
                s_lastCircleFontSize = sSize;
            }

            headerFont = s_cachedHeaderFont;
            italicFont = s_cachedItalicFont;
            memberFont = s_cachedMemberFont;
        }

        public override void Render(Graphics g, ViewportTransform viewport)
        {
            if (g == null || viewport == null)
            {
                return;
            }

            // Measure layout if dirty (uses fixed-size layout fonts)
            if (IsDirty)
            {
                MeasureLayout(g, s_layoutHeaderFont, s_layoutMemberFont);
            }

            if (_isCircleMode)
            {
                RenderCircle(g, viewport);
                return;
            }

            NodeStyleInfo style = _styleStrategy.GetStyle(_isSelected);
            RectangleF screenRect = viewport.WorldToScreen(WorldBounds);

            float padding = 8f * viewport.Zoom;
            float lineSpacing = 2f * viewport.Zoom;
            float cornerRadius = 8f * viewport.Zoom;

            Font headerFont, italicFont, memberFont;
            GetZoomFonts(viewport.Zoom, out headerFont, out italicFont, out memberFont);

            // Draw drop shadow with rounded corners
            RectangleF shadowRect = new RectangleF(
                screenRect.X + 3f, screenRect.Y + 3f,
                screenRect.Width, screenRect.Height);
            using (GraphicsPath shadowPath = CreateRoundedRectPath(shadowRect, cornerRadius))
            {
                g.FillPath(s_shadowBrush, shadowPath);
            }

            // Draw body fill with rounded corners
            using (GraphicsPath bodyPath = CreateRoundedRectPath(screenRect, cornerRadius))
            using (SolidBrush bodyBrush = new SolidBrush(style.BodyFillColor))
            {
                g.FillPath(bodyBrush, bodyPath);
            }

            // Draw header band with rounded top corners
            float screenHeaderHeight = _headerHeight * viewport.Zoom;
            RectangleF headerRect = new RectangleF(
                screenRect.X, screenRect.Y,
                screenRect.Width, screenHeaderHeight);
            using (GraphicsPath headerPath = CreateRoundedRectPath(headerRect, cornerRadius, true, true, false, false))
            using (SolidBrush headerBrush = new SolidBrush(style.HeaderFillColor))
            {
                g.FillPath(headerBrush, headerPath);
            }

            // Draw text
            using (SolidBrush textBrush = new SolidBrush(style.TextColor))
            {
                float currentY = screenRect.Y + padding;

                // Type kind label (italic, centered)
                if (!string.IsNullOrEmpty(_typeKindLabel))
                {
                    RectangleF kindRect = new RectangleF(
                        screenRect.X, currentY,
                        screenRect.Width, italicFont.GetHeight(g));
                    g.DrawString(_typeKindLabel, italicFont, textBrush, kindRect, s_centerFormat);
                    currentY += italicFont.GetHeight(g) + lineSpacing;
                }

                // Name + generics (bold, centered)
                string nameDisplay = _displayName;
                if (!string.IsNullOrEmpty(_genericParams))
                {
                    nameDisplay = string.Format("{0}<{1}>", _displayName, _genericParams);
                }
                RectangleF nameRect = new RectangleF(
                    screenRect.X, currentY,
                    screenRect.Width, headerFont.GetHeight(g));
                g.DrawString(nameDisplay, headerFont, textBrush, nameRect, s_centerFormat);

                // Separator line
                float separatorY = screenRect.Y + screenHeaderHeight;
                using (Pen separatorPen = new Pen(style.BorderColor, 1f))
                {
                    g.DrawLine(separatorPen, screenRect.X + cornerRadius / 2, separatorY,
                        screenRect.X + screenRect.Width - cornerRadius / 2, separatorY);
                }

                // Member lines (left-aligned) with overflow handling
                float memberY = separatorY + padding;
                float availableHeight = screenRect.Bottom - memberY - padding;
                float memberLineHeight = memberFont.GetHeight(g) + lineSpacing;
                int maxVisibleMembers = (int)(availableHeight / memberLineHeight);

                int displayCount = Math.Min(_memberLines.Count, maxVisibleMembers);
                bool hasOverflow = _memberLines.Count > maxVisibleMembers;

                // If overflow, reserve one line for indicator
                if (hasOverflow && displayCount > 0)
                {
                    displayCount--;
                }

                for (int i = 0; i < displayCount; i++)
                {
                    RectangleF memberRect = new RectangleF(
                        screenRect.X + padding, memberY,
                        screenRect.Width - padding * 2, memberFont.GetHeight(g));
                    g.DrawString(_memberLines[i], memberFont, textBrush, memberRect, s_leftFormat);
                    memberY += memberFont.GetHeight(g) + lineSpacing;
                }

                // Draw overflow indicator
                if (hasOverflow)
                {
                    string overflowText = string.Format("+{0} more...", _memberLines.Count - displayCount);
                    RectangleF overflowRect = new RectangleF(
                        screenRect.X + padding, memberY,
                        screenRect.Width - padding * 2, memberFont.GetHeight(g));
                    g.DrawString(overflowText, memberFont, s_grayBrush, overflowRect, s_leftFormat);
                }
            }

            // Draw border with rounded corners
            using (GraphicsPath borderPath = CreateRoundedRectPath(screenRect, cornerRadius))
            using (Pen borderPen = new Pen(style.BorderColor, style.BorderWidth))
            {
                borderPen.DashStyle = style.BorderDashStyle;
                g.DrawPath(borderPen, borderPath);
            }

            // Selection highlight (blue glow) with rounded corners
            if (_isSelected)
            {
                RectangleF glowRect = new RectangleF(
                    screenRect.X - 2f, screenRect.Y - 2f,
                    screenRect.Width + 4f, screenRect.Height + 4f);
                using (GraphicsPath glowPath = CreateRoundedRectPath(glowRect, cornerRadius + 2f))
                {
                    s_selectionGlowPen.Width = style.BorderWidth + 4f;
                    g.DrawPath(s_selectionGlowPen, glowPath);
                }
            }

            // Violation badge (SOLID analysis). Drawn on top of everything else.
            if (ViolationCount > 0)
            {
                DrawViolationBadge(g, screenRect, viewport.Zoom);
            }
        }

        private void DrawViolationBadge(Graphics g, RectangleF screenRect, float zoom)
        {
            float size = 14f * zoom;
            if (size < 10f) size = 10f;
            if (size > 22f) size = 22f;

            float x = screenRect.Right - size - 2f;
            float y = screenRect.Y + 2f;
            RectangleF badge = new RectangleF(x, y, size, size);

            using (SolidBrush fill = new SolidBrush(Color.FromArgb(220, 60, 60)))
            {
                g.FillEllipse(fill, badge);
            }
            using (Pen border = new Pen(Color.White, 1.5f))
            {
                g.DrawEllipse(border, badge);
            }
            using (Font f = new Font(LunaFonts.Ui.FontFamily, size * 0.55f, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string label = ViolationCount > 9 ? "!" : ViolationCount.ToString();
                SizeF textSize = g.MeasureString(label, f);
                g.DrawString(label, f, textBrush,
                    badge.X + (size - textSize.Width) / 2f,
                    badge.Y + (size - textSize.Height) / 2f);
            }
        }

        private void RenderCircle(Graphics g, ViewportTransform viewport)
        {
            NodeStyleInfo style = _styleStrategy.GetStyle(_isSelected);
            RectangleF screenRect = viewport.WorldToScreen(WorldBounds);

            // Ensure zoom-dependent fonts are cached (also populates s_cachedCircleFont)
            Font headerFont, italicFont, memberFont;
            GetZoomFonts(viewport.Zoom, out headerFont, out italicFont, out memberFont);

            // Drop shadow
            RectangleF shadowRect = new RectangleF(
                screenRect.X + 2f, screenRect.Y + 2f,
                screenRect.Width, screenRect.Height);
            g.FillEllipse(s_shadowBrush, shadowRect);

            // Fill circle with header color
            using (SolidBrush fillBrush = new SolidBrush(style.HeaderFillColor))
            {
                g.FillEllipse(fillBrush, screenRect);
            }

            // Border
            using (Pen borderPen = new Pen(style.BorderColor, style.BorderWidth))
            {
                borderPen.DashStyle = style.BorderDashStyle;
                g.DrawEllipse(borderPen, screenRect);
            }

            // Type name centered in circle (use a center-center format for circle)
            using (SolidBrush textBrush = new SolidBrush(style.TextColor))
            {
                StringFormat circleCenterFormat = new StringFormat();
                circleCenterFormat.Alignment = StringAlignment.Center;
                circleCenterFormat.LineAlignment = StringAlignment.Center;
                circleCenterFormat.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(_displayName, s_cachedCircleFont, textBrush, screenRect, circleCenterFormat);
                circleCenterFormat.Dispose();
            }

            // Selection highlight
            if (_isSelected)
            {
                RectangleF glowRect = new RectangleF(
                    screenRect.X - 2f, screenRect.Y - 2f,
                    screenRect.Width + 4f, screenRect.Height + 4f);
                s_selectionGlowPen.Width = style.BorderWidth + 4f;
                g.DrawEllipse(s_selectionGlowPen, glowRect);
            }
        }

        /// <summary>
        /// Override hit test for circle mode to use circular bounds.
        /// </summary>
        public override bool HitTest(PointF worldPoint, float tolerance)
        {
            if (!_isCircleMode)
            {
                return base.HitTest(worldPoint, tolerance);
            }

            // Circle hit test: check distance from center
            RectangleF bounds = WorldBounds;
            float cx = bounds.X + bounds.Width / 2f;
            float cy = bounds.Y + bounds.Height / 2f;
            float radius = bounds.Width / 2f + tolerance;

            float dx = worldPoint.X - cx;
            float dy = worldPoint.Y - cy;
            return (dx * dx + dy * dy) <= (radius * radius);
        }

        private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
        {
            return CreateRoundedRectPath(rect, radius, true, true, true, true);
        }

        private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius,
            bool roundTopLeft, bool roundTopRight, bool roundBottomRight, bool roundBottomLeft)
        {
            GraphicsPath path = new GraphicsPath();

            // Guard against invalid dimensions
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return path;
            }

            float diameter = radius * 2;

            if (diameter > rect.Width) diameter = rect.Width;
            if (diameter > rect.Height) diameter = rect.Height;
            if (diameter <= 0) diameter = 1f;
            radius = diameter / 2;

            // Top-left corner
            if (roundTopLeft)
            {
                path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            }
            else
            {
                path.AddLine(rect.X, rect.Y + radius, rect.X, rect.Y);
                path.AddLine(rect.X, rect.Y, rect.X + radius, rect.Y);
            }

            // Top edge and top-right corner
            if (roundTopRight)
            {
                path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            }
            else
            {
                path.AddLine(rect.Right - radius, rect.Y, rect.Right, rect.Y);
                path.AddLine(rect.Right, rect.Y, rect.Right, rect.Y + radius);
            }

            // Right edge and bottom-right corner
            if (roundBottomRight)
            {
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            }
            else
            {
                path.AddLine(rect.Right, rect.Bottom - radius, rect.Right, rect.Bottom);
                path.AddLine(rect.Right, rect.Bottom, rect.Right - radius, rect.Bottom);
            }

            // Bottom edge and bottom-left corner
            if (roundBottomLeft)
            {
                path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            }
            else
            {
                path.AddLine(rect.X + radius, rect.Bottom, rect.X, rect.Bottom);
                path.AddLine(rect.X, rect.Bottom, rect.X, rect.Bottom - radius);
            }

            path.CloseFigure();
            return path;
        }
    }
}
