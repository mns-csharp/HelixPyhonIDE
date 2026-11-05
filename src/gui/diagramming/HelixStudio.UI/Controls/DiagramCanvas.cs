namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.UI.Interaction;
    using HelixPythonNamespace.UI.Interaction.Commands;
    using HelixPythonNamespace.UI.Interaction.States;
    using HelixPythonNamespace.UI.Projection;
    using HelixPythonNamespace.UI.Rendering;

    public class DiagramCanvas : Control
    {
        private SceneGraph _sceneGraph;
        private ViewportTransform _viewport;
        private InteractionContext _interactionContext;
        private SelectionManager _selectionManager;
        private CommandDispatcher _commandDispatcher;
        private EdgeRouter _edgeRouter;
        private SceneBuilder _sceneBuilder;

        // Right-click tracking
        private PointF _lastRightClickWorld;

        // Double-click tracking
        private DateTime _lastClickTime;
        private PointF _lastClickLocation;
        private const int DoubleClickInterval = 500; // milliseconds
        private const float DoubleClickDistance = 5f; // pixels

        // Tooltip support
        private ToolTip _tooltip;
        private NodeVisual _lastHoveredNode;
        private bool _showDrillUpButton;
        private int _drillUpButtonLeft = 12;
        private bool _drillUpHovered;
        private bool _drillUpPressed;
        private const int DrillUpButtonWidth = 104;
        private const int DrillUpButtonHeight = 30;

        // Scroll bars
        private HScrollBar _hScrollBar;
        private VScrollBar _vScrollBar;
        private bool _fitOnFirstPaint;
        private float _fitOnFirstPaintPadding = 50f;

        // The automatic fit after a load or drill-in never zooms out below this — a container level
        // with hundreds of boxes stops here and anchors top-left rather than shrinking to an
        // unreadable postage-stamp grid. The manual Zoom-to-Fit command has no such floor.
        internal const float ReadableFitFloor = 0.40f;
        private Panel _scrollCorner;
        private bool _updatingScrollBars;

        public DiagramCanvas()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.DoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);

            _sceneGraph = new SceneGraph();
            _viewport = new ViewportTransform();
            _selectionManager = new SelectionManager();
            _commandDispatcher = new CommandDispatcher();
            _edgeRouter = new EdgeRouter();
            _sceneBuilder = new SceneBuilder(_sceneGraph);

            _interactionContext = new InteractionContext(
                _sceneGraph,
                _viewport,
                _selectionManager,
                _commandDispatcher,
                _edgeRouter,
                new Action(delegate { Invalidate(); }));

            // Set initial state to idle
            _interactionContext.TransitionTo(new IdleState(_interactionContext));

            // Refresh cursor when the active tool changes (default prefs instance)
            _interactionContext.Preferences.ActiveToolChanged += OnActiveToolChanged;

            // Add grid visual to the scene (spacing is read from LayoutConstants at render time)
            GridVisual grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _sceneGraph.AddRenderable(grid);
            LayoutConstants.GridSpacingChanged += OnGridSpacingChanged;

            _lastClickTime = DateTime.MinValue;
            _lastClickLocation = PointF.Empty;

            // Initialize tooltip
            _tooltip = new ToolTip();
            _tooltip.InitialDelay = 500;
            _tooltip.ReshowDelay = 100;
            _tooltip.AutoPopDelay = 10000;
            _tooltip.ShowAlways = true;

            // Create scroll bars
            _hScrollBar = new HScrollBar();
            _hScrollBar.Dock = DockStyle.Bottom;
            _hScrollBar.Scroll += OnHorizontalScroll;

            _vScrollBar = new VScrollBar();
            _vScrollBar.Dock = DockStyle.Right;
            _vScrollBar.Scroll += OnVerticalScroll;

            // Corner panel to fill the gap between scroll bars
            _scrollCorner = new Panel();
            _scrollCorner.Size = new Size(_vScrollBar.Width, _hScrollBar.Height);
            _scrollCorner.Dock = DockStyle.None;
            _scrollCorner.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _scrollCorner.BackColor = SystemColors.Control;

            Controls.Add(_scrollCorner);
            Controls.Add(_hScrollBar);
            Controls.Add(_vScrollBar);

            PositionScrollCorner();

            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragDrop += OnDragDrop;
        }

        public event EventHandler<TypeDroppedEventArgs> TypeDroppedFromTree;

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(ProjectTreePanel.TypeDragFormat))
            {
                e.Effect = DragDropEffects.Link;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(ProjectTreePanel.TypeDragFormat))
            {
                e.Effect = DragDropEffects.Link;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(ProjectTreePanel.TypeDragFormat)) return;

            HelixPythonNamespace.Core.Model.TypeModel type =
                e.Data.GetData(ProjectTreePanel.TypeDragFormat) as HelixPythonNamespace.Core.Model.TypeModel;
            if (type == null) return;

            Point client = PointToClient(new Point(e.X, e.Y));
            PointF worldPoint = _viewport.ScreenToWorld(new PointF(client.X, client.Y));

            EventHandler<TypeDroppedEventArgs> handler = TypeDroppedFromTree;
            if (handler != null)
            {
                handler(this, new TypeDroppedEventArgs(type, worldPoint));
            }
        }

        public event EventHandler<NodeVisualEventArgs> NodeDoubleClicked;
        public event EventHandler DrillUpRequested;

        /// <summary>
        /// Shows a fixed, screen-space Drill Up button over the canvas. Unlike scene visuals,
        /// the button does not move or scale when the diagram is panned or zoomed.
        /// </summary>
        public bool ShowDrillUpButton
        {
            get { return _showDrillUpButton; }
            set
            {
                if (_showDrillUpButton == value) return;
                _showDrillUpButton = value;
                _drillUpHovered = false;
                _drillUpPressed = false;
                Invalidate();
            }
        }

        /// <summary>Screen-space X coordinate for Drill Up, used when a pinned canvas toolbox occupies the top-left.</summary>
        public int DrillUpButtonLeft
        {
            get { return _drillUpButtonLeft; }
            set { _drillUpButtonLeft = Math.Max(0, value); Invalidate(); }
        }

        /// <summary>
        /// Fired when the user right-clicks on a <see cref="NodeVisual"/> on the canvas.
        /// The host (HelixPythonNamespace.App) listens to show a Goto Designer / Goto Source
        /// context menu. Not fired when the right-click misses every node.
        /// </summary>
        public event EventHandler<NodeContextRequestedEventArgs> NodeContextRequested;

        public SceneGraph SceneGraph
        {
            get { return _sceneGraph; }
        }

        public InteractionContext InteractionContext
        {
            get { return _interactionContext; }
        }

        public SelectionManager SelectionManager
        {
            get { return _selectionManager; }
        }

        public CommandDispatcher CommandDispatcher
        {
            get { return _commandDispatcher; }
        }

        public SceneBuilder SceneBuilder
        {
            get { return _sceneBuilder; }
        }

        public EdgeRouter EdgeRouter
        {
            get { return _edgeRouter; }
        }

        public PointF LastRightClickWorldPosition
        {
            get { return _lastRightClickWorld; }
        }

        public void SyncToDiagram(Diagram diagram)
        {
            if (diagram == null)
            {
                return;
            }

            for (int i = 0; i < diagram.Nodes.Count; i++)
            {
                TypeNode typeNode = diagram.Nodes[i];
                NodeVisual visual = _sceneGraph.FindNodeById(typeNode.Id.ToString());
                if (visual != null)
                {
                    RectangleF bounds = visual.WorldBounds;
                    typeNode.PositionX = bounds.X;
                    typeNode.PositionY = bounds.Y;
                    typeNode.Width = bounds.Width;
                    typeNode.Height = bounds.Height;
                }
            }
        }

        public void AddNodeFromDomain(TypeNode node)
        {
            if (node == null)
            {
                return;
            }

            string nodeId = node.Id.ToString();
            RectangleF bounds = new RectangleF(
                node.PositionX, node.PositionY, node.Width, node.Height);
            INodeStyleStrategy style = NodeStyleFactory.GetStyle(node.TypeKind);
            NodeVisual visual = new NodeVisual(nodeId, bounds, style);

            string typeKindLabel;
            switch (node.TypeKind)
            {
                case TypeKind.Interface:
                    typeKindLabel = "<<interface>>";
                    break;
                case TypeKind.AbstractClass:
                    typeKindLabel = "<<abstract>>";
                    break;
                default:
                    typeKindLabel = "";
                    break;
            }

            visual.UpdateFromModel(node.Name, typeKindLabel, "", new List<string>());

            AddNodeCommand cmd = new AddNodeCommand(_sceneGraph, visual, _edgeRouter);
            _commandDispatcher.Execute(cmd);

            // Refresh with full formatting from SceneBuilder
            _sceneBuilder.UpdateNodeFromDomain(node);
        }

        public void UpdateNodeFromDomain(TypeNode node)
        {
            if (node == null)
            {
                return;
            }
            _sceneBuilder.UpdateNodeFromDomain(node);
        }

        public void DeleteSelection()
        {
            if (_selectionManager.IsEmpty)
            {
                return;
            }

            List<NodeVisual> nodes = new List<NodeVisual>();
            for (int i = 0; i < _selectionManager.SelectedNodes.Count; i++)
            {
                nodes.Add(_selectionManager.SelectedNodes[i]);
            }

            List<EdgeVisual> edges = new List<EdgeVisual>();
            for (int i = 0; i < _selectionManager.SelectedEdges.Count; i++)
            {
                edges.Add(_selectionManager.SelectedEdges[i]);
            }

            DeleteSelectionCommand cmd = new DeleteSelectionCommand(
                _sceneGraph, _edgeRouter, nodes, edges);
            _commandDispatcher.Execute(cmd);
            _selectionManager.ClearSelection();
        }

        public void LoadDiagram(Diagram diagram)
        {
            if (diagram == null)
            {
                return;
            }

            _selectionManager.ClearSelection();
            _sceneBuilder.RebuildScene(diagram);
            _edgeRouter.RouteAllEdges(_sceneGraph);
            Invalidate();
        }

        /// <summary>
        /// Load and display a solution using the projection builder pattern.
        /// </summary>
        public SolutionProjectionBuilder LoadFromSolution(SolutionModel solution, ILayoutMetadataStore layoutStore)
        {
            if (solution == null)
            {
                throw new ArgumentNullException("solution");
            }
            if (layoutStore == null)
            {
                throw new ArgumentNullException("layoutStore");
            }

            _selectionManager.ClearSelection();

            var autoLayout = new GridAutoLayoutStrategy();
            var projectionBuilder = new SolutionProjectionBuilder(_sceneGraph, layoutStore, autoLayout);
            projectionBuilder.RebuildFromSolution(solution);

            Invalidate();

            return projectionBuilder;
        }

        /// <summary>
        /// Find the TypeModel FQN for a given NodeVisual using the projection builder.
        /// </summary>
        public string FindTypeFQN(NodeVisual visual, SolutionProjectionBuilder projectionBuilder)
        {
            if (visual == null || projectionBuilder == null)
            {
                return null;
            }
            return projectionBuilder.FindFQN(visual);
        }

        /// <summary>
        /// Reset the canvas to an empty state.
        /// </summary>
        public void Clear()
        {
            _selectionManager.ClearSelection();
            _sceneGraph.Clear();

            // Re-add grid visual
            GridVisual grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _sceneGraph.AddRenderable(grid);

            Invalidate();
        }

        /// <summary>
        /// Adjust viewport to fit all content with padding.
        /// </summary>
        /// <param name="padding">Padding in screen pixels around the content.</param>
        public void ZoomToFit(float padding = 50f)
        {
            // Fit against the CURRENT client area (minus scrollbars, exactly as OnResize computes it).
            // A fit requested during solution load — before the canvas has received its final OnResize
            // — would otherwise centre against a stale, smaller viewport and leave sparse content stuck
            // in the top-left instead of centred.
            _viewport.ViewportSize = new SizeF(
                System.Math.Max(1, Width - _vScrollBar.Width),
                System.Math.Max(1, Height - _hScrollBar.Height));

            RectangleF bounds = _sceneGraph.ComputeContentBounds();
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // Content is measured — fit it now.
                _viewport.ZoomToFit(bounds, padding);
                Invalidate();
            }
            else if (_sceneGraph.Nodes.Count > 0)
            {
                // Nodes exist but their bounds are only measured at first render (text sizing needs a
                // live Graphics). Fitting now would centre a zero-area rect and strand the diagram in
                // the top-left. Defer to the first paint so every caller — solution load, drill-in
                // navigation, category rebuilds — fits against real content.
                RequestFitOnFirstPaint(padding);
            }
            else
            {
                // Genuinely empty — reset to the default view.
                _viewport.Reset();
                Invalidate();
            }
        }

        /// <summary>
        /// Fit content for an AUTOMATIC fit (solution load / drill-in): identical to
        /// <see cref="ZoomToFit(float)"/> except it never zooms out below
        /// <see cref="ReadableFitFloor"/> and never enlarges past natural size. When the level has
        /// hundreds of boxes it stops at the floor and anchors top-left so the labels stay readable
        /// and the user scrolls. The manual Zoom-to-Fit command keeps <see cref="ZoomToFit(float)"/>,
        /// which fits everything at once with no floor.
        /// </summary>
        public void FitContentReadable(float padding = 50f)
        {
            _viewport.ViewportSize = new SizeF(
                System.Math.Max(1, Width - _vScrollBar.Width),
                System.Math.Max(1, Height - _hScrollBar.Height));

            RectangleF bounds = _sceneGraph.ComputeContentBounds();
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _viewport.ZoomToFit(bounds, padding, 1.0f, ReadableFitFloor);
                UpdateScrollBars();
                Invalidate();
            }
            else if (_sceneGraph.Nodes.Count > 0)
            {
                // Bounds not measured yet — the deferred first-paint fit already applies the floor.
                RequestFitOnFirstPaint(padding);
            }
            else
            {
                _viewport.Reset();
                Invalidate();
            }
        }

        /// <summary>
        /// Fit content to the viewport on the next paint instead of now. Node bounds are only
        /// finalised during rendering (text measurement needs a live Graphics), so a fit requested
        /// at load time runs against un-measured bounds and leaves sparse diagrams stuck top-left.
        /// Deferring to the first real paint fits against the true, measured content.
        /// </summary>
        public void RequestFitOnFirstPaint(float padding = 50f)
        {
            _fitOnFirstPaintPadding = padding;
            _fitOnFirstPaint = true;
            Invalidate();
        }

        /// <summary>
        /// Get the current viewport transform.
        /// </summary>
        public ViewportTransform Viewport
        {
            get { return _viewport; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (e == null)
            {
                return;
            }

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);

            _sceneGraph.RenderAll(g, _viewport);

            IInteractionState currentState = _interactionContext.CurrentState;
            if (currentState != null)
            {
                currentState.OnPaint(g, _viewport);
            }

            DrawDrillUpButton(g);

            // One-shot: fit content the first time it is actually painted. RenderAll above has now
            // measured every node's bounds, so ComputeContentBounds is finally accurate — unlike at
            // load time when a fit was requested. Cleared before fitting so the follow-up repaint
            // does not re-enter this block.
            if (_fitOnFirstPaint)
            {
                _fitOnFirstPaint = false;
                RectangleF fitBounds = _sceneGraph.ComputeContentBounds();
                if (!fitBounds.IsEmpty && fitBounds.Width > 0 && fitBounds.Height > 0)
                {
                    _viewport.ViewportSize = new SizeF(
                        System.Math.Max(1, Width - _vScrollBar.Width),
                        System.Math.Max(1, Height - _hScrollBar.Height));
                    // Cap at 1.0 so a sparse diagram (a single small node) is centred at natural size
                    // rather than blown up to fill the window; dense diagrams zoom out to fit, but no
                    // further than ReadableFitFloor — below that the labels are unreadable, so we stop
                    // there and anchor top-left for the user to scroll.
                    _viewport.ZoomToFit(fitBounds, _fitOnFirstPaintPadding, 1.0f, ReadableFitFloor);
                    UpdateScrollBars();
                    Invalidate();
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e == null)
            {
                return;
            }

            if (e.Button == MouseButtons.Left && DrillUpButtonBounds.Contains(e.Location))
            {
                _drillUpPressed = true;
                Capture = true;
                Cursor = Cursors.Hand;
                Invalidate(DrillUpButtonBounds);
                return;
            }

            PointF screenPoint = new PointF(e.X, e.Y);
            PointF worldPoint = _viewport.ScreenToWorld(screenPoint);

            if (e.Button == MouseButtons.Right)
            {
                _lastRightClickWorld = worldPoint;
            }

            CanvasMouseEventArgs args = new CanvasMouseEventArgs(
                e.Button, screenPoint, worldPoint, ModifierKeys);

            IInteractionState currentState = _interactionContext.CurrentState;
            if (currentState != null)
            {
                currentState.OnMouseDown(args);
                Cursor = currentState.GetCursor();
            }

            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (e == null)
            {
                return;
            }

            bool drillUpHovered = DrillUpButtonBounds.Contains(e.Location);
            if (drillUpHovered != _drillUpHovered)
            {
                _drillUpHovered = drillUpHovered;
                _tooltip.SetToolTip(this, drillUpHovered ? "Return to the parent diagram (Alt+Up)" : null);
                Invalidate(DrillUpButtonBounds);
            }
            if (drillUpHovered || _drillUpPressed)
            {
                Cursor = Cursors.Hand;
                return;
            }

            PointF screenPoint = new PointF(e.X, e.Y);
            PointF worldPoint = _viewport.ScreenToWorld(screenPoint);

            CanvasMouseEventArgs args = new CanvasMouseEventArgs(
                e.Button, screenPoint, worldPoint, ModifierKeys);

            IInteractionState currentState = _interactionContext.CurrentState;
            if (currentState != null)
            {
                currentState.OnMouseMove(args);
                Cursor = currentState.GetCursor();
            }

            // Tooltip handling for nodes
            float tolerance = _interactionContext.HitTestTolerance;
            NodeVisual hoveredNode = _sceneGraph.HitTestNode(worldPoint, tolerance);

            if (hoveredNode != _lastHoveredNode)
            {
                _lastHoveredNode = hoveredNode;

                if (hoveredNode != null)
                {
                    string tooltipText = hoveredNode.GetTooltipText();
                    _tooltip.SetToolTip(this, tooltipText);
                }
                else
                {
                    _tooltip.SetToolTip(this, null);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e == null)
            {
                return;
            }

            if (_drillUpPressed)
            {
                bool activate = e.Button == MouseButtons.Left &&
                    DrillUpButtonBounds.Contains(e.Location);
                _drillUpPressed = false;
                Capture = false;
                Invalidate(DrillUpButtonBounds);
                if (activate)
                {
                    EventHandler handler = DrillUpRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                return;
            }

            PointF screenPoint = new PointF(e.X, e.Y);
            PointF worldPoint = _viewport.ScreenToWorld(screenPoint);

            CanvasMouseEventArgs args = new CanvasMouseEventArgs(
                e.Button, screenPoint, worldPoint, ModifierKeys);

            IInteractionState currentState = _interactionContext.CurrentState;
            if (currentState != null)
            {
                currentState.OnMouseUp(args);
                Cursor = currentState.GetCursor();
            }

            Invalidate();

            // Right-click on a node -> raise NodeContextRequested so the host can show a
            // dedicated Goto menu. Suppress the default ContextMenuStrip for this click
            // so the two menus don't stack; restore it after WM_CONTEXTMENU is processed.
            if (e.Button == MouseButtons.Right)
            {
                float tolerance = _interactionContext.HitTestTolerance;
                NodeVisual hit = _sceneGraph.HitTestNode(worldPoint, tolerance);
                if (hit != null)
                {
                    EventHandler<NodeContextRequestedEventArgs> handler = NodeContextRequested;
                    if (handler != null)
                    {
                        ContextMenuStrip savedMenu = this.ContextMenuStrip;
                        this.ContextMenuStrip = null;
                        try { handler(this, new NodeContextRequestedEventArgs(hit, new Point(e.X, e.Y))); }
                        finally
                        {
                            if (savedMenu != null)
                            {
                                BeginInvoke(new Action(delegate { this.ContextMenuStrip = savedMenu; }));
                            }
                        }
                    }
                }
            }

            // Double-click detection
            if (e.Button == MouseButtons.Left)
            {
                DateTime now = DateTime.Now;
                TimeSpan elapsed = now - _lastClickTime;

                float dx = screenPoint.X - _lastClickLocation.X;
                float dy = screenPoint.Y - _lastClickLocation.Y;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);

                if (elapsed.TotalMilliseconds < DoubleClickInterval && distance < DoubleClickDistance)
                {
                    // This is a double-click
                    float tolerance = _interactionContext.HitTestTolerance;
                    NodeVisual hitNode = _sceneGraph.HitTestNode(worldPoint, tolerance);
                    if (hitNode != null)
                    {
                        OnNodeDoubleClicked(hitNode);
                    }
                    _lastClickTime = DateTime.MinValue;
                }
                else
                {
                    _lastClickTime = now;
                    _lastClickLocation = screenPoint;
                }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            if (e == null)
            {
                return;
            }

            PointF screenPoint = new PointF(e.X, e.Y);
            PointF worldPoint = _viewport.ScreenToWorld(screenPoint);

            CanvasMouseWheelEventArgs args = new CanvasMouseWheelEventArgs(
                e.Delta, screenPoint, worldPoint, ModifierKeys);

            IInteractionState currentState = _interactionContext.CurrentState;
            if (currentState != null)
            {
                currentState.OnMouseWheel(args);
            }

            // Update scroll bars after zoom change
            UpdateScrollBars();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_drillUpHovered) return;
            _drillUpHovered = false;
            if (!_drillUpPressed) Cursor = Cursors.Default;
            Invalidate(DrillUpButtonBounds);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || !_drillUpPressed) return;
            _drillUpPressed = false;
            Invalidate(DrillUpButtonBounds);
        }

        private Rectangle DrillUpButtonBounds
        {
            get
            {
                return _showDrillUpButton
                    ? new Rectangle(_drillUpButtonLeft, 12, DrillUpButtonWidth, DrillUpButtonHeight)
                    : Rectangle.Empty;
            }
        }

        private void DrawDrillUpButton(Graphics g)
        {
            Rectangle bounds = DrillUpButtonBounds;
            if (bounds.IsEmpty) return;

            Rectangle face = bounds;
            if (_drillUpPressed) face.Offset(0, 1);
            Color fill = _drillUpPressed
                ? Color.FromArgb(208, 226, 248)
                : (_drillUpHovered ? Color.FromArgb(235, 244, 255) : Color.FromArgb(250, 252, 255));

            using (GraphicsPath path = RoundedRectangle(face, 6))
            using (var shadow = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
            using (var background = new SolidBrush(fill))
            using (var border = new Pen(Color.FromArgb(80, 125, 180)))
            using (var arrow = new Pen(Color.FromArgb(35, 85, 150), 2f))
            using (var textBrush = new SolidBrush(Color.FromArgb(35, 65, 105)))
            {
                if (!_drillUpPressed)
                {
                    using (GraphicsPath shadowPath = RoundedRectangle(
                        new Rectangle(face.X + 1, face.Y + 2, face.Width, face.Height), 6))
                        g.FillPath(shadow, shadowPath);
                }
                g.FillPath(background, path);
                g.DrawPath(border, path);

                arrow.StartCap = LineCap.Round;
                arrow.EndCap = LineCap.Round;
                float centerY = face.Top + face.Height / 2f;
                g.DrawLine(arrow, face.Left + 15, centerY + 4, face.Left + 15, centerY - 5);
                g.DrawLine(arrow, face.Left + 15, centerY - 5, face.Left + 10, centerY);
                g.DrawLine(arrow, face.Left + 15, centerY - 5, face.Left + 20, centerY);

                TextRenderer.DrawText(g, "Drill Up", Font,
                    new Rectangle(face.Left + 27, face.Top, face.Width - 31, face.Height),
                    textBrush.Color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                    TextFormatFlags.SingleLine);
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e == null)
            {
                return;
            }

            IInteractionState currentState = _interactionContext.CurrentState;
            if (currentState != null)
            {
                currentState.OnKeyDown(e);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            _viewport.ViewportSize = new SizeF(
                Width - _vScrollBar.Width,
                Height - _hScrollBar.Height);
            PositionScrollCorner();
            UpdateScrollBars();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                LayoutConstants.GridSpacingChanged -= OnGridSpacingChanged;
                if (_interactionContext != null && _interactionContext.Preferences != null)
                {
                    _interactionContext.Preferences.ActiveToolChanged -= OnActiveToolChanged;
                }
            }
            base.Dispose(disposing);
        }

        private void OnGridSpacingChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(Invalidate)); } catch { }
            }
            else
            {
                Invalidate();
            }
        }

        private void OnActiveToolChanged()
        {
            if (IsDisposed) return;
            Action refresh = new Action(delegate
            {
                IInteractionState st = _interactionContext.CurrentState;
                if (st != null) Cursor = st.GetCursor();
                Invalidate();
            });
            if (InvokeRequired)
            {
                try { BeginInvoke(refresh); } catch { }
            }
            else
            {
                refresh();
            }
        }

        /// <summary>
        /// Replace this canvas's ambient interaction preferences with a shared instance,
        /// so one floating palette can drive every canvas.
        /// </summary>
        public void SetSharedPreferences(SharedInteractionPreferences prefs)
        {
            if (prefs == null) return;
            if (_interactionContext.Preferences != null)
            {
                _interactionContext.Preferences.ActiveToolChanged -= OnActiveToolChanged;
            }
            _interactionContext.Preferences = prefs;
            prefs.ActiveToolChanged += OnActiveToolChanged;
            OnActiveToolChanged();
        }

        public SharedInteractionPreferences SharedPreferences
        {
            get { return _interactionContext.Preferences; }
        }

        private void PositionScrollCorner()
        {
            _scrollCorner.Location = new Point(
                Width - _vScrollBar.Width,
                Height - _hScrollBar.Height);
        }

        private void OnHorizontalScroll(object sender, ScrollEventArgs e)
        {
            if (_updatingScrollBars)
            {
                return;
            }

            float worldX = e.NewValue;
            _viewport.SetPanOffset(-worldX * _viewport.Zoom, _viewport.PanOffset.Y);
            Invalidate();
        }

        private void OnVerticalScroll(object sender, ScrollEventArgs e)
        {
            if (_updatingScrollBars)
            {
                return;
            }

            float worldY = e.NewValue;
            _viewport.SetPanOffset(_viewport.PanOffset.X, -worldY * _viewport.Zoom);
            Invalidate();
        }

        public void UpdateScrollBars()
        {
            _updatingScrollBars = true;
            try
            {
                RectangleF contentBounds = _sceneGraph.ComputeContentBounds();
                if (contentBounds.IsEmpty)
                {
                    _hScrollBar.Enabled = false;
                    _vScrollBar.Enabled = false;
                    return;
                }

                // Add margin around content
                contentBounds.Inflate(200, 200);

                float viewWidth = Width - _vScrollBar.Width;
                float viewHeight = Height - _hScrollBar.Height;

                // Horizontal scroll bar
                int hMin = (int)contentBounds.Left;
                int hMax = (int)Math.Max(0, contentBounds.Right - viewWidth / _viewport.Zoom);
                _hScrollBar.Minimum = hMin;
                _hScrollBar.Maximum = hMax + (int)(viewWidth / _viewport.Zoom);
                _hScrollBar.LargeChange = Math.Max(1, (int)(viewWidth / _viewport.Zoom));
                int hValue = (int)(-_viewport.PanOffset.X / _viewport.Zoom);
                _hScrollBar.Value = Math.Max(hMin, Math.Min(hMax, hValue));
                _hScrollBar.Enabled = hMax > hMin;

                // Vertical scroll bar
                int vMin = (int)contentBounds.Top;
                int vMax = (int)Math.Max(0, contentBounds.Bottom - viewHeight / _viewport.Zoom);
                _vScrollBar.Minimum = vMin;
                _vScrollBar.Maximum = vMax + (int)(viewHeight / _viewport.Zoom);
                _vScrollBar.LargeChange = Math.Max(1, (int)(viewHeight / _viewport.Zoom));
                int vValue = (int)(-_viewport.PanOffset.Y / _viewport.Zoom);
                _vScrollBar.Value = Math.Max(vMin, Math.Min(vMax, vValue));
                _vScrollBar.Enabled = vMax > vMin;
            }
            finally
            {
                _updatingScrollBars = false;
            }
        }

        protected virtual void OnNodeDoubleClicked(NodeVisual node)
        {
            EventHandler<NodeVisualEventArgs> handler = NodeDoubleClicked;
            if (handler != null)
            {
                handler(this, new NodeVisualEventArgs(node));
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            // Ensure arrow keys, Delete, etc. reach OnKeyDown
            if (keyData == Keys.Delete ||
                keyData == Keys.Up ||
                keyData == Keys.Down ||
                keyData == Keys.Left ||
                keyData == Keys.Right ||
                keyData == Keys.Escape)
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }
    }

    public sealed class NodeVisualEventArgs : EventArgs
    {
        private readonly NodeVisual _nodeVisual;

        public NodeVisualEventArgs(NodeVisual nodeVisual)
        {
            _nodeVisual = nodeVisual;
        }

        public NodeVisual NodeVisual
        {
            get { return _nodeVisual; }
        }
    }

    public sealed class NodeContextRequestedEventArgs : EventArgs
    {
        public NodeContextRequestedEventArgs(NodeVisual nodeVisual, Point screenLocation)
        {
            NodeVisual = nodeVisual;
            ScreenLocation = screenLocation;
        }

        public NodeVisual NodeVisual { get; }

        /// <summary>Client-relative (on the canvas) coordinates where the menu should be shown.</summary>
        public Point ScreenLocation { get; }
    }
}
