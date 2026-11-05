namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// Owns the Solution Explorer / navigation panel docking behaviour: docking the
    /// left panel to the left or right of the splitter, floating it (and the nested
    /// navigation tree) into a separate window, redocking, and the auto-hide/collapse
    /// interactions. <see cref="MainForm"/> keeps thin internal wrappers for the
    /// entries invoked by the View menu and wires the DockablePanel events here.
    /// </summary>
    public sealed class ExplorerCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        public void Detach()
        {
            _context = null;
            host = null;
        }

        // View ▸ Dock Explorer Left. If currently floating, redock into the splitter
        // first, then move the explorer to the left panel when it isn't already there.
        public void OnDockExplorerLeft(object sender, EventArgs e)
        {
            if (host._floatingExplorerForm != null)
            {
                RedockExplorer();
            }

            if (host._explorerDockedRight)
            {
                SwapSplitterPanels(false);
                host._explorerDockedRight = false;
            }
            // A dock command is also the way back from collapsed, auto-hidden or squeezed-to-
            // nothing: whatever state the panel got into, this is a state the user can work in.
            RestoreExplorer();
            host._statusLabel.Text = "Explorer docked left";
        }

        // View ▸ Dock Explorer Right. Mirror of the above.
        public void OnDockExplorerRight(object sender, EventArgs e)
        {
            if (host._floatingExplorerForm != null)
            {
                RedockExplorer();
            }

            if (!host._explorerDockedRight)
            {
                SwapSplitterPanels(true);
                host._explorerDockedRight = true;
            }
            RestoreExplorer();
            host._statusLabel.Text = "Explorer docked right";
        }

        // Reparents the explorer dock panel and the main tab control between the two
        // splitter panes to put the explorer on the requested side. SuspendLayout brackets
        // the reflow so the swap doesn't flicker through an intermediate layout.
        private void SwapSplitterPanels(bool explorerOnRight)
        {
            host._leftSplitter.SuspendLayout();

            // Remove both panels' controls
            host._leftSplitter.Panel1.Controls.Clear();
            host._leftSplitter.Panel2.Controls.Clear();

            // Explorer right: tabs go left, explorer right, and the splitter distance is
            // mirrored (width - saved) so the explorer keeps its remembered pixel width.
            if (explorerOnRight)
            {
                host._leftSplitter.Panel1.Controls.Add(host.EditorHost);
                host._leftSplitter.Panel2.Controls.Add(host._leftDockPanel);
                try
                {
                    host._leftSplitter.SplitterDistance = host._leftSplitter.Width - host._savedLeftSplitterDistance - host._leftSplitter.SplitterWidth;
                }
                catch { }
            }
            // Explorer left: the saved distance is the explorer width directly.
            else
            {
                host._leftSplitter.Panel1.Controls.Add(host._leftDockPanel);
                host._leftSplitter.Panel2.Controls.Add(host.EditorHost);
                try
                {
                    host._leftSplitter.SplitterDistance = host._savedLeftSplitterDistance;
                }
                catch { }
            }

            host._leftSplitter.ResumeLayout(true);
        }

        // View ▸ Float Explorer. Detaches the explorer dock panel into a separate LunaForm
        // window and collapses the splitter so the tab area fills the freed space.
        public void OnFloatExplorer(object sender, EventArgs e)
        {
            // Already floating — just bring the window forward.
            if (host._floatingExplorerForm != null)
            {
                host._floatingExplorerForm.BringToFront();
                return;
            }

            // Before the pane is zeroed: the width it had is the width it comes back to.
            RememberExplorerWidth();

            // Pull the dock panel out of whichever splitter pane currently holds it.
            if (host._explorerDockedRight)
            {
                host._leftSplitter.Panel2.Controls.Remove(host._leftDockPanel);
            }
            else
            {
                host._leftSplitter.Panel1.Controls.Remove(host._leftDockPanel);
            }

            // Collapse the splitter to a single pane holding the tab control, and pin the
            // splitter fixed so the (now empty) explorer pane can't be dragged back open.
            host._leftSplitter.SuspendLayout();
            host._leftSplitter.Panel1.Controls.Clear();
            host._leftSplitter.Panel2.Controls.Clear();
            host._leftSplitter.Panel1.Controls.Add(host.EditorHost);
            try
            {
                host._leftSplitter.SplitterDistance = 0;
                host._leftSplitter.IsSplitterFixed = true;
            }
            catch { }
            host._leftSplitter.ResumeLayout(true);

            // Build the floating host window. LunaForm + LunaColors keep the XP/"Luna"
            // theme consistent with the main window; Owner = host keeps it on top and
            // tied to the app's lifetime.
            host._floatingExplorerForm = new LunaForm();
            host._floatingExplorerForm.Text = "Solution Explorer";
            host._floatingExplorerForm.Size = new Size(300, 600);
            host._floatingExplorerForm.StartPosition = FormStartPosition.CenterScreen;
            host._floatingExplorerForm.ShowInTaskbar = true;
            // A tool window is not a document: there is nothing to minimize it to and nothing to
            // maximize it over, and the buttons that say otherwise are the same lie the panel's own
            // caption used to tell.
            host._floatingExplorerForm.MinimizeBox = false;
            host._floatingExplorerForm.MaximizeBox = false;
            host._floatingExplorerForm.Owner = host;
            host._floatingExplorerForm.BackColor = LunaColors.MainBackground;
            host._floatingExplorerForm.ForeColor = LunaColors.TextPrimary;
            host._floatingExplorerForm.FormClosing += OnFloatingExplorerClosing;
            // Dragged by its own title bar — Windows moves the window, we only get told. The
            // preview is the same one a docked drag shows, and letting go over a site docks there.
            host._floatingExplorerForm.Move += delegate
            {
                host.PreviewFloatDrop(Cursor.Position, host._leftDockPanel.Title);
            };
            host._floatingExplorerForm.ResizeEnd += delegate
            {
                bool? left = host.FloatDropSide(Cursor.Position);
                host.HideFloatDropPreview();
                if (left == null) return;
                if (left.Value) OnDockExplorerLeft(this, EventArgs.Empty);
                else OnDockExplorerRight(this, EventArgs.Empty);
            };

            // Move the dock panel into the floating form (fill) and show it.
            host._leftDockPanel.Dock = DockStyle.Fill;
            host._floatingExplorerForm.Controls.Add(host._leftDockPanel);
            host._floatingExplorerForm.Show();
            // Tell the panel it is floating, so its header button offers Dock rather than Float.
            host._leftDockPanel.IsFloating = true;

            host._statusLabel.Text = "Explorer floating";
        }

        // Closing the floating window redocks instead of destroying: cancel the close and
        // reparent the panel back into the splitter.
        /// <summary>
        /// ✕ on the floating window means GO AWAY — not "dock again".
        ///
        /// It used to cancel the close and re-dock, so a user who had undocked a panel had no way
        /// left to dismiss it: the only button that says "close" put it back on the side of the
        /// screen instead. The panel comes back from View ▸ Panels, and goes back to an edge by
        /// being dragged there. Docking it first is how it keeps its place in the tool group — a
        /// hidden panel has to be hidden FROM somewhere to be shown again.
        /// </summary>
        public void OnFloatingExplorerClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            string front = host.FrontToolPanelTitle(true);
            RedockExplorer();
            if (front != null) host.HideToolPanel(front);
            host._statusLabel.Text = (front ?? "Panel") + " closed — View ▸ Panels brings it back";
        }

        // Tears down the floating window and restores the explorer to its splitter pane,
        // honoring the current left/right dock side. Unsubscribes the closing handler
        // first so Close() here doesn't re-enter this method.
        private void RedockExplorer()
        {
            if (host._floatingExplorerForm == null)
            {
                return;
            }

            host._floatingExplorerForm.FormClosing -= OnFloatingExplorerClosing;

            // The panel is taken back by ADDING it to the splitter, not by removing it from the
            // floating form — and the window is closed only once it is out.
            //
            // LunaForm draws its own caption, so it moves every child into a Dock=Fill host panel;
            // the explorer therefore sits a level below the form and Controls.Remove on the FORM
            // matched nothing and silently did nothing. Close() then disposed that host panel and
            // the explorer inside it, and the redock crashed re-adding a disposed control — leaving
            // the session with no Solution Explorer and no way to bring one back.
            //
            // Controls.Add reparents from whatever the current parent actually is, at any depth, so
            // it cannot be defeated by a container the caller does not know about.
            host._leftSplitter.SuspendLayout();
            host._leftSplitter.IsSplitterFixed = false;
            host._leftSplitter.Panel1.Controls.Clear();
            host._leftSplitter.Panel2.Controls.Clear();

            if (host._explorerDockedRight)
            {
                host._leftSplitter.Panel1.Controls.Add(host.EditorHost);
                host._leftSplitter.Panel2.Controls.Add(host._leftDockPanel);
            }
            else
            {
                host._leftSplitter.Panel1.Controls.Add(host._leftDockPanel);
                host._leftSplitter.Panel2.Controls.Add(host.EditorHost);
            }
            ApplyExplorerWidth();

            host._leftSplitter.ResumeLayout(true);
            host._leftDockPanel.Dock = DockStyle.Fill;

            // Now that the explorer is back in the main window, the floating one owns nothing and
            // is safe to destroy.
            host._floatingExplorerForm.Close();
            host._floatingExplorerForm.Dispose();
            host._floatingExplorerForm = null;
            host._leftDockPanel.IsFloating = false;

            host._statusLabel.Text = host._explorerDockedRight ? "Explorer docked right" : "Explorer docked left";
        }

        /// <summary>
        /// The narrowest the explorer pane may be restored to.
        ///
        /// A remembered width is only worth remembering if the panel could be USED at it. The
        /// collapsed strip is about 26px; restoring to that leaves a sliver against the window edge
        /// that looks exactly like a missing Solution Explorer.
        /// </summary>
        private const int MinimumExplorerWidth = 160;

        /// <summary>
        /// Remembers the current width as the explorer's — unless it is already collapsed.
        ///
        /// THE BUG THIS EXISTS FOR: auto-hide fired on every pointer exit, collapsed or not, and
        /// each firing saved the CURRENT distance. The second one therefore recorded the collapsed
        /// strip as the panel's width, and from then on every expand, redock and dock-side change
        /// restored a 26px sliver. Reported from the running IDE as "solution explorer has vanished
        /// and I cannot recover it" — it had not vanished; it was 26 pixels wide, permanently.
        /// </summary>
        private void RememberExplorerWidth()
        {
            if (host._leftSplitter == null) return;
            int current = host._explorerDockedRight
                ? host._leftSplitter.Width - host._leftSplitter.SplitterDistance - host._leftSplitter.SplitterWidth
                : host._leftSplitter.SplitterDistance;
            if (current >= MinimumExplorerWidth) host._savedLeftSplitterDistance = current;
        }

        /// <summary>The remembered width, never narrower than a panel can be read at.</summary>
        private int ExplorerWidth()
        {
            return Math.Max(MinimumExplorerWidth, host._savedLeftSplitterDistance);
        }

        /// <summary>
        /// Puts the explorer back in a state the user can see and use, whatever it was in:
        /// floating, collapsed, auto-hidden, or squeezed to nothing.
        ///
        /// Every route back — the View menu's two dock commands and its Panels entry — goes through
        /// this, because a tool window that can get into a state no command reverses is a tool
        /// window the user loses.
        /// </summary>
        public void RestoreExplorer()
        {
            if (host._floatingExplorerForm != null) RedockExplorer();
            host._leftDockPanel.IsExpanded = true;
            host._leftDockPanel.IsPinned = true;
            host._leftDockPanel.Visible = true;
            ApplyExplorerWidth();
        }

        /// <summary>Sets the splitter to the remembered width, on whichever side the panel is.</summary>
        private void ApplyExplorerWidth()
        {
            if (host._leftSplitter == null) return;
            try
            {
                host._leftSplitter.IsSplitterFixed = false;
                host._leftSplitter.SplitterDistance = host._explorerDockedRight
                    ? Math.Max(0, host._leftSplitter.Width - ExplorerWidth() - host._leftSplitter.SplitterWidth)
                    : ExplorerWidth();
            }
            catch (Exception) { /* before the splitter is sized there is nothing to divide */ }
        }

        // ---- Left Panel Dock Events ----

        // DockablePanel raised its collapse toggle. On expand, restore the remembered
        // splitter width; on collapse, save the current width first (so expand can undo
        // it) and shrink the pane to just the panel's collapsed header strip.
        public void OnLeftPanelCollapseChanged(object sender, EventArgs e)
        {
            // While the panel is auto-hidden the OVERLAY owns its geometry, and the pane it left
            // behind must stay a rail. Peeking expands the panel — that is how it stops painting
            // itself as an edge strip — and letting that re-widen the splitter put the push
            // behaviour straight back: the document jumped every time the panel came out.
            if (!host._leftDockPanel.IsPinned) return;

            if (host._leftDockPanel.IsExpanded)
            {
                // Restore
                ApplyExplorerWidth();
            }
            else
            {
                // Collapse. The width is remembered only if it is one the panel could be used at —
                // collapsing twice must not teach the IDE that the panel is 26px wide.
                RememberExplorerWidth();
                SetExplorerWidth(DockablePanel.RailWidth);
            }
        }

        /// <summary>
        /// The auto-hide button was pressed: the panel goes to the edge, or comes back to stay.
        ///
        /// This used to be empty — "auto-hide logic is in DockablePanel" — and the panel alone
        /// cannot do it: what makes a panel narrow is the SPLITTER, which only the host owns. So
        /// unpinning set a flag and changed a glyph, and nothing on screen moved. The edge width is
        /// the panel's rail width, not its collapsed header height: a caption bar squeezed to 26px
        /// is a broken title bar, and a rail is a rail.
        /// </summary>
        public void OnLeftPanelPinChanged(object sender, EventArgs e)
        {
            if (host._leftDockPanel.IsPinned)
            {
                host.LeftAutoHide.Leave();
                host._leftDockPanel.IsExpanded = true;
                ApplyExplorerWidth();
                host._statusLabel.Text = host._leftDockPanel.Title + " pinned open";
            }
            else
            {
                // The width is remembered BEFORE the pane shrinks to a rail: it is what the panel
                // comes back out at, and once the rail is in place there is nothing left to read.
                RememberExplorerWidth();
                host.LeftAutoHide.Enter();
                host._statusLabel.Text = host._leftDockPanel.Title
                    + " auto-hides — hover the left edge to bring it back";
            }
        }

        /// <summary>Puts the splitter at <paramref name="width"/> for the explorer, on its side.</summary>
        internal void SetExplorerPaneWidth(int width) { SetExplorerWidth(width); }

        /// <summary>Puts the splitter at <paramref name="width"/> for the explorer, on its side.</summary>
        private void SetExplorerWidth(int width)
        {
            if (host._leftSplitter == null) return;
            try
            {
                host._leftSplitter.IsSplitterFixed = false;
                host._leftSplitter.SplitterDistance = host._explorerDockedRight
                    ? Math.Max(0, host._leftSplitter.Width - width - host._leftSplitter.SplitterWidth)
                    : width;
            }
            catch (Exception) { /* before the splitter is sized there is nothing to divide */ }
        }

        // Auto-hide fired (pointer left an unpinned panel): remember the width, collapse
        // to the header strip, and mark not-expanded. No-op while the panel is pinned.
        public void OnLeftPanelAutoHide(object sender, EventArgs e)
        {
            // Putting a peeked panel away is the OVERLAY's business now, and it is driven by a
            // click outside rather than by the pointer wandering off: a panel that vanishes the
            // moment the mouse strays cannot be read, let alone used.
            if (host._leftDockPanel.IsPinned) return;
            host.LeftAutoHide.Unpeek();
        }

        // Auto-show fired (pointer re-entered a collapsed unpinned panel): restore the
        // saved width and re-expand. Mirror of OnLeftPanelAutoHide.
        public void OnLeftPanelAutoShow(object sender, EventArgs e)
        {
            if (!host._leftDockPanel.IsPinned) host.LeftAutoHide.Peek();
        }

        // View ▸ Float Navigation Tree toggle. Flips between floating and docked, then
        // syncs the menu item's checkmark and persists the choice to settings so the
        // tree comes back in the same state next launch.
        public void OnToggleFloatNavigationTree(object sender, EventArgs e)
        {
            if (host._treeFloating) RedockNavigationTree();
            else FloatNavigationTree();
            if (host._floatTreeMenuItem != null) host._floatTreeMenuItem.Checked = host._treeFloating;
            if (host._settingsManager != null)
            {
                host._settingsManager.Settings.TreeFloating = host._treeFloating;
                host._settingsManager.Save();
            }
        }

        // Undocks the navigation tree into its own borderless Form. Collapses the left
        // splitter pane entirely (Panel1Collapsed) so the tab area reclaims the space,
        // then reparents the dock panel into the new floating window (owned by host).
        public void FloatNavigationTree()
        {
            if (host._treeFloating || host._leftDockPanel == null || host._leftSplitter == null) return;

            host._savedSplitterDistance = host._leftSplitter.SplitterDistance;

            // Remove from the left splitter and give its space to the tab area.
            host._leftSplitter.Panel1.Controls.Remove(host._leftDockPanel);
            host._leftSplitter.Panel1Collapsed = true;

            host._floatingTreeForm = new Form
            {
                Text = "Navigation",
                Size = new Size(280, 640),
                StartPosition = FormStartPosition.CenterScreen,
                ShowInTaskbar = true,
                Owner = host,
                BackColor = LunaColors.MainBackground,
                ForeColor = LunaColors.TextPrimary,
            };
            host._leftDockPanel.Dock = DockStyle.Fill;
            host._floatingTreeForm.Controls.Add(host._leftDockPanel);
            host._floatingTreeForm.FormClosing += OnFloatingTreeClosing;
            host._floatingTreeForm.Show();

            host._treeFloating = true;
            host._statusLabel.Text = "Navigation tree undocked";
        }

        // Reverse of FloatNavigationTree: unsubscribe the closing handler (so Close()
        // below doesn't re-enter via OnFloatingTreeClosing), move the dock panel back
        // into the un-collapsed splitter pane, restore its width, and dispose the window.
        public void RedockNavigationTree()
        {
            if (!host._treeFloating || host._floatingTreeForm == null) return;

            host._floatingTreeForm.FormClosing -= OnFloatingTreeClosing;
            if (host._floatingTreeForm.Controls.Contains(host._leftDockPanel))
                host._floatingTreeForm.Controls.Remove(host._leftDockPanel);

            host._leftDockPanel.Dock = DockStyle.Fill;
            host._leftSplitter.Panel1Collapsed = false;
            host._leftSplitter.Panel1.Controls.Add(host._leftDockPanel);
            if (host._savedSplitterDistance > 0)
                try { host._leftSplitter.SplitterDistance = host._savedSplitterDistance; } catch { }

            host._floatingTreeForm.Close();
            host._floatingTreeForm = null;
            host._treeFloating = false;
            host._statusLabel.Text = "Navigation tree redocked";
        }

        private void OnFloatingTreeClosing(object sender, FormClosingEventArgs e)
        {
            // When the user closes the floating window, treat it as "redock" rather than
            // destroying the tree outright.
            if (host._treeFloating)
            {
                e.Cancel = true;
                RedockNavigationTree();
                if (host._floatTreeMenuItem != null) host._floatTreeMenuItem.Checked = false;
                if (host._settingsManager != null)
                {
                    host._settingsManager.Settings.TreeFloating = false;
                    host._settingsManager.Save();
                }
            }
        }
    }
}
