using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Rendering;

namespace HelixPythonNamespace.UI.Projection
{
    /// <summary>
    /// Data class for container-level nodes (solution, project, namespace rectangles).
    /// Stores the list of child names for tooltip and navigation.
    /// </summary>
    public sealed class ContainerNodeData
    {
        public ContainerNodeData(string headerName, string scopeKey, NavigationLevel nodeLevel, List<string> childNames)
        {
            HeaderName = headerName ?? "";
            ScopeKey = scopeKey ?? "";
            NodeLevel = nodeLevel;
            ChildNames = childNames ?? new List<string>();
        }

        /// <summary>Display name shown as the header.</summary>
        public string HeaderName { get; private set; }

        /// <summary>Scope key used for navigation (project name, namespace name, etc.).</summary>
        public string ScopeKey { get; private set; }

        /// <summary>The navigation level this node represents.</summary>
        public NavigationLevel NodeLevel { get; private set; }

        /// <summary>Child names listed inside the rectangle.</summary>
        public List<string> ChildNames { get; private set; }
    }

    /// <summary>
    /// Builds a visual scene from SolutionModel + LayoutModel.
    /// Replaces SceneBuilder for solution-native architecture.
    /// </summary>
    public sealed class SolutionProjectionBuilder
    {
        private readonly SceneGraph _scene;
        private readonly ILayoutMetadataStore _layoutStore;
        private readonly IAutoLayoutStrategy _autoLayout;
        private readonly EdgeRouter _edgeRouter;
        private readonly Dictionary<string, NodeVisual> _typeIdToVisual;
        private readonly HashSet<string> _visibleProjects;
        private CategoryFilterMode _categoryFilter;

        // Feature 3: sidecar layout cache for category tabs.
        // When UseSidecarLayout is true, positions are stored in _sidecarLayout
        // rather than the shared _layoutStore. The sidecar is recompacted whenever
        // the visible-type set changes.
        private bool _useSidecarLayout;
        private readonly Dictionary<string, NodeLayoutData> _sidecarLayout;
        private string _sidecarSetKey;

        // Feature 5: one-shot flag for Refresh Canvas — force auto-layout on the next rebuild.
        private bool _forceFreshLayout;

        public SolutionProjectionBuilder(
            SceneGraph scene,
            ILayoutMetadataStore layoutStore,
            IAutoLayoutStrategy autoLayout)
        {
            _scene = scene ?? throw new ArgumentNullException("scene");
            _layoutStore = layoutStore ?? throw new ArgumentNullException("layoutStore");
            _autoLayout = autoLayout ?? throw new ArgumentNullException("autoLayout");
            _edgeRouter = new EdgeRouter();
            _typeIdToVisual = new Dictionary<string, NodeVisual>();
            _visibleProjects = new HashSet<string>();
            _categoryFilter = CategoryFilterMode.All;
            _sidecarLayout = new Dictionary<string, NodeLayoutData>();
            _sidecarSetKey = null;
        }

        /// <summary>
        /// When true, this builder stores positions in an in-memory sidecar
        /// keyed by the visible-type set rather than the shared layout store.
        /// The sidecar is recompacted whenever the visible set changes,
        /// so category tabs compact on filter changes while still preserving
        /// the user's drags between tab switches (as long as the set is stable).
        /// </summary>
        public bool UseSidecarLayout
        {
            get { return _useSidecarLayout; }
            set { _useSidecarLayout = value; }
        }

        /// <summary>Fired after any change to the hidden-types set (Hide / Unhide / UnhideAll).</summary>
        public event Action HiddenTypesChanged;

        /// <summary>True when the type's layout entry marks it hidden.</summary>
        public bool IsTypeHidden(string fqn)
        {
            if (string.IsNullOrEmpty(fqn) || _layoutStore == null) return false;
            var data = _layoutStore.GetPosition(fqn);
            return data != null && data.IsHidden;
        }

        /// <summary>Mark the given FQNs hidden. Missing layout entries are created on demand.</summary>
        public void HideTypes(IEnumerable<string> fqns)
        {
            if (fqns == null) return;
            bool any = false;
            foreach (var fqn in fqns)
            {
                if (string.IsNullOrEmpty(fqn)) continue;
                var data = _layoutStore.GetPosition(fqn) ?? new NodeLayoutData();
                if (data.IsHidden) continue;
                data.IsHidden = true;
                _layoutStore.SetPosition(fqn, data);
                any = true;
            }
            if (any) RaiseHiddenTypesChanged();
        }

        /// <summary>Clear the hidden flag for the given FQNs.</summary>
        public void UnhideTypes(IEnumerable<string> fqns)
        {
            if (fqns == null) return;
            bool any = false;
            foreach (var fqn in fqns)
            {
                if (string.IsNullOrEmpty(fqn)) continue;
                var data = _layoutStore.GetPosition(fqn);
                if (data == null || !data.IsHidden) continue;
                data.IsHidden = false;
                _layoutStore.SetPosition(fqn, data);
                any = true;
            }
            if (any) RaiseHiddenTypesChanged();
        }

        /// <summary>Unhide every currently-hidden type in the layout store.</summary>
        public void UnhideAll()
        {
            if (_layoutStore == null) return;
            var hidden = GetHiddenFqns();
            if (hidden.Count == 0) return;
            UnhideTypes(hidden);
        }

        /// <summary>Return the list of FQNs currently marked hidden in the layout store.</summary>
        public IReadOnlyList<string> GetHiddenFqns()
        {
            var result = new List<string>();
            if (_layoutStore == null || _layoutStore.LayoutModel == null) return result;
            foreach (var kvp in _layoutStore.LayoutModel.NodeLayouts)
            {
                if (kvp.Value != null && kvp.Value.IsHidden) result.Add(kvp.Key);
            }
            return result;
        }

        private void RaiseHiddenTypesChanged()
        {
            Action handler = HiddenTypesChanged;
            if (handler != null) handler();
        }

        /// <summary>
        /// Clear the sidecar cache (e.g. when the solution changes).
        /// </summary>
        public void ResetSidecar()
        {
            _sidecarLayout.Clear();
            _sidecarSetKey = null;
        }

        /// <summary>
        /// One-shot flag: when true, the next RebuildFromSolution ignores persisted
        /// positions in _layoutStore and uses fresh auto-layout for every node.
        /// The positions are still written back to _layoutStore in-memory, but the
        /// on-disk .csooptool-layout.json is not touched (caller controls SaveCurrentPositions).
        /// The flag auto-resets at the end of RebuildFromSolution.
        /// </summary>
        public bool ForceFreshLayout
        {
            get { return _forceFreshLayout; }
            set { _forceFreshLayout = value; }
        }

        /// <summary>
        /// Snapshot all NodeVisual positions from the current scene into the sidecar,
        /// preserving drags made since the previous rebuild.
        /// </summary>
        private void CaptureVisualPositionsToSidecar()
        {
            foreach (var kvp in _typeIdToVisual)
            {
                NodeVisual v = kvp.Value;
                if (v == null) continue;
                RectangleF wb = v.WorldBounds;
                NodeLayoutData data;
                if (!_sidecarLayout.TryGetValue(kvp.Key, out data) || data == null)
                {
                    data = new NodeLayoutData();
                    _sidecarLayout[kvp.Key] = data;
                }
                data.PositionX = wb.X;
                data.PositionY = wb.Y;
                data.Width = wb.Width;
                data.Height = wb.Height;
            }
        }

        private static string BuildSetKey(List<TypeModel> types)
        {
            if (types == null || types.Count == 0) return string.Empty;
            var names = new List<string>(types.Count);
            for (int i = 0; i < types.Count; i++)
            {
                names.Add(types[i].FullyQualifiedName ?? "");
            }
            names.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(names[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Gets or sets the category filter mode for this builder.
        /// When not All, secondary types are rendered as circles and
        /// types with no relationship to primary types are excluded.
        /// </summary>
        public CategoryFilterMode CategoryFilter
        {
            get { return _categoryFilter; }
            set { _categoryFilter = value; }
        }

        /// <summary>
        /// Get the set of visible project names. Empty means all projects are visible.
        /// </summary>
        public IReadOnlyCollection<string> VisibleProjects
        {
            get { return _visibleProjects.ToList().AsReadOnly(); }
        }

        /// <summary>
        /// Set which projects should be visible. Pass null or empty to show all projects.
        /// </summary>
        public void SetVisibleProjects(IEnumerable<string> projectNames)
        {
            _visibleProjects.Clear();
            if (projectNames != null)
            {
                foreach (var name in projectNames)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        _visibleProjects.Add(name);
                    }
                }
            }
        }

        /// <summary>
        /// Toggle visibility of a specific project.
        /// </summary>
        public void ToggleProjectVisibility(string projectName)
        {
            if (string.IsNullOrEmpty(projectName))
            {
                return;
            }

            if (_visibleProjects.Contains(projectName))
            {
                _visibleProjects.Remove(projectName);
            }
            else
            {
                _visibleProjects.Add(projectName);
            }
        }

        /// <summary>
        /// Check if a project is currently visible.
        /// </summary>
        public bool IsProjectVisible(string projectName)
        {
            if (string.IsNullOrEmpty(projectName))
            {
                return false;
            }
            // Empty visible set means no projects checked → show nothing
            if (_visibleProjects.Count == 0)
            {
                return false;
            }
            return _visibleProjects.Contains(projectName);
        }

        /// <summary>
        /// Determines whether a type is a primary category for the current filter.
        /// </summary>
        public bool IsPrimaryType(TypeKind kind)
        {
            switch (_categoryFilter)
            {
                case CategoryFilterMode.InterfacesOnly:
                    return kind == TypeKind.Interface;
                case CategoryFilterMode.AbstractClassesOnly:
                    return kind == TypeKind.AbstractClass;
                case CategoryFilterMode.ConcreteClassesOnly:
                    return kind == TypeKind.ConcreteClass;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Resolves the target tab name for navigating to a given type kind.
        /// </summary>
        public static string GetTargetTabName(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface:
                    return "Interfaces";
                case TypeKind.AbstractClass:
                    return "Abstract Classes";
                default:
                    return "General Classes";
            }
        }

        /// <summary>
        /// Full rebuild from solution model.
        /// </summary>
        public void RebuildFromSolution(SolutionModel solution)
        {
            if (solution == null)
            {
                throw new ArgumentNullException("solution");
            }

            // Feature 3: if using the sidecar, preserve any drags made since
            // the last rebuild by snapshotting current visual positions before clearing.
            if (_useSidecarLayout)
            {
                CaptureVisualPositionsToSidecar();
            }

            _scene.Clear();
            _typeIdToVisual.Clear();

            // Add grid background
            GridVisual grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _scene.AddRenderable(grid);

            // Collect visible types
            var visibleTypes = new List<TypeModel>();
            foreach (var type in solution.GetAllTypes())
            {
                if (IsProjectVisible(type.Project != null ? type.Project.Name : null))
                {
                    visibleTypes.Add(type);
                }
            }

            // When filtering by category, split into primary and secondary types
            var primaryTypes = new List<TypeModel>();
            var secondaryTypes = new List<TypeModel>();

            if (_categoryFilter == CategoryFilterMode.All)
            {
                primaryTypes.AddRange(visibleTypes);
            }
            else
            {
                foreach (var type in visibleTypes)
                {
                    if (IsPrimaryType(type.TypeKind))
                    {
                        primaryTypes.Add(type);
                    }
                    else
                    {
                        secondaryTypes.Add(type);
                    }
                }
            }

            // Build a set of primary FQNs for relationship checking
            var primaryFqns = new HashSet<string>();
            foreach (var type in primaryTypes)
            {
                primaryFqns.Add(type.FullyQualifiedName);
            }

            // Determine which secondary types are needed for relationship context
            var neededSecondary = new HashSet<string>();
            if (_categoryFilter != CategoryFilterMode.All)
            {
                // A secondary type is needed if it has a relationship to/from any primary type
                foreach (var type in primaryTypes)
                {
                    foreach (var baseType in type.BaseTypes)
                    {
                        if (!baseType.IsExternal && !primaryFqns.Contains(baseType.EffectiveFqn))
                        {
                            neededSecondary.Add(baseType.EffectiveFqn);
                        }
                    }
                }
                foreach (var type in secondaryTypes)
                {
                    foreach (var baseType in type.BaseTypes)
                    {
                        if (!baseType.IsExternal && primaryFqns.Contains(baseType.EffectiveFqn))
                        {
                            neededSecondary.Add(type.FullyQualifiedName);
                        }
                    }
                }
            }

            // Collect all types that will be displayed (primary + needed secondary)
            var allTypesForLayout = new List<TypeModel>();
            var addedForLayout = new HashSet<string>();
            foreach (var type in primaryTypes)
            {
                if (!addedForLayout.Contains(type.FullyQualifiedName))
                {
                    allTypesForLayout.Add(type);
                    addedForLayout.Add(type.FullyQualifiedName);
                }
            }
            foreach (var type in secondaryTypes)
            {
                if (neededSecondary.Contains(type.FullyQualifiedName)
                    && !addedForLayout.Contains(type.FullyQualifiedName))
                {
                    allTypesForLayout.Add(type);
                    addedForLayout.Add(type.FullyQualifiedName);
                }
            }

            // Feature 3: if using sidecar mode and the visible-type set has changed
            // since the last rebuild, drop the sidecar so we recompact fresh.
            if (_useSidecarLayout)
            {
                string newKey = BuildSetKey(allTypesForLayout);
                if (!string.Equals(newKey, _sidecarSetKey, StringComparison.Ordinal))
                {
                    _sidecarLayout.Clear();
                    _sidecarSetKey = newKey;
                }
            }

            // Compute batch layout for all types (banded by category)
            int totalCount = allTypesForLayout.Count;
            _autoLayout.SetExpectedCount(totalCount);
            var batchLayout = _autoLayout.ComputeAllPositions(allTypesForLayout);

            // Create NodeVisual for each primary type (skip duplicate FQNs + hidden types)
            foreach (var type in primaryTypes)
            {
                if (_typeIdToVisual.ContainsKey(type.FullyQualifiedName))
                {
                    continue;
                }
                if (IsTypeHidden(type.FullyQualifiedName))
                {
                    continue;
                }

                var nodeVisual = CreateNodeVisualWithBatch(type, batchLayout);
                nodeVisual.IsCircleMode = false;
                _scene.AddNode(nodeVisual);
                _typeIdToVisual[type.FullyQualifiedName] = nodeVisual;
            }

            // Create circle NodeVisual for each needed secondary type
            foreach (var type in secondaryTypes)
            {
                if (!neededSecondary.Contains(type.FullyQualifiedName))
                {
                    continue;
                }
                if (_typeIdToVisual.ContainsKey(type.FullyQualifiedName))
                {
                    continue;
                }
                if (IsTypeHidden(type.FullyQualifiedName))
                {
                    continue;
                }

                var nodeVisual = CreateNodeVisualWithBatch(type, batchLayout);
                nodeVisual.IsCircleMode = true;
                _scene.AddNode(nodeVisual);
                _typeIdToVisual[type.FullyQualifiedName] = nodeVisual;
            }

            // A persisted position (from when a type was first added) can collide exactly with a
            // freshly batch-placed node — e.g. a concrete type stored at the origin (50,50) lands on
            // an interface's band-0 slot — leaving one node completely hidden behind the other. Nudge
            // any exact-overlap to a free slot so every type stays visible.
            ResolveExactNodeOverlaps();

            // Create EdgeVisual for each relationship (derived from base types)
            // Track seen edges to skip duplicates from corrupted source files
            var seenEdges = new HashSet<string>();
            var allDisplayedTypes = new List<TypeModel>();
            allDisplayedTypes.AddRange(primaryTypes);
            foreach (var type in secondaryTypes)
            {
                if (neededSecondary.Contains(type.FullyQualifiedName))
                {
                    allDisplayedTypes.Add(type);
                }
            }

            foreach (var type in allDisplayedTypes)
            {
                foreach (var baseType in type.BaseTypes)
                {
                    // A base may be written by short name (e.g. `class Circle(Shape)`) but the visual
                    // map is keyed by the resolved FQN (`pkg.mod.Shape`). Use the resolved type's FQN
                    // so inheritance/implementation edges are actually created.
                    string targetFqn = baseType.EffectiveFqn;
                    // Only create edges to types within the displayed set
                    if (!baseType.IsExternal && targetFqn != null && _typeIdToVisual.ContainsKey(targetFqn))
                    {
                        string edgeKey = type.FullyQualifiedName + "|" + targetFqn;
                        if (seenEdges.Contains(edgeKey))
                        {
                            continue;
                        }
                        seenEdges.Add(edgeKey);

                        var edgeVisual = CreateEdgeVisual(type, baseType);
                        if (edgeVisual != null)
                        {
                            _scene.AddEdge(edgeVisual);
                        }
                    }
                }
            }

            // Spatial nesting: expand parent nodes to enclose their nested children
            // and boost child Z-order so clicks hit the inner node first.
            ApplyNestedTypeLayout(allDisplayedTypes);

            // Route all edges
            _edgeRouter.RouteAllEdges(_scene);

            // One-shot flag: consumed.
            _forceFreshLayout = false;
        }

        /// <summary>
        /// Post-rebuild pass: for each parent type that has nested children visible,
        /// ensure the parent's bounds enclose all children (with padding) and that
        /// children render on top. If no child has a stored position yet, seed them
        /// in a row below the parent header.
        /// </summary>
        private void ApplyNestedTypeLayout(List<TypeModel> displayedTypes)
        {
            // Sort by nesting depth ascending so parents are sized before grandparents.
            displayedTypes.Sort((a, b) => NestingDepth(a).CompareTo(NestingDepth(b)));

            const float childRowX = 16f;
            const float childRowY = 48f;
            const float childGap = 12f;
            const float padding = 16f;

            foreach (var parent in displayedTypes)
            {
                NodeVisual parentVisual;
                if (!_typeIdToVisual.TryGetValue(parent.FullyQualifiedName, out parentVisual)) continue;

                // Collect direct-child visuals (one level; grandchildren are handled by depth).
                List<NodeVisual> children = new List<NodeVisual>();
                List<TypeModel> childTypes = new List<TypeModel>();
                foreach (var t in displayedTypes)
                {
                    if (string.Equals(t.ParentTypeFqn, parent.FullyQualifiedName, StringComparison.Ordinal))
                    {
                        NodeVisual cv;
                        if (_typeIdToVisual.TryGetValue(t.FullyQualifiedName, out cv))
                        {
                            children.Add(cv);
                            childTypes.Add(t);
                        }
                    }
                }
                if (children.Count == 0) continue;

                // Detect "fresh" placement: if every child sits inside (or within a small margin of)
                // the parent, assume user-curated layout and only expand the parent if needed.
                bool anyChildOutsideParent = false;
                RectangleF parentBounds = parentVisual.WorldBounds;
                for (int i = 0; i < children.Count; i++)
                {
                    if (!parentBounds.Contains(children[i].WorldBounds))
                    {
                        anyChildOutsideParent = true;
                        break;
                    }
                }

                if (anyChildOutsideParent)
                {
                    // Seed a simple row layout below the header for this parent.
                    float cursorX = parentBounds.X + childRowX;
                    float cursorY = parentBounds.Y + childRowY;
                    float rowBottom = cursorY;
                    float maxRowRight = parentBounds.Right - childRowX;
                    for (int i = 0; i < children.Count; i++)
                    {
                        var c = children[i];
                        float w = c.WorldBounds.Width;
                        float h = c.WorldBounds.Height;
                        if (cursorX + w > maxRowRight && cursorX > parentBounds.X + childRowX)
                        {
                            // Wrap to next row
                            cursorX = parentBounds.X + childRowX;
                            cursorY = rowBottom + childGap;
                        }
                        c.SetPosition(cursorX, cursorY);
                        if (_useSidecarLayout)
                        {
                            UpdateNodePositionSidecarInternal(childTypes[i].FullyQualifiedName, cursorX, cursorY);
                        }
                        else
                        {
                            UpdateNodePosition(childTypes[i].FullyQualifiedName, cursorX, cursorY);
                        }
                        cursorX += w + childGap;
                        if (cursorY + h + childGap > rowBottom) rowBottom = cursorY + h;
                    }
                }

                // Expand parent bounds to enclose every child + padding.
                RectangleF union = parentVisual.WorldBounds;
                for (int i = 0; i < children.Count; i++)
                {
                    union = RectangleF.Union(union, children[i].WorldBounds);
                }
                union.Inflate(padding, padding);
                if (union.Width > parentVisual.WorldBounds.Width || union.Height > parentVisual.WorldBounds.Height)
                {
                    // Keep top-left but extend right/bottom so children fit without jumping.
                    RectangleF newBounds = new RectangleF(
                        parentVisual.WorldBounds.X,
                        parentVisual.WorldBounds.Y,
                        Math.Max(parentVisual.WorldBounds.Width, union.Right - parentVisual.WorldBounds.X),
                        Math.Max(parentVisual.WorldBounds.Height, union.Bottom - parentVisual.WorldBounds.Y));
                    parentVisual.SetBounds(newBounds);
                    UpdateNodeBoundsPersistent(parent.FullyQualifiedName, newBounds);
                }

                // Boost Z-order so children render above the parent body.
                int depth = NestingDepth(parent) + 1;
                foreach (var c in children) c.ZOrder = ZOrders.Node + depth;
            }
        }

        private static int NestingDepth(TypeModel t)
        {
            if (t == null) return 0;
            string fqn = t.FullyQualifiedName ?? string.Empty;
            int depth = 0;
            for (int i = 0; i < fqn.Length; i++) if (fqn[i] == '+') depth++;
            return depth;
        }

        /// <summary>
        /// Write the child's new position into whichever layout store is active (sidecar or main).
        /// </summary>
        private void UpdateNodePositionSidecarInternal(string fqn, float x, float y)
        {
            NodeLayoutData data;
            if (!_sidecarLayout.TryGetValue(fqn, out data) || data == null)
            {
                data = new NodeLayoutData();
                _sidecarLayout[fqn] = data;
            }
            data.PositionX = x;
            data.PositionY = y;
        }

        /// <summary>
        /// Persist updated bounds (X/Y and Width/Height) for a node into the layout store.
        /// Used by the nesting pass to grow parent containers.
        /// </summary>
        private void UpdateNodeBoundsPersistent(string fqn, RectangleF bounds)
        {
            if (_useSidecarLayout)
            {
                NodeLayoutData data;
                if (!_sidecarLayout.TryGetValue(fqn, out data) || data == null)
                {
                    data = new NodeLayoutData();
                    _sidecarLayout[fqn] = data;
                }
                data.PositionX = bounds.X;
                data.PositionY = bounds.Y;
                data.Width = bounds.Width;
                data.Height = bounds.Height;
            }
            else
            {
                UpdateNodeBounds(fqn, bounds);
            }
        }

        /// <summary>
        /// Add a new type to the scene.
        /// </summary>
        public NodeVisual AddType(TypeModel type)
        {
            if (type == null)
            {
                return null;
            }

            var nodeVisual = CreateNodeVisual(type);
            _scene.AddNode(nodeVisual);
            _typeIdToVisual[type.FullyQualifiedName] = nodeVisual;

            // Re-route edges
            _edgeRouter.RouteAllEdges(_scene);

            return nodeVisual;
        }

        /// <summary>
        /// Remove a type from the scene.
        /// </summary>
        public void RemoveType(string fullyQualifiedName)
        {
            NodeVisual visual;
            if (_typeIdToVisual.TryGetValue(fullyQualifiedName, out visual))
            {
                _scene.RemoveNode(visual);
                _typeIdToVisual.Remove(fullyQualifiedName);

                // Remove edges connected to this type
                var edgesToRemove = new List<EdgeVisual>();
                foreach (var edge in _scene.Edges)
                {
                    if (edge.SourceNodeId == visual.NodeId || edge.TargetNodeId == visual.NodeId)
                    {
                        edgesToRemove.Add(edge);
                    }
                }

                foreach (var edge in edgesToRemove)
                {
                    _scene.RemoveEdge(edge);
                }
            }
        }

        /// <summary>
        /// Update a type's visual representation.
        /// </summary>
        public void UpdateType(TypeModel type)
        {
            if (type == null)
            {
                return;
            }

            NodeVisual visual;
            if (_typeIdToVisual.TryGetValue(type.FullyQualifiedName, out visual))
            {
                // Update style in case TypeKind changed (abstract/concrete/interface)
                var newStyle = NodeStyleFactory.GetStyle(type.TypeKind);
                visual.SetStyle(newStyle);

                var typeKindLabel = FormatTypeHeaderLabel(type);
                var genericParams = FormatGenericParameters(type.GenericParameters);
                var memberLines = FormatMemberLines(type);

                visual.UpdateFromModel(type.Name, typeKindLabel, genericParams, memberLines);
            }
        }

        /// <summary>
        /// Handle type rename by updating visual and layout.
        /// </summary>
        public void RenameType(string oldFQN, string newFQN, TypeModel type)
        {
            NodeVisual visual;
            if (_typeIdToVisual.TryGetValue(oldFQN, out visual))
            {
                _typeIdToVisual.Remove(oldFQN);
                _typeIdToVisual[newFQN] = visual;

                // Update layout store
                _layoutStore.OnTypeRenamed(oldFQN, newFQN);

                // Update visual content
                UpdateType(type);
            }
        }

        /// <summary>
        /// Update node position in layout store.
        /// </summary>
        public void UpdateNodePosition(string fullyQualifiedName, float x, float y)
        {
            var layout = _layoutStore.GetPosition(fullyQualifiedName);
            if (layout == null)
            {
                layout = new NodeLayoutData();
            }

            layout.PositionX = x;
            layout.PositionY = y;
            _layoutStore.SetPosition(fullyQualifiedName, layout);
        }

        /// <summary>
        /// Update node bounds in layout store.
        /// </summary>
        public void UpdateNodeBounds(string fullyQualifiedName, RectangleF bounds)
        {
            var layout = _layoutStore.GetPosition(fullyQualifiedName);
            if (layout == null)
            {
                layout = new NodeLayoutData();
            }

            layout.PositionX = bounds.X;
            layout.PositionY = bounds.Y;
            layout.Width = bounds.Width;
            layout.Height = bounds.Height;
            _layoutStore.SetPosition(fullyQualifiedName, layout);
        }

        /// <summary>
        /// Find the NodeVisual for a type.
        /// </summary>
        public NodeVisual FindVisual(string fullyQualifiedName)
        {
            NodeVisual visual;
            _typeIdToVisual.TryGetValue(fullyQualifiedName, out visual);
            return visual;
        }

        /// <summary>
        /// Get the FQN for a NodeVisual.
        /// </summary>
        /// <summary>
        /// Ensures no two type nodes occupy the exact same position. Walks the scene's nodes in
        /// order; if a node's top-left coincides with an already-placed node, it is shifted right by
        /// one grid column (repeatedly) until the slot is free, and the new position is persisted so
        /// the de-collision sticks. Guards against the stale-persisted-vs-fresh-batch collision.
        /// </summary>
        private void ResolveExactNodeOverlaps()
        {
            var occupied = new HashSet<string>();
            foreach (NodeVisual node in _scene.Nodes)
            {
                if (node == null) continue;
                float x = node.WorldBounds.X;
                float y = node.WorldBounds.Y;
                string key = OverlapKey(x, y);
                int guard = 0;
                while (occupied.Contains(key) && guard++ < 1000)
                {
                    x += LayoutConstants.XSpacing;
                    key = OverlapKey(x, y);
                }
                occupied.Add(key);
                if (x != node.WorldBounds.X || y != node.WorldBounds.Y)
                {
                    node.SetPosition(x, y);
                    string fqn = FindFQN(node);
                    if (!string.IsNullOrEmpty(fqn) && !_useSidecarLayout)
                    {
                        NodeLayoutData ld = _layoutStore.GetPosition(fqn) ?? new NodeLayoutData();
                        ld.PositionX = x;
                        ld.PositionY = y;
                        ld.Width = node.WorldBounds.Width;
                        ld.Height = node.WorldBounds.Height;
                        _layoutStore.SetPosition(fqn, ld);
                    }
                }
            }
        }

        private static string OverlapKey(float x, float y)
        {
            // Snap to a coarse grid so near-coincident floats count as the same slot.
            return ((int)System.Math.Round(x / 10f)) + "," + ((int)System.Math.Round(y / 10f));
        }

        public string FindFQN(NodeVisual visual)
        {
            foreach (var kvp in _typeIdToVisual)
            {
                if (kvp.Value == visual)
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        /// <summary>
        /// Create a NodeVisual using batch-computed layout positions.
        /// Persisted positions take precedence over auto-layout.
        /// </summary>
        private NodeVisual CreateNodeVisualWithBatch(TypeModel type, Dictionary<string, NodeLayoutData> batchLayout)
        {
            var fqn = type.FullyQualifiedName;

            NodeLayoutData layout;
            if (_useSidecarLayout)
            {
                // Sidecar path: category tab with its own in-memory layout cache.
                // Do NOT touch _layoutStore so the 'All' tab's persisted layout is unaffected.
                if (!_sidecarLayout.TryGetValue(fqn, out layout))
                {
                    NodeLayoutData batchPosition;
                    if (batchLayout != null && batchLayout.TryGetValue(fqn, out batchPosition))
                    {
                        layout = batchPosition;
                    }
                    else
                    {
                        layout = _autoLayout.ComputePosition(type, _scene.Nodes);
                    }
                    _sidecarLayout[fqn] = layout;
                }
            }
            else
            {
                // ForceFreshLayout: skip persisted positions and use batch auto-layout directly.
                // Still writes back to _layoutStore so subsequent operations see the fresh layout,
                // but the on-disk layout file is not touched until the caller chooses to save.
                layout = _forceFreshLayout ? null : _layoutStore.GetPosition(fqn);
                if (layout == null)
                {
                    NodeLayoutData batchPosition;
                    if (batchLayout != null && batchLayout.TryGetValue(fqn, out batchPosition))
                    {
                        layout = batchPosition;
                    }
                    else
                    {
                        // Final fallback: single-node auto-layout
                        layout = _autoLayout.ComputePosition(type, _scene.Nodes);
                    }
                    _layoutStore.SetPosition(fqn, layout);
                }
            }

            var bounds = new RectangleF(
                layout.PositionX,
                layout.PositionY,
                layout.Width,
                layout.Height);

            var style = NodeStyleFactory.GetStyle(type.TypeKind);
            var nodeVisual = new NodeVisual(fqn, bounds, style);

            var typeKindLabel = FormatTypeHeaderLabel(type);
            var genericParams = FormatGenericParameters(type.GenericParameters);
            var memberLines = FormatMemberLines(type);

            nodeVisual.UpdateFromModel(type.Name, typeKindLabel, genericParams, memberLines);

            return nodeVisual;
        }

        /// <summary>
        /// Create a NodeVisual for a single type addition (uses single-node auto-layout fallback).
        /// </summary>
        private NodeVisual CreateNodeVisual(TypeModel type)
        {
            var fqn = type.FullyQualifiedName;

            // Get layout from store or auto-generate
            var layout = _layoutStore.GetPosition(fqn);
            if (layout == null)
            {
                layout = _autoLayout.ComputePosition(type, _scene.Nodes);
                _layoutStore.SetPosition(fqn, layout);
            }

            var bounds = new RectangleF(
                layout.PositionX,
                layout.PositionY,
                layout.Width,
                layout.Height);

            var style = NodeStyleFactory.GetStyle(type.TypeKind);
            var nodeVisual = new NodeVisual(fqn, bounds, style);

            var typeKindLabel = FormatTypeHeaderLabel(type);
            var genericParams = FormatGenericParameters(type.GenericParameters);
            var memberLines = FormatMemberLines(type);

            nodeVisual.UpdateFromModel(type.Name, typeKindLabel, genericParams, memberLines);

            return nodeVisual;
        }

        private EdgeVisual CreateEdgeVisual(TypeModel sourceType, TypeReference baseTypeRef)
        {
            NodeVisual sourceVisual;
            NodeVisual targetVisual;

            if (!_typeIdToVisual.TryGetValue(sourceType.FullyQualifiedName, out sourceVisual))
            {
                return null;
            }

            // The base reference may carry a short/unqualified name that resolves to a fully-qualified
            // type; the visual map is keyed by the resolved FQN, so prefer that for the target lookup.
            string targetFqn = baseTypeRef.EffectiveFqn;
            if (!_typeIdToVisual.TryGetValue(targetFqn, out targetVisual))
            {
                return null;
            }

            var edgeId = sourceType.FullyQualifiedName + "->" + targetFqn;
            var style = EdgeStyleFactory.GetStyle(baseTypeRef.RelationshipKind);

            return new EdgeVisual(edgeId, sourceVisual.NodeId, targetVisual.NodeId, style);
        }

        private static string FormatTypeKindLabel(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface:
                    return "<<interface>>";
                case TypeKind.AbstractClass:
                    return "<<abstract>>";
                default:
                    return "";
            }
        }

        /// <summary>
        /// Full header label including nested-in suffix when the type is nested inside
        /// another type. E.g. "&lt;&lt;abstract&gt;&gt; (nested in Outer)".
        /// </summary>
        private string FormatTypeHeaderLabel(TypeModel type)
        {
            if (type == null) return string.Empty;
            string kindLabel = FormatTypeKindLabel(type.TypeKind);
            if (!type.IsNested) return kindLabel;

            string parentName = GetParentShortName(type.ParentTypeFqn);
            string nestedSuffix = string.IsNullOrEmpty(parentName)
                ? "(nested)"
                : "(nested in " + parentName + ")";
            return string.IsNullOrEmpty(kindLabel)
                ? nestedSuffix
                : kindLabel + " " + nestedSuffix;
        }

        /// <summary>
        /// Given a ParentTypeFqn like "Acme.Foo" or "Acme.Foo+Bar", return the last
        /// type-name segment so the label reads cleanly (just "Foo" or "Bar").
        /// </summary>
        private static string GetParentShortName(string parentFqn)
        {
            if (string.IsNullOrEmpty(parentFqn)) return string.Empty;
            int plus = parentFqn.LastIndexOf('+');
            if (plus >= 0) return parentFqn.Substring(plus + 1);
            int dot = parentFqn.LastIndexOf('.');
            return dot >= 0 ? parentFqn.Substring(dot + 1) : parentFqn;
        }

        private static string FormatGenericParameters(List<GenericParameterModel> genericParams)
        {
            if (genericParams == null || genericParams.Count == 0)
            {
                return "";
            }

            var sb = new StringBuilder();
            for (int i = 0; i < genericParams.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(genericParams[i].Name);
            }
            return sb.ToString();
        }

        private static List<string> FormatMemberLines(TypeModel type)
        {
            var lines = new List<string>();

            if (type.Members == null)
            {
                return lines;
            }

            for (int i = 0; i < type.Members.Count; i++)
            {
                var member = type.Members[i];
                var accessPrefix = FormatAccessModifier(member.AccessModifier);
                string staticMarker = member.IsStatic ? "[static] " : string.Empty;
                string asyncMarker = member.IsAsync ? "async " : string.Empty;

                if (member.MemberKind == MemberKind.Method
                    || member.MemberKind == MemberKind.Constructor)
                {
                    var parameters = FormatMethodParameters(member.Parameters);
                    // Python has no "void". An unannotated def has no return type to show, so the
                    // UML return-suffix is omitted rather than invented — "handle_starttag(tag, attr)"
                    // reads as Python; "handle_starttag(...) : void" reads as a bug. Only a real
                    // "-> T" annotation produces a suffix.
                    string returnSuffix = string.IsNullOrEmpty(member.ReturnType)
                        ? string.Empty
                        : " : " + member.ReturnType;
                    var line = string.Format("{0} {1}{2}{3}({4}){5}",
                        accessPrefix, staticMarker, asyncMarker, member.Name, parameters, returnSuffix);
                    lines.Add(line);
                }
                else if (member.MemberKind == MemberKind.Property)
                {
                    // Only a real annotation gets a " : T" suffix. An unannotated Python property
                    // shows just its name, matching the methods — "object" was an invented C#
                    // placeholder that read as a real type the source never wrote.
                    var accessors = FormatPropertyAccessors(member.HasGetter, member.HasSetter);
                    string typeSuffix = string.IsNullOrEmpty(member.ReturnType)
                        ? string.Empty
                        : " : " + member.ReturnType;
                    string accessorSuffix = string.IsNullOrEmpty(accessors) ? string.Empty : " " + accessors;
                    var line = string.Format("{0} {1}{2}{3}{4}",
                        accessPrefix, staticMarker, member.Name, typeSuffix, accessorSuffix);
                    lines.Add(line);
                }
                else if (member.MemberKind == MemberKind.Field)
                {
                    string constMarker = member.IsConst ? "const " : (member.IsReadOnly ? "readonly " : string.Empty);
                    // Same rule as properties: no invented "object" type for an unannotated attribute.
                    string typeSuffix = string.IsNullOrEmpty(member.ReturnType)
                        ? string.Empty
                        : " : " + member.ReturnType;
                    var line = string.Format("{0} {1}{2}{3}{4}",
                        accessPrefix, staticMarker, constMarker, member.Name, typeSuffix);
                    lines.Add(line);
                }
                else if (member.MemberKind == MemberKind.Event)
                {
                    var handler = member.ReturnType ?? "EventHandler";
                    var line = string.Format("{0} {1}event {2} : {3}",
                        accessPrefix, staticMarker, member.Name, handler);
                    lines.Add(line);
                }
                else if (member.MemberKind == MemberKind.Indexer)
                {
                    var parameters = FormatMethodParameters(member.Parameters);
                    var returnType = member.ReturnType ?? "object";
                    var accessors = FormatPropertyAccessors(member.HasGetter, member.HasSetter);
                    var line = string.Format("{0} this[{1}] : {2} {3}",
                        accessPrefix, parameters, returnType, accessors);
                    lines.Add(line);
                }
                else if (member.MemberKind == MemberKind.Operator)
                {
                    var parameters = FormatMethodParameters(member.Parameters);
                    var returnType = member.ReturnType ?? "object";
                    var line = string.Format("{0} {1}({2}) : {3}",
                        accessPrefix, member.Name, parameters, returnType);
                    lines.Add(line);
                }
            }

            return lines;
        }

        private static string FormatAccessModifier(AccessModifier modifier)
        {
            switch (modifier)
            {
                case AccessModifier.Public:
                    return "+";
                case AccessModifier.Protected:
                    return "#";
                case AccessModifier.Internal:
                    return "~";
                case AccessModifier.Private:
                    return "-";
                default:
                    return "+";
            }
        }

        private static string FormatMethodParameters(List<MethodParameterModel> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return "";
            }

            var sb = new StringBuilder();
            for (int i = 0; i < parameters.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                MethodParameterModel p = parameters[i];
                string modifier = string.Empty;
                if (p.IsOut) modifier = "out ";
                else if (p.IsRef) modifier = "ref ";
                else if (p.IsParams) modifier = "params ";

                // A Python parameter is only annotated when the source wrote "name: T". Without an
                // annotation TypeName is empty, so emit just the name — "tag", not "tag: " with a
                // dangling colon and nothing after it.
                sb.Append(string.IsNullOrEmpty(p.TypeName)
                    ? modifier + p.Name
                    : string.Format("{0}{1}: {2}", modifier, p.Name, p.TypeName));

                if (p.HasDefaultValue && !string.IsNullOrEmpty(p.DefaultValue))
                {
                    sb.Append(" = ");
                    sb.Append(p.DefaultValue);
                }
            }
            return sb.ToString();
        }

        private static string FormatPropertyAccessors(bool hasGetter, bool hasSetter)
        {
            if (hasGetter && hasSetter)
            {
                return "{ get; set; }";
            }
            if (hasGetter)
            {
                return "{ get; }";
            }
            if (hasSetter)
            {
                return "{ set; }";
            }
            return "";
        }

        // ================================================================
        // Hierarchical scene building methods
        // ================================================================

        /// <summary>
        /// Build solution-level scene: one rectangle showing solution name and project list.
        /// </summary>
        public void BuildSolutionLevel(SolutionModel solution)
        {
            if (solution == null)
            {
                throw new ArgumentNullException("solution");
            }

            _scene.Clear();
            _typeIdToVisual.Clear();

            // Add grid
            var grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _scene.AddRenderable(grid);

            // Collect project names
            var projectNames = new List<string>();
            for (int i = 0; i < solution.Projects.Count; i++)
            {
                projectNames.Add(solution.Projects[i].Name);
            }

            // Create single solution node
            string nodeId = "[SOL]" + solution.Name;
            var layout = _layoutStore.GetPosition(nodeId);
            float x = 50f;
            float y = 50f;
            if (layout != null)
            {
                x = layout.PositionX;
                y = layout.PositionY;
            }

            var bounds = new RectangleF(x, y, LayoutConstants.NodeWidth, LayoutConstants.NodeHeight);
            var style = NodeStyleFactory.GetStyle(TypeKind.ConcreteClass);
            var nodeVisual = new NodeVisual(nodeId, bounds, style);

            // Use member lines for project names
            nodeVisual.UpdateFromModel(solution.Name, "<<solution>>", "", projectNames);
            nodeVisual.ContainerData = new ContainerNodeData(
                solution.Name, solution.Name, NavigationLevel.Solution, projectNames);

            _scene.AddNode(nodeVisual);

            // Save layout if not already saved
            if (layout == null)
            {
                var newLayout = new NodeLayoutData();
                newLayout.PositionX = x;
                newLayout.PositionY = y;
                _layoutStore.SetPosition(nodeId, newLayout);
            }
        }

        /// <summary>
        /// Build project-level scene: one rectangle per project, each showing namespace list.
        /// </summary>
        public void BuildProjectLevel(SolutionModel solution)
        {
            if (solution == null)
            {
                throw new ArgumentNullException("solution");
            }

            _scene.Clear();
            _typeIdToVisual.Clear();

            // Add grid
            var grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _scene.AddRenderable(grid);

            int columns = LayoutConstants.CalculateColumnsForSquareGrid(solution.Projects.Count);
            if (columns < 1) columns = 1;

            for (int i = 0; i < solution.Projects.Count; i++)
            {
                ProjectModel project = solution.Projects[i];
                string nodeId = "[PROJ]" + project.Name;

                var layout = _layoutStore.GetPosition(nodeId);
                float x, y;
                if (layout != null)
                {
                    x = layout.PositionX;
                    y = layout.PositionY;
                }
                else
                {
                    int col = i % columns;
                    int row = i / columns;
                    x = LayoutConstants.StartX + col * LayoutConstants.XSpacing;
                    y = LayoutConstants.StartY + row * LayoutConstants.YSpacing;
                }

                var bounds = new RectangleF(x, y, LayoutConstants.NodeWidth, LayoutConstants.NodeHeight);
                var style = NodeStyleFactory.GetStyle(TypeKind.ConcreteClass);
                var nodeVisual = new NodeVisual(nodeId, bounds, style);

                // Namespace names as member lines
                var namespaces = project.GetNamespaces();
                nodeVisual.UpdateFromModel(project.Name, "<<project>>", "", namespaces);
                nodeVisual.ContainerData = new ContainerNodeData(
                    project.Name, project.Name, NavigationLevel.Project, namespaces);

                _scene.AddNode(nodeVisual);

                // Save layout
                if (layout == null)
                {
                    var newLayout = new NodeLayoutData();
                    newLayout.PositionX = x;
                    newLayout.PositionY = y;
                    _layoutStore.SetPosition(nodeId, newLayout);
                }
            }
        }

        /// <summary>
        /// Build namespace-level scene: one rectangle per namespace in the specified project.
        /// </summary>
        public void BuildNamespaceLevel(SolutionModel solution, string projectName)
        {
            if (solution == null)
            {
                throw new ArgumentNullException("solution");
            }

            _scene.Clear();
            _typeIdToVisual.Clear();

            // Add grid
            var grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _scene.AddRenderable(grid);

            // Find the project
            ProjectModel project = null;
            for (int i = 0; i < solution.Projects.Count; i++)
            {
                if (string.Equals(solution.Projects[i].Name, projectName, StringComparison.OrdinalIgnoreCase))
                {
                    project = solution.Projects[i];
                    break;
                }
            }

            if (project == null)
            {
                return;
            }

            var namespaces = project.GetNamespaces();
            int columns = LayoutConstants.CalculateColumnsForSquareGrid(namespaces.Count);
            if (columns < 1) columns = 1;

            for (int i = 0; i < namespaces.Count; i++)
            {
                string ns = namespaces[i];
                string nodeId = string.Format("[NS]{0}::{1}", projectName, ns);

                var layout = _layoutStore.GetPosition(nodeId);
                float x, y;
                if (layout != null)
                {
                    x = layout.PositionX;
                    y = layout.PositionY;
                }
                else
                {
                    int col = i % columns;
                    int row = i / columns;
                    x = LayoutConstants.StartX + col * LayoutConstants.XSpacing;
                    y = LayoutConstants.StartY + row * LayoutConstants.YSpacing;
                }

                var bounds = new RectangleF(x, y, LayoutConstants.NodeWidth, LayoutConstants.NodeHeight);
                var style = NodeStyleFactory.GetStyle(TypeKind.Interface);
                var nodeVisual = new NodeVisual(nodeId, bounds, style);

                // Type names as member lines
                var types = project.GetTypesByNamespace(ns);
                var typeNames = new List<string>();
                for (int t = 0; t < types.Count; t++)
                {
                    typeNames.Add(types[t].Name);
                }

                nodeVisual.UpdateFromModel(ns, "<<namespace>>", "", typeNames);
                nodeVisual.ContainerData = new ContainerNodeData(
                    ns, ns, NavigationLevel.Namespace, typeNames);

                _scene.AddNode(nodeVisual);

                // Save layout
                if (layout == null)
                {
                    var newLayout = new NodeLayoutData();
                    newLayout.PositionX = x;
                    newLayout.PositionY = y;
                    _layoutStore.SetPosition(nodeId, newLayout);
                }
            }
        }

        /// <summary>
        /// Build type-level scene for a specific namespace within a project.
        /// Filters to only show types in the given namespace.
        /// </summary>
        public void BuildTypeLevelForNamespace(SolutionModel solution, string projectName, string namespaceName)
        {
            if (solution == null)
            {
                throw new ArgumentNullException("solution");
            }

            // Find the project
            ProjectModel project = null;
            for (int i = 0; i < solution.Projects.Count; i++)
            {
                if (string.Equals(solution.Projects[i].Name, projectName, StringComparison.OrdinalIgnoreCase))
                {
                    project = solution.Projects[i];
                    break;
                }
            }

            if (project == null)
            {
                return;
            }

            // Get types in this namespace
            string actualNs = namespaceName;
            if (actualNs == "(global)")
            {
                actualNs = "";
            }
            var types = project.GetTypesByNamespace(actualNs);

            // Create a temporary SolutionModel-like view with only these types visible
            // We reuse the existing RebuildFromSolution by setting visible projects
            // and filtering. But for namespace-scoped view, we do a direct build.

            _scene.Clear();
            _typeIdToVisual.Clear();

            // Add grid
            var grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _scene.AddRenderable(grid);

            // Compute batch layout
            _autoLayout.SetExpectedCount(types.Count);
            var batchLayout = _autoLayout.ComputeAllPositions(types);

            // Create NodeVisual for each type
            for (int i = 0; i < types.Count; i++)
            {
                TypeModel type = types[i];
                if (_typeIdToVisual.ContainsKey(type.FullyQualifiedName))
                {
                    continue;
                }

                var nodeVisual = CreateNodeVisualWithBatch(type, batchLayout);
                // Namespace-local types stay visible under every category filter (unlike the
                // solution-wide RebuildFromSolution view, nothing here gets dropped) — a non-
                // matching kind just renders as a circle instead of a full node.
                nodeVisual.IsCircleMode = !IsPrimaryType(type.TypeKind);
                _scene.AddNode(nodeVisual);
                _typeIdToVisual[type.FullyQualifiedName] = nodeVisual;
            }

            // Create edges between types in this namespace
            var seenEdges = new HashSet<string>();
            for (int i = 0; i < types.Count; i++)
            {
                TypeModel type = types[i];
                for (int j = 0; j < type.BaseTypes.Count; j++)
                {
                    var baseType = type.BaseTypes[j];
                    // Use the RESOLVED FQN: a base written by short name (`class Circle(Shape)`)
                    // stores "Shape" but the visual map is keyed by "pkg.mod.Shape" (see #28).
                    string targetFqn = baseType.EffectiveFqn;
                    if (!baseType.IsExternal && targetFqn != null && _typeIdToVisual.ContainsKey(targetFqn))
                    {
                        string edgeKey = type.FullyQualifiedName + "|" + targetFqn;
                        if (seenEdges.Contains(edgeKey))
                        {
                            continue;
                        }
                        seenEdges.Add(edgeKey);

                        var edgeVisual = CreateEdgeVisual(type, baseType);
                        if (edgeVisual != null)
                        {
                            _scene.AddEdge(edgeVisual);
                        }
                    }
                }
            }

            // Also add cross-namespace edges that connect to types in the broader solution
            // (show them as circles if needed)
            var allTypes = solution.GetAllTypes();
            foreach (var type in types)
            {
                foreach (var baseType in type.BaseTypes)
                {
                    if (baseType.IsExternal) continue;
                    // Resolve to the real FQN before the dedup check — otherwise a same-namespace
                    // base written by short name (e.g. Circle's "Shape") fails ContainsKey and gets
                    // added a SECOND time as a stray circle (the duplicate-node bug).
                    string targetFqn = baseType.EffectiveFqn;
                    if (targetFqn == null || _typeIdToVisual.ContainsKey(targetFqn)) continue;

                    // Find the actual type model
                    TypeModel targetType = baseType.ResolvedType ?? solution.FindType(targetFqn);
                    if (targetType == null) continue;

                    // Add as circle
                    var nodeVisual = CreateNodeVisualWithBatch(targetType, batchLayout);
                    nodeVisual.IsCircleMode = true;
                    _scene.AddNode(nodeVisual);
                    _typeIdToVisual[targetType.FullyQualifiedName] = nodeVisual;

                    string edgeKey = type.FullyQualifiedName + "|" + targetFqn;
                    if (!seenEdges.Contains(edgeKey))
                    {
                        seenEdges.Add(edgeKey);
                        var edgeVisual = CreateEdgeVisual(type, baseType);
                        if (edgeVisual != null)
                        {
                            _scene.AddEdge(edgeVisual);
                        }
                    }
                }
            }

            // Also add edges where external types inherit from types in this namespace
            foreach (var extType in allTypes)
            {
                if (_typeIdToVisual.ContainsKey(extType.FullyQualifiedName)) continue;

                foreach (var baseType in extType.BaseTypes)
                {
                    if (baseType.IsExternal) continue;
                    if (!_typeIdToVisual.ContainsKey(baseType.EffectiveFqn)) continue;

                    // This external type inherits from a type in our namespace — add as circle
                    if (!_typeIdToVisual.ContainsKey(extType.FullyQualifiedName))
                    {
                        var nodeVisual = CreateNodeVisualWithBatch(extType, batchLayout);
                        nodeVisual.IsCircleMode = true;
                        _scene.AddNode(nodeVisual);
                        _typeIdToVisual[extType.FullyQualifiedName] = nodeVisual;
                    }

                    string edgeKey = extType.FullyQualifiedName + "|" + baseType.EffectiveFqn;
                    if (!seenEdges.Contains(edgeKey))
                    {
                        seenEdges.Add(edgeKey);
                        var edgeVisual = CreateEdgeVisual(extType, baseType);
                        if (edgeVisual != null)
                        {
                            _scene.AddEdge(edgeVisual);
                        }
                    }
                }
            }

            // No two nodes should share a position (stale-persisted vs fresh-batch collision, #35).
            ResolveExactNodeOverlaps();

            // Route edges
            _edgeRouter.RouteAllEdges(_scene);
        }

        /// <summary>
        /// Save current node positions to layout store for the given scope key prefix.
        /// </summary>
        public void SaveCurrentPositions()
        {
            foreach (var node in _scene.Nodes)
            {
                string key = node.NodeId;
                var layout = _layoutStore.GetPosition(key);
                if (layout == null)
                {
                    layout = new NodeLayoutData();
                }
                layout.PositionX = node.WorldBounds.X;
                layout.PositionY = node.WorldBounds.Y;
                layout.Width = node.WorldBounds.Width;
                layout.Height = node.WorldBounds.Height;
                _layoutStore.SetPosition(key, layout);
            }
        }
    }
}
