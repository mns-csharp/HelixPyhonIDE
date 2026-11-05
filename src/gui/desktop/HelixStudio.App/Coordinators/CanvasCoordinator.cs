namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.UI.Interaction;
    using HelixPythonNamespace.UI.Interaction.Commands;
    using HelixPythonNamespace.UI.Projection;
    using HelixPythonNamespace.UI.Rendering;
    using HelixPythonNamespace.Workspace.Services;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// Owns the diagram canvas surface and its category designer tabs: type creation,
    /// linking, alignment/distribution, layout, zoom, refresh, image export, the
    /// canvas context menu, and node/selection/command event handling. References the
    /// host form via <c>host</c>; MainForm keeps thin wrappers for menu/toolbar/event
    /// entry points.
    /// </summary>
    public sealed class CanvasCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

        // ICoordinator lifecycle: cache the shared context + direct host reference.
        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        // Drop references on teardown so the coordinator doesn't pin the form.
        public void Detach()
        {
            _context = null;
            host = null;
        }

        // Build the canvas right-click menu (add types/relationships, nested type, view/edit/
        // hide/delete). Handlers delegate to MainForm wrappers. The Opening handler is wired last
        // so item enablement (e.g. View Source) can be recomputed against the live selection each
        // time the menu pops.
        public ContextMenuStrip CreateCanvasContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem drillUp = new ToolStripMenuItem(
                "Drill Up",
                ToolbarIcons.DrillUp(),
                host.OnDrillUp);
            drillUp.ShortcutKeyDisplayString = "Alt+Up";
            menu.Items.Add(drillUp);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Add Interface", null, delegate { host.AddTypeAtMouse(TypeKind.Interface); });
            menu.Items.Add("Add Abstract Class", null, delegate { host.AddTypeAtMouse(TypeKind.AbstractClass); });
            menu.Items.Add("Add Concrete Class", null, delegate { host.AddTypeAtMouse(TypeKind.ConcreteClass); });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem relMenu = new ToolStripMenuItem("Add Relationship");
            relMenu.DropDownItems.Add("Implements", null, delegate { host.StartRelationship(RelationshipKind.Implements); });
            relMenu.DropDownItems.Add("Inherits", null, delegate { host.StartRelationship(RelationshipKind.Inherits); });
            menu.Items.Add(relMenu);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Add Nested Type...", null, host.OnAddNestedType);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("View Source", null, host.OnViewSource);
            menu.Items.Add("Edit Type...", null, host.OnEditType);
            menu.Items.Add("Hide Selected", null, host.OnHideSelected);
            menu.Items.Add("Delete", null, host.OnDeleteSelection);

            // Dynamically enable/disable View Source based on selection
            menu.Opening += delegate
            {
                drillUp.Enabled = host.CanDrillUp;
            };
            menu.Opening += host.OnCanvasContextMenuOpening;

            return menu;
        }

        // Per-node context menu, raised when the canvas requests a menu for a specific node.
        // Resolves the clicked visual to its TypeModel via the projection builder, then offers
        // Goto Designer / Goto Source (the latter only when the type has a backing source file).
        public void OnCanvasNodeContextRequested(object sender, NodeContextRequestedEventArgs e)
        {
            if (e == null || e.NodeVisual == null || host._currentSolution == null) return;
            DiagramCanvas canvas = sender as DiagramCanvas;
            if (canvas == null || host._projectionBuilder == null) return;
            string fqn = host._projectionBuilder.FindFQN(e.NodeVisual);
            if (string.IsNullOrEmpty(fqn)) return;
            TypeModel type = host._currentSolution.FindType(fqn);
            if (type == null) return;

            // Capture the resolved type for the deferred menu-item delegates (closure safety).
            TypeModel captured = type;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Goto Designer", null, delegate { host.GoToDesigner(captured); });
            if (!string.IsNullOrEmpty(type.SourceFilePath))
            {
                menu.Items.Add("Goto Source", null, delegate { host.GoToSource(captured); });
            }
            menu.Show(canvas, e.ScreenLocation);
        }

        // Add a type via the toolbar/menu (as opposed to the context menu, which uses the mouse
        // location) — drops it at a fixed default canvas position.
        internal void AddType(TypeKind kind)
        {
            host.AddTypeAtPosition(kind, 100, 100);
        }

        // "Link" command: infer the relationship(s) to create among the selected types using a
        // small rule set, else prompt. Operates on the active canvas + its projection builder.
        internal void OnLinkSelected(object sender, EventArgs e)
        {
            DiagramCanvas activeCanvas = host.GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = host.GetProjectionBuilderForCanvas(activeCanvas);
            if (host._currentSolution == null || activeBuilder == null || activeCanvas == null)
            {
                host._statusLabel.Text = "Open a solution first.";
                return;
            }

            // Gather selected type models (skip visuals that don't map to a type, e.g. container nodes).
            var selected = new List<TypeModel>();
            foreach (var v in activeCanvas.SelectionManager.SelectedNodes)
            {
                string fqn = activeBuilder.FindFQN(v);
                if (string.IsNullOrEmpty(fqn)) continue;
                TypeModel tm = host._currentSolution.FindType(fqn);
                if (tm != null) selected.Add(tm);
            }
            if (selected.Count < 2)
            {
                host._statusLabel.Text = "Select at least two types on the canvas to link.";
                return;
            }

            int interfaceCount = 0;
            int abstractCount = 0;
            int concreteCount = 0;
            foreach (var t in selected)
            {
                if (t.TypeKind == TypeKind.Interface) interfaceCount++;
                else if (t.TypeKind == TypeKind.AbstractClass) abstractCount++;
                else if (t.TypeKind == TypeKind.ConcreteClass || t.TypeKind == TypeKind.Struct) concreteCount++;
            }

            try
            {
                using (host._fileWatcher?.SuspendWhileEditing())
                {
                    // Rule 1: exactly one interface + ≥ 1 non-interface → each non-interface implements.
                    if (interfaceCount == 1 && selected.Count > 1)
                    {
                        TypeModel iface = selected.Find(t => t.TypeKind == TypeKind.Interface);
                        int linked = 0;
                        foreach (var other in selected)
                        {
                            if (other == iface) continue;
                            host._typeService.AddImplementation(other, iface);
                            linked++;
                        }
                        host.RefreshAfterRename();
                        host._statusLabel.Text = string.Format("Linked {0} type(s) to implement {1}",
                            linked, iface.Name);
                        return;
                    }

                    // Rule 2: exactly one abstract class + ≥ 1 concrete/struct (no interfaces) → inherit.
                    if (interfaceCount == 0 && abstractCount == 1 && concreteCount >= 1)
                    {
                        TypeModel baseType = selected.Find(t => t.TypeKind == TypeKind.AbstractClass);
                        int linked = 0;
                        foreach (var derived in selected)
                        {
                            if (derived == baseType) continue;
                            host._typeService.AddInheritance(derived, baseType);
                            linked++;
                        }
                        host.RefreshAfterRename();
                        host._statusLabel.Text = string.Format("Linked {0} type(s) to inherit from {1}",
                            linked, baseType.Name);
                        return;
                    }

                    // Rule 3: exactly two entities that didn't fit Rule 1/2 → prompt.
                    if (selected.Count == 2)
                    {
                        using (var dlg = new HelixPythonNamespace.App.Dialogs.RelationshipPromptDialog(selected[0], selected[1]))
                        {
                            if (dlg.ShowDialog(host) != DialogResult.OK) return;
                            if (dlg.ChosenKind == RelationshipKind.Implements)
                            {
                                host._typeService.AddImplementation(dlg.ChosenDerived, dlg.ChosenBase);
                            }
                            else
                            {
                                host._typeService.AddInheritance(dlg.ChosenDerived, dlg.ChosenBase);
                            }
                            host.RefreshAfterRename();
                            host._statusLabel.Text = string.Format("Linked {0} → {1} ({2})",
                                dlg.ChosenDerived.Name, dlg.ChosenBase.Name, dlg.ChosenKind);
                        }
                        return;
                    }

                    host._statusLabel.Text = "Selection is ambiguous — Link works with 1 interface + classes, 1 abstract + classes, or a single pair.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(host, "Link failed: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Drag-and-drop from the project tree onto the canvas: locate the dropped type's existing
        // node visual, then re-center it on the drop point via an undoable MoveNodesCommand (so it
        // participates in undo/redo and edge re-routing) and persist the new position/size to the
        // layout store. No-ops if the type isn't currently visible on the active canvas.
        public void OnTypeDroppedFromTree(object sender, TypeDroppedEventArgs e)
        {
            if (e == null || e.Type == null || host._currentSolution == null) return;

            DiagramCanvas activeCanvas = host.GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = host.GetProjectionBuilderForCanvas(activeCanvas);
            if (activeCanvas == null || activeBuilder == null) return;

            NodeVisual visual = activeBuilder.FindVisual(e.Type.FullyQualifiedName);
            if (visual == null && e.Type.Project != null)
            {
                // §5.8: dropping a not-yet-visible type OPENS it in the designer rather
                // than rejecting the drop — make its project visible, rebuild, and retry.
                var visible = host._projectTreePanel.GetCheckedProjects();
                if (!visible.Contains(e.Type.Project.Name))
                {
                    visible.Add(e.Type.Project.Name);
                    host._projectTreePanel.SetCheckedProjects(visible);
                    host.OnProjectVisibilityChanged(host._projectTreePanel,
                        new HelixPythonNamespace.UI.Controls.ProjectVisibilityChangedEventArgs(e.Type.Project.Name, true));
                    visual = activeBuilder.FindVisual(e.Type.FullyQualifiedName);
                }
            }
            if (visual == null)
            {
                host._statusLabel.Text = string.Format(
                    "Type '{0}' is not visible on the current canvas", e.Type.Name);
                return;
            }

            System.Drawing.RectangleF bounds = visual.WorldBounds;
            System.Drawing.PointF original = new System.Drawing.PointF(bounds.X, bounds.Y);
            System.Drawing.PointF target = new System.Drawing.PointF(
                e.WorldLocation.X - bounds.Width / 2f,
                e.WorldLocation.Y - bounds.Height / 2f);

            var originalPositions = new System.Collections.Generic.Dictionary<string, System.Drawing.PointF>
            {
                { visual.NodeId, original }
            };
            var finalPositions = new System.Collections.Generic.Dictionary<string, System.Drawing.PointF>
            {
                { visual.NodeId, target }
            };

            MoveNodesCommand cmd = new MoveNodesCommand(
                activeCanvas.SceneGraph, originalPositions, finalPositions, activeCanvas.EdgeRouter);
            activeCanvas.CommandDispatcher.Execute(cmd);

            if (host._layoutStore != null)
            {
                NodeLayoutData layout = host._layoutStore.GetPosition(e.Type.FullyQualifiedName) ?? new NodeLayoutData();
                layout.PositionX = target.X;
                layout.PositionY = target.Y;
                layout.Width = bounds.Width;
                layout.Height = bounds.Height;
                host._layoutStore.SetPosition(e.Type.FullyQualifiedName, layout);
            }

            activeCanvas.Invalidate();
            host._statusLabel.Text = string.Format("Moved '{0}' to drop location", e.Type.Name);
        }

        // Align 2+ selected nodes (left/right/top/etc.) through an undoable AlignNodesCommand,
        // which also re-routes edges. Needs at least two nodes to have an alignment reference.
        internal void AlignSelected(AlignmentKind kind)
        {
            DiagramCanvas activeCanvas = host.GetActiveCanvas();
            if (activeCanvas == null) return;

            var selected = activeCanvas.SelectionManager.SelectedNodes;
            if (selected.Count < 2)
            {
                host._statusLabel.Text = "Select 2 or more nodes to align";
                return;
            }

            List<NodeVisual> nodes = new List<NodeVisual>(selected);
            AlignNodesCommand cmd = new AlignNodesCommand(
                activeCanvas.SceneGraph, activeCanvas.EdgeRouter, nodes, kind);
            activeCanvas.CommandDispatcher.Execute(cmd);
            activeCanvas.Invalidate();
            host._statusLabel.Text = string.Format("Aligned {0} nodes ({1})", nodes.Count, kind);
        }

        // Evenly distribute 3+ selected nodes horizontally/vertically via an undoable
        // DistributeNodesCommand. Needs at least three nodes for even spacing to be meaningful.
        internal void DistributeSelected(DistributeKind kind)
        {
            DiagramCanvas activeCanvas = host.GetActiveCanvas();
            if (activeCanvas == null) return;

            var selected = activeCanvas.SelectionManager.SelectedNodes;
            if (selected.Count < 3)
            {
                host._statusLabel.Text = "Select 3 or more nodes to distribute";
                return;
            }

            List<NodeVisual> nodes = new List<NodeVisual>(selected);
            DistributeNodesCommand cmd = new DistributeNodesCommand(
                activeCanvas.SceneGraph, activeCanvas.EdgeRouter, nodes, kind);
            activeCanvas.CommandDispatcher.Execute(cmd);
            activeCanvas.Invalidate();
            host._statusLabel.Text = string.Format("Distributed {0} nodes ({1})", nodes.Count, kind);
        }

        // Double-click behavior depends on what was hit. A container node (solution/project/
        // namespace rectangle) drills the navigation one level deeper, rebuilding the projection
        // for that scope and re-fitting the viewport; a plain type node opens the type editor.
        // Before drilling we snapshot node positions + the current viewport so the level can be
        // restored on the way back up, and sync the category tabs to the new level.
        public void OnNodeDoubleClicked(object sender, NodeVisualEventArgs e)
        {
            if (e == null || e.NodeVisual == null)
            {
                return;
            }

            // A circle-mode node is a type that doesn't match the "Show:" filter — rendered as a
            // placeholder so its relationships to the filtered-in types stay visible. Double-
            // clicking it switches the filter to that type's own category and re-centers on it,
            // rather than drilling or opening the editor.
            if (e.NodeVisual.IsCircleMode)
            {
                SolutionProjectionBuilder circleBuilder = host._projectionBuilder;
                if (circleBuilder == null || host._currentSolution == null) return;

                string circleFqn = circleBuilder.FindFQN(e.NodeVisual);
                if (string.IsNullOrEmpty(circleFqn)) return;

                TypeModel circleType = host._currentSolution.FindType(circleFqn);
                if (circleType == null) return;

                host.NavigateToTypeByFilter(circleType);
                return;
            }

            // Check if host is a container node (solution/project/namespace)
            var containerData = e.NodeVisual.ContainerData;
            if (containerData != null && host._navigationState != null && host._currentSolution != null)
            {
                // Save current positions before navigating
                if (host._projectionBuilder != null)
                {
                    host._projectionBuilder.SaveCurrentPositions();
                }

                // Save current viewport state
                host._navigationState.SavedViewport.PanX = host._canvas.Viewport.PanOffset.X;
                host._navigationState.SavedViewport.PanY = host._canvas.Viewport.PanOffset.Y;
                host._navigationState.SavedViewport.Zoom = host._canvas.Viewport.Zoom;

                switch (host._navigationState.Level)
                {
                    case NavigationLevel.Solution:
                        // Double-clicking solution rectangle → show all projects
                        host._navigationState = host._navigationState.DrillDownToProjects();
                        host._projectionBuilder.BuildProjectLevel(host._currentSolution);
                        break;

                    case NavigationLevel.Project:
                        // Double-clicking a project rectangle → show its namespaces
                        host._navigationState = host._navigationState.DrillDownToNamespaces(containerData.ScopeKey);
                        host._projectionBuilder.BuildNamespaceLevel(host._currentSolution, containerData.ScopeKey);
                        break;

                    case NavigationLevel.Namespace:
                        // Double-clicking a namespace rectangle → show its types
                        host._navigationState = host._navigationState.DrillDownToTypes(containerData.ScopeKey);
                        host._projectionBuilder.BuildTypeLevelForNamespace(
                            host._currentSolution,
                            host._navigationState.ProjectName,
                            containerData.ScopeKey);
                        break;
                }

                // Reset viewport for new level, then fit — but never below the readable floor, so
                // drilling into a project with hundreds of namespace boxes stops at a legible zoom
                // and anchors top-left rather than shrinking to an unreadable grid (iter 356).
                host._canvas.Viewport.Reset();
                host._canvas.FitContentReadable(80f);
                host._canvas.Invalidate();

                host.UpdateBreadcrumb();

                // The breadcrumb has its own status segment (UpdateBreadcrumb). Writing it into the primary
                // status label as well duplicated it AND destroyed whatever was there — on a large
                // solution it overwrote "Loaded: N project(s), M type(s)" before the user could read it.
                host.UpdateBreadcrumb();
                return;
            }

            // At type level, double-click opens editor (existing behavior)
            host.OnEditType(sender, EventArgs.Empty);
        }

        // Canvas selection changed: refresh selection-dependent UI. Currently just toggles the Link
        // button (needs 2+ types) and resolves a single selected node to its TypeModel — the former
        // Properties-panel hook has been removed, so the single-selection branch now falls through.
        public void OnSelectionChanged(object sender, EventArgs e)
        {
            DiagramCanvas activeCanvas = host.GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = host.GetProjectionBuilderForCanvas(activeCanvas);

            // Link toolbar button active iff ≥ 2 types selected on the active canvas.
            if (host._linkButton != null && activeCanvas != null)
            {
                host._linkButton.Enabled = activeCanvas.SelectionManager.SelectedNodes.Count >= 2;
            }

            if (activeCanvas.SelectionManager.SelectedNodes.Count == 1 &&
                host._currentSolution != null &&
                activeBuilder != null)
            {
                NodeVisual visual = activeCanvas.SelectionManager.SelectedNodes[0];
                string fqn = activeBuilder.FindFQN(visual);
                if (!string.IsNullOrEmpty(fqn))
                {
                    TypeModel typeModel = host._currentSolution.FindType(fqn);
                    if (typeModel != null)
                    {
                        // Properties panel removed
                        return;
                    }
                }
            }
            // Properties panel removed
        }

        // Fires after any canvas command runs. Marks the solution dirty, then persists the model-
        // level side effects that the in-memory command alone doesn't write back: a new relationship
        // (AddRelationshipCommand) into the type model, and edge removals (DeleteSelectionCommand)
        // back out of it. Other commands (moves, aligns) only affect layout, so no persistence here.
        public void OnCommandExecuted(object sender, CommandExecutedEventArgs e)
        {
            if (e == null || e.Command == null)
            {
                return;
            }

            // Any canvas command marks the solution as having unsaved layout / structural changes.
            host.MarkSolutionDirty();

            var addRelCmd = e.Command as AddRelationshipCommand;
            if (addRelCmd != null)
            {
                if (host._currentSolution == null)
                {
                    host._statusLabel.Text = "Warning: No solution loaded, relationship not persisted";
                    return;
                }
                if (host._projectionBuilder == null)
                {
                    host._statusLabel.Text = "Warning: Projection not initialized, relationship not persisted";
                    return;
                }
                host.PersistRelationship(addRelCmd);
            }

            var deleteCmd = e.Command as DeleteSelectionCommand;
            if (deleteCmd != null && host._currentSolution != null)
            {
                host.PersistEdgeDeletions(deleteCmd);
            }
        }

        // Apply grid auto-layout to the main canvas: recreate the layout store + projection builder
        // with a GridAutoLayoutStrategy, rebuild from the solution (honoring the checked-projects
        // filter), then null the cached category builders so they too are recreated against the new
        // layout store when the category tabs rebuild.
        internal void OnAutoLayout(object sender, EventArgs e)
        {
            if (host._currentSolution == null || host._projectionBuilder == null)
            {
                return;
            }

            var checkedProjects = host._projectTreePanel.GetCheckedProjects();

            host._layoutStore = new JsonLayoutMetadataStore(host._currentSolution.FilePath);
            var autoLayout = new GridAutoLayoutStrategy();
            host._projectionBuilder = new SolutionProjectionBuilder(host._canvas.SceneGraph, host._layoutStore, autoLayout);
            if (host._designerCategoryCombo != null)
            {
                var mode = (HelixPythonNamespace.UI.Rendering.CategoryFilterMode)host._designerCategoryCombo.SelectedIndex;
                host._projectionBuilder.CategoryFilter = mode;
                host._projectionBuilder.UseSidecarLayout = mode != HelixPythonNamespace.UI.Rendering.CategoryFilterMode.All;
            }
            host._projectionBuilder.SetVisibleProjects(checkedProjects);
            host._projectionBuilder.RebuildFromSolution(host._currentSolution);
            host._canvas.Invalidate();

            host._statusLabel.Text = "Auto layout applied";
        }

        // Zoom the canvas so all content fits, then refresh the zoom-percentage label.
        internal void OnZoomToFit(object sender, EventArgs e)
        {
            host._canvas.ZoomToFit();
            host.UpdateZoomLabel();
            host._statusLabel.Text = "Zoomed to fit content";
        }

        // Zoom in/out by a fixed 15% step (the reciprocal keeps in/out symmetric).
        internal void OnZoomIn(object sender, EventArgs e)
        {
            host.ApplyZoom(1.15f);
        }

        internal void OnZoomOut(object sender, EventArgs e)
        {
            host.ApplyZoom(1f / 1.15f);
        }

        // Render the active canvas's scene graph to a 2x white-background bitmap and put it on the
        // clipboard as an image (for pasting into docs/chat).
        internal void OnCopyDiagramImage(object sender, EventArgs e)
        {
            DiagramCanvas canvas = host.GetActiveCanvas() ?? host._canvas;
            if (canvas == null || canvas.SceneGraph == null) return;
            try
            {
                using (Bitmap bmp = HelixPythonNamespace.UI.Rendering.CanvasImageRenderer.Render(
                    canvas.SceneGraph, 2f, Color.White))
                {
                    Clipboard.SetImage(bmp);
                }
                host._statusLabel.Text = "Diagram copied to clipboard";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to copy diagram as image: {0}", ex.Message),
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Refresh: recompute a fresh in-memory layout WITHOUT persisting. ForceFreshLayout /
        // ResetSidecar discard any saved positions so nodes recompact from scratch (both for the
        // All view and, if the "Show:" combo is on a category filter, its sidecar); the status
        // line reminds the user to Save Layout to keep it.
        internal void OnRefreshCanvas(object sender, EventArgs e)
        {
            if (host._currentSolution == null || host._projectionBuilder == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }

            List<string> checkedProjects = host._projectTreePanel != null
                ? host._projectTreePanel.GetCheckedProjects()
                : new List<string>();

            host._projectionBuilder.SetVisibleProjects(checkedProjects);
            host._projectionBuilder.ForceFreshLayout = true;
            host._projectionBuilder.ResetSidecar();
            host._projectionBuilder.RebuildFromSolution(host._currentSolution);
            host._canvas.Invalidate();
            host._canvas.ZoomToFit();

            host._statusLabel.Text = "Canvas refreshed (in-memory — use File → Save Layout to persist)";
        }

        // Grid-spacing dialog: on OK, push the chosen spacing into the shared LayoutConstants (so
        // rendering/snapping pick it up immediately) and persist it to settings.
        internal void OnGridSettings(object sender, EventArgs e)
        {
            float current = HelixPythonNamespace.UI.Rendering.LayoutConstants.GridSpacing;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.GridSettingsDialog(current))
            {
                if (dlg.ShowDialog(host) == DialogResult.OK)
                {
                    HelixPythonNamespace.UI.Rendering.LayoutConstants.GridSpacing = dlg.SelectedGridSpacing;
                    if (host._settingsManager != null)
                    {
                        host._settingsManager.Settings.GridSpacing = dlg.SelectedGridSpacing;
                        host._settingsManager.Save();
                    }
                }
            }
        }

        // ---- navigation: open a source-code element (type/function's owning type) on the canvas ----

        // Activate the All designer tab and center the viewport on the given type. Entry point for
        // Solution Explorer's "Open in Designer", the editor's "Go to Designer", and Ctrl+F's Find
        // Type dialog — anything that wants to land on a specific type on the canvas.
        internal void NavigateToType(TypeModel typeModel)
        {
            if (typeModel == null) return;
            if (host._projectionBuilder == null) return;

            // The Designer Canvas top-level tab is closable via its own × button (like the Editor
            // tab). If it's been closed, TabControl.SelectedTab = _graphicalDesignerTab is a
            // SILENT NO-OP — WinForms only accepts a TabPage that's actually a current member of
            // TabPages — so every subsequent Width/Height read stays at the unlaid-out default
            // (200x100) forever, no matter how long anything waits: this was never a timing race,
            // it was a missing tab. Re-add it the same way OpenInEditor restores a closed Editor
            // tab before selecting it.
            if (host._mainTabControl != null && host._graphicalDesignerTab != null &&
                !host._mainTabControl.TabPages.Contains(host._graphicalDesignerTab))
            {
                host.ShowTab("designer");
            }
            if (host._mainTabControl != null) host._mainTabControl.SelectedTab = host._graphicalDesignerTab;
            if (host._designerInnerTabControl != null) host._designerInnerTabControl.SelectedTab = host._designerTab;
            host.PerformLayout();

            // Force the "Show:" filter to the type's own category so it lands as a full node
            // rather than staying hidden behind whatever filter was previously selected.
            if (host._designerCategoryCombo != null)
            {
                HelixPythonNamespace.UI.Rendering.CategoryFilterMode wantMode = MainForm.CategoryFilterModeFor(typeModel.TypeKind);
                if ((HelixPythonNamespace.UI.Rendering.CategoryFilterMode)host._designerCategoryCombo.SelectedIndex != wantMode)
                {
                    host._designerCategoryCombo.SelectedIndex = (int)wantMode;
                }
            }

            NodeVisual visual = host._projectionBuilder.FindVisual(typeModel.FullyQualifiedName);
            if (visual == null)
            {
                // The All tab may be sitting at a higher drill-down level (solution/project/
                // namespace motif) with no type nodes on screen at all, so the target type has no
                // visual yet. Ensure the type's project is visible, then drill the SAME shared
                // navigation state (breadcrumb, canvas context menu, category-tab sync all read
                // from it) down to the type's own project + namespace, exactly as if the user had
                // double-clicked through each level -- instead of silently swapping in an unscoped
                // flat all-types view that leaves the navigation level stale.
                List<string> visible = host._projectTreePanel != null
                    ? new List<string>(host._projectTreePanel.GetCheckedProjects())
                    : new List<string>();
                string projName = typeModel.Project != null ? typeModel.Project.Name : null;
                if (!string.IsNullOrEmpty(projName) && !visible.Contains(projName))
                {
                    visible.Add(projName);
                    if (host._projectTreePanel != null) host._projectTreePanel.SetCheckedProjects(visible);
                }
                host._projectionBuilder.SetVisibleProjects(visible);

                if (host._navigationState != null && host._currentSolution != null && !string.IsNullOrEmpty(projName))
                {
                    string nsName = string.IsNullOrEmpty(typeModel.Namespace) ? "(global)" : typeModel.Namespace;

                    host._projectionBuilder.SaveCurrentPositions();
                    host._navigationState = new NavigationViewState(host._currentSolution.Name)
                        .DrillDownToProjects()
                        .DrillDownToNamespaces(projName)
                        .DrillDownToTypes(nsName);
                    host._projectionBuilder.BuildTypeLevelForNamespace(host._currentSolution, projName, nsName);
                    host.UpdateBreadcrumb();
                }
                else
                {
                    host._projectionBuilder.RebuildFromSolution(host._currentSolution);
                }

                visual = host._projectionBuilder.FindVisual(typeModel.FullyQualifiedName);
            }
            if (visual == null || host._canvas == null) return;

            host._canvas.SelectionManager.ClearSelection();
            host._canvas.SelectionManager.Select(visual);

            var viewport = host._canvas.Viewport;
            float centerX = visual.WorldBounds.X + visual.WorldBounds.Width / 2f;
            float centerY = visual.WorldBounds.Y + visual.WorldBounds.Height / 2f;
            float screenCenterX = host._canvas.Width / 2f;
            float screenCenterY = host._canvas.Height / 2f;
            viewport.SetPanOffset(
                screenCenterX - centerX * viewport.Zoom,
                screenCenterY - centerY * viewport.Zoom);
            host._canvas.Invalidate();
            host._statusLabel.Text = string.Format("Navigated to {0}", typeModel.FullyQualifiedName);
        }
    }
}
