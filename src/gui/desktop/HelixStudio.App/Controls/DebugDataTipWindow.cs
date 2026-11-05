using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Debugger;

namespace HelixPythonNamespace.App.Controls
{
    /// <summary>
    /// The Visual Studio data tip: hover an expression while the debugger is paused and its live
    /// value appears, expandable into its members.
    ///
    /// Follows the non-activating-popup shape every popup in this IDE uses
    /// (<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> + <see cref="ShowWithoutActivation"/>): the tip
    /// takes mouse clicks so its tree can be expanded, but never Win32 keyboard focus, so the caret
    /// stays in the editor and typing is unaffected. A popup that activates would make hovering a
    /// variable steal the user's cursor mid-edit.
    ///
    /// Children are fetched LAZILY, on expand, through the same <c>variablesReference</c> protocol
    /// the Variables panel uses. Eagerly walking an object graph would make a tooltip issue dozens
    /// of round trips to a paused process for members nobody asked to see.
    /// </summary>
    internal sealed class DebugDataTipWindow : Form
    {
        // Children of a node, fetched on demand. Mirrors DebugCoordinator's VarNode.
        private sealed class TipNode
        {
            public int Ref;
            public bool Loaded;
        }

        private readonly TreeView _tree;
        private Func<int, IList<DebugVariable>> _childProvider;

        public DebugDataTipWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(255, 255, 225);   // the classic tooltip ground, as the calltip uses
            Padding = new Padding(1);
            // A plain Form starts in Microsoft Sans Serif; the tip is not a LunaForm (see the
            // skin exemption), so it takes the shell font itself and the tree inherits it.
            Font = HelixPythonNamespace.UI.Controls.LunaFonts.Ui;

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(255, 255, 225),
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                HideSelection = false,
                ItemHeight = 18,
            };
            _tree.BeforeExpand += OnBeforeExpand;
            // Resizing has to happen AFTER the expansion, not inside BeforeExpand: during
            // BeforeExpand the node still reports IsExpanded == false, so measuring there counted
            // one row and left the tip its collapsed height with nine members hidden inside it
            // (measured on a real stop). Collapsing shrinks it back the same way.
            _tree.AfterExpand += (s, e) => FitToContent();
            _tree.AfterCollapse += (s, e) => FitToContent();
            Controls.Add(_tree);

            _grip = BuildGrip();
            Controls.Add(_grip);
            _grip.BringToFront();   // index 0 is topmost in WinForms; the docked tree must not cover it

            MinimumSize = new Size(180, _tree.ItemHeight + 8);
        }

        // ---------------------------------------------------------------- the resize grip

        /// <summary>Side of the square drag corner, in pixels.</summary>
        private const int GripSize = 14;

        private readonly Control _grip;
        private bool _resizing;
        private Point _resizeStartLocal;
        private Size _resizeStartSize;

        /// <summary>
        /// The size the user dragged this tip to, remembered for every later hover. The coordinator
        /// keeps one tip for the whole session, so this survives as long as the session does.
        /// <see cref="Size.Empty"/> until they resize it.
        /// </summary>
        private Size _userSize = Size.Empty;

        /// <summary>True while a resize drag is in progress.</summary>
        public bool IsResizing { get { return _resizing; } }

        /// <summary>
        /// True while the user is doing something with the tip — hovering it or dragging its corner.
        /// The caller's dismiss timer consults this instead of <see cref="ContainsMouse"/> alone: a
        /// fast drag can throw the cursor outside the old bounds for a frame, and a tip that closed
        /// mid-resize would be impossible to make bigger.
        /// </summary>
        public bool IsInteracting
        {
            get { return _resizing || ContainsMouse; }
        }

        /// <summary>
        /// The bottom-right drag corner. A data tip that is exactly as big as the IDE decided is
        /// fine until the value is a long tuple or a deep object — then the reader needs it bigger,
        /// and there is no other way to ask for that.
        /// </summary>
        private Control BuildGrip()
        {
            var grip = new Control
            {
                Size = new Size(GripSize, GripSize),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Cursor = Cursors.SizeNWSE,
                // No BackColor here: a bare Control throws "does not support transparent background
                // colors", and it inherits the tip's tooltip ground anyway, which is what the grip
                // is drawn over.
                TabStop = false,
            };
            grip.Paint += (s, e) =>
                ControlPaint.DrawSizeGrip(e.Graphics, BackColor, 0, 0, GripSize, GripSize);
            grip.MouseDown += OnGripMouseDown;
            grip.MouseMove += OnGripMouseMove;
            grip.MouseUp += OnGripMouseUp;
            return grip;
        }

        private void OnGripMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _resizing = true;
            // Deltas are taken from the grip's OWN coordinates rather than Cursor.Position: mouse
            // capture keeps delivering moves once the pointer leaves the 14px square (which it does
            // immediately), and screen coordinates would make this untestable without physically
            // moving the user's pointer.
            _resizeStartLocal = e.Location;
            _resizeStartSize = Size;
            _grip.Capture = true;
        }

        private void OnGripMouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            Size = ResizedTo(e.Location);
        }

        /// <summary>
        /// The size a drag to <paramref name="gripPoint"/> (grip-local coordinates) asks for, clamped.
        ///
        /// A hand-dragged size is the user's call, so the automatic content caps do not apply — but
        /// the tip still must not grow off the screen it is on, which would hide the very rows the
        /// drag was asking for.
        /// </summary>
        internal Size ResizedTo(Point gripPoint)
        {
            int w = _resizeStartSize.Width + (gripPoint.X - _resizeStartLocal.X);
            int h = _resizeStartSize.Height + (gripPoint.Y - _resizeStartLocal.Y);

            Rectangle screen;
            try { screen = Screen.FromControl(this).WorkingArea; }
            catch { screen = new Rectangle(0, 0, 1920, 1080); }
            w = Math.Max(MinimumSize.Width, Math.Min(w, Math.Max(MinimumSize.Width, screen.Right - Left)));
            h = Math.Max(MinimumSize.Height, Math.Min(h, Math.Max(MinimumSize.Height, screen.Bottom - Top)));
            return new Size(w, h);
        }

        private void OnGripMouseUp(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            _grip.Capture = false;
            // Remember it. Re-measuring content on the next hover and quietly undoing the drag is
            // the behaviour that makes a resizable popup feel broken.
            _userSize = Size;
        }

        private void PositionGrip()
        {
            if (_grip == null) return;
            _grip.Location = new Point(ClientSize.Width - GripSize, ClientSize.Height - GripSize);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PositionGrip();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle = HelixPythonNamespace.Core.TestEnvironment.ExStyle(cp.Style, cp.ExStyle);   // test runs only
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HelixPythonNamespace.Core.TestEnvironment.HandleCreated(Handle, CreateParams.Style);   // alpha 0 in a test run
        }

        /// <summary>True while the mouse is over the tip — the caller must not dismiss it then.</summary>
        public bool ContainsMouse
        {
            get
            {
                if (!Visible || IsDisposed) return false;
                try { return Bounds.Contains(Cursor.Position); }
                catch { return false; }
            }
        }

        /// <summary>The expression currently on show, so a repeat dwell doesn't rebuild the tip.</summary>
        public string Expression { get; private set; }

        /// <summary>
        /// Fills the tip with one evaluated expression and shows it near <paramref name="anchor"/>
        /// (screen coordinates of the hovered text). <paramref name="childProvider"/> resolves a
        /// <c>variablesReference</c> to its members when the user expands a node.
        /// </summary>
        public void ShowFor(string expression, DebugVariable value, Point anchor, int lineHeight,
                           Func<int, IList<DebugVariable>> childProvider, IWin32Window owner)
        {
            if (value == null) return;
            _childProvider = childProvider;
            Expression = expression;

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                TreeNode root = MakeNode(expression, value.Value, value.Type, value.VariablesReference);
                _tree.Nodes.Add(root);

                // Open the root immediately. A tip that comes up as a single collapsed row is a
                // strip of text the user has to click before it says anything — the members ARE the
                // answer to "what is this object right now". The one `variables` round trip this
                // costs is the same one the click would have made a moment later.
                //
                // The children are fetched HERE rather than by letting Expand() raise BeforeExpand,
                // because the window has no handle yet on the first hover: TreeNode.Expand() would
                // defer to realization and the size below would be computed against one row.
                // Pre-loading also means BeforeExpand finds Loaded == true and does not re-fetch.
                if (value.VariablesReference > 0)
                {
                    LoadChildren(root);
                    root.Expand();
                }
            }
            finally { _tree.EndUpdate(); }

            // Once the user has dragged a tip to a size, that is the size tips are — re-measuring
            // the content on the next hover and quietly shrinking back is what makes a resizable
            // popup feel like it ignored you.
            Size = _userSize.IsEmpty ? PreferredTipSize() : _userSize;
            Location = PlaceOnScreen(anchor, lineHeight);
            if (!Visible)
            {
                if (owner != null) Show(owner); else Show();
            }
            PositionGrip();
            // A tip must never sit above the window it explains without being on top of it, but it
            // must also not float above unrelated applications — Show(owner) already keeps it with
            // the shell, matching the calltip's deliberate non-TopMost choice.
        }

        public void HideTip()
        {
            Expression = null;
            if (!IsDisposed && Visible) Hide();
        }

        private TreeNode MakeNode(string name, string value, string type, int childRef)
        {
            string label = name + " = " + Summarize(value);
            if (!string.IsNullOrEmpty(type)) label += "   {" + type + "}";
            var node = new TreeNode(label) { Tag = new TipNode { Ref = childRef } };
            // A placeholder child is what makes the [+] glyph appear before anything is fetched.
            if (childRef > 0) node.Nodes.Add(new TreeNode("..."));
            return node;
        }

        /// <summary>Longest value text a single row shows before it is elided.</summary>
        private const int MaxValueChars = 160;

        /// <summary>
        /// One row of a tree is one LINE, and a debugger returns values that are neither.
        /// <c>repr()</c> of a numpy array or a large dict arrives with embedded newlines and runs to
        /// thousands of characters; putting that straight into a TreeNode label produced a wide,
        /// blank-looking box with nothing legible in it (measured against a real 12x12 array).
        /// Flattening the whitespace and eliding the tail gives the row the summary a data tip is
        /// for — the detail is what expanding the node is for.
        /// </summary>
        private static string Summarize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new System.Text.StringBuilder(Math.Min(value.Length, MaxValueChars + 8));
            bool lastWasSpace = false;
            foreach (char c in value)
            {
                bool blank = c == '\n' || c == '\r' || c == '\t' || c == ' ';
                if (blank)
                {
                    if (lastWasSpace || sb.Length == 0) continue;
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
                if (sb.Length >= MaxValueChars) { sb.Append('…'); break; }
            }
            return sb.ToString().TrimEnd();
        }

        private void OnBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            LoadChildren(e.Node);
        }

        /// <summary>
        /// Fetches a node's members once, replacing the placeholder that drew its [+] glyph. Safe to
        /// call twice — the second call finds <c>Loaded</c> and does nothing, which is what lets
        /// <see cref="ShowFor"/> pre-load the root without the later BeforeExpand re-fetching it.
        /// </summary>
        private void LoadChildren(TreeNode node)
        {
            var tag = node == null ? null : node.Tag as TipNode;
            if (tag == null || tag.Loaded || tag.Ref <= 0 || _childProvider == null) return;
            tag.Loaded = true;
            node.Nodes.Clear();   // drop the placeholder
            IList<DebugVariable> children;
            try { children = _childProvider(tag.Ref); }
            catch { return; }
            if (children == null) return;
            foreach (DebugVariable c in children)
                node.Nodes.Add(MakeNode(c.Name, c.Value, c.Type, c.VariablesReference));
        }

        /// <summary>
        /// Grows (or shrinks) the tip to the rows it now shows, and keeps it on screen. Without it
        /// the members the user just expanded are hidden inside a one-row popup.
        /// </summary>
        private void FitToContent()
        {
            // A size the user chose by hand outranks anything measured from the content, and a
            // resize in progress must not be fought by a re-measure mid-drag.
            if (_resizing || !_userSize.IsEmpty) return;
            Size = PreferredTipSize();
            Location = PlaceOnScreen(Location, 0);
        }

        /// <summary>
        /// How many rows a tip with members opens at, at minimum. One row is a strip of text, not a
        /// view of an object: the point of the tip is to see the fields, so an expandable value gets
        /// room for several of them straight away and only grows from there.
        /// </summary>
        private const int MinRowsWhenExpandable = 5;

        /// <summary>Beyond this the tip stops growing and scrolls — a tooltip is not a panel.</summary>
        private const int MaxTipHeight = 360;

        private const int MaxTipWidth = 720;

        // Wide enough for the longest visible label, tall enough for the rows it shows, and capped
        // so a large container cannot produce a tip the size of the screen.
        private Size PreferredTipSize()
        {
            int rows = CountVisibleRows(_tree.Nodes);
            int widest;
            using (Graphics g = CreateGraphics())
                widest = WidestLabel(g, _tree.Nodes, 0);

            int w = Math.Max(180, Math.Min(widest + 60, MaxTipWidth));

            // A leaf value is genuinely one line — a five-row box of whitespace around `n = 42`
            // would be worse than the strip. The floor applies only where there are members to show.
            int floorRows = HasExpandableRoot() ? MinRowsWhenExpandable : 1;
            int contentRows = Math.Max(rows, floorRows);
            int h = Math.Min(contentRows * _tree.ItemHeight + 8, MaxTipHeight);

            // A label wider than the cap puts a horizontal scrollbar along the bottom; without
            // allowing for it the last row is hidden behind the scrollbar the long value caused.
            if (widest + 60 > MaxTipWidth)
                h = Math.Min(h + SystemInformation.HorizontalScrollBarHeight, MaxTipHeight + SystemInformation.HorizontalScrollBarHeight);

            return new Size(w, h);
        }

        // True when the tip's subject has members, i.e. the height floor is worth applying.
        private bool HasExpandableRoot()
        {
            if (_tree.Nodes.Count == 0) return false;
            var tag = _tree.Nodes[0].Tag as TipNode;
            return tag != null && tag.Ref > 0;
        }

        private static int CountVisibleRows(TreeNodeCollection nodes)
        {
            int n = 0;
            foreach (TreeNode node in nodes)
            {
                n++;
                if (node.IsExpanded) n += CountVisibleRows(node.Nodes);
            }
            return n;
        }

        private int WidestLabel(Graphics g, TreeNodeCollection nodes, int depth)
        {
            int widest = 0;
            foreach (TreeNode node in nodes)
            {
                int w = TextRenderer.MeasureText(g, node.Text ?? string.Empty, _tree.Font).Width
                        + depth * _tree.Indent;
                if (w > widest) widest = w;
                if (node.IsExpanded)
                {
                    int inner = WidestLabel(g, node.Nodes, depth + 1);
                    if (inner > widest) widest = inner;
                }
            }
            return widest;
        }

        /// <summary>
        /// Puts the tip just under the hovered text, then pulls it back onto the screen it is on.
        /// A tip that runs off the bottom or the right edge shows the user nothing, which is the
        /// state a naive "anchor + line height" placement leaves it in near any screen edge.
        /// </summary>
        private Point PlaceOnScreen(Point anchor, int lineHeight)
        {
            int x = anchor.X;
            int y = anchor.Y + (lineHeight > 0 ? lineHeight + 2 : 0);
            Rectangle screen;
            try { screen = Screen.FromPoint(anchor).WorkingArea; }
            catch { return new Point(x, y); }

            if (x + Width > screen.Right) x = Math.Max(screen.Left, screen.Right - Width);
            if (x < screen.Left) x = screen.Left;
            if (y + Height > screen.Bottom)
            {
                // Flip above the hovered line rather than covering it.
                int above = anchor.Y - Height - 2;
                y = above >= screen.Top ? above : Math.Max(screen.Top, screen.Bottom - Height);
            }
            if (y < screen.Top) y = screen.Top;
            return new Point(x, y);
        }
    }
}
