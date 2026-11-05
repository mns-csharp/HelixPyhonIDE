using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Rendering;

namespace HelixPythonNamespace.UI.Projection
{
    /// <summary>
    /// Type-category-banded auto-layout strategy.
    /// Places types into three horizontal bands by category:
    ///   Band 0 (top):    Interfaces
    ///   Band 1 (middle): Abstract Classes
    ///   Band 2 (bottom): Concrete Classes (including Struct, Enum)
    /// Within each band, types are arranged in a grid sorted alphabetically by FQN
    /// for deterministic layout.
    /// </summary>
    public sealed class GridAutoLayoutStrategy : IAutoLayoutStrategy
    {
        private int _expectedCount;
        private int _columnsPerRow;

        // Cached band layout from the last ComputeAllPositions call,
        // used by the single-node fallback ComputePosition method.
        private Dictionary<string, NodeLayoutData> _lastBatchLayout;
        private float _lastBand0EndY;
        private float _lastBand1EndY;
        private float _lastBand2EndY;
        private int _lastBand0Columns;
        private int _lastBand1Columns;
        private int _lastBand2Columns;
        private int _lastBand0Count;
        private int _lastBand1Count;
        private int _lastBand2Count;

        public GridAutoLayoutStrategy()
        {
            _expectedCount = 0;
            _columnsPerRow = 4;
            _lastBatchLayout = new Dictionary<string, NodeLayoutData>();
        }

        public void SetExpectedCount(int totalCount)
        {
            _expectedCount = totalCount;
            _columnsPerRow = LayoutConstants.CalculateColumnsForSquareGrid(totalCount);
        }

        /// <summary>
        /// Compute positions for all types at once using type-category banding.
        /// </summary>
        public Dictionary<string, NodeLayoutData> ComputeAllPositions(IEnumerable<TypeModel> types)
        {
            var result = new Dictionary<string, NodeLayoutData>();
            if (types == null)
            {
                return result;
            }

            // Sort all types into three bands by TypeKind
            var band0 = new List<TypeModel>(); // Interfaces
            var band1 = new List<TypeModel>(); // Abstract Classes
            var band2 = new List<TypeModel>(); // Concrete Classes, Structs, Enums

            foreach (var type in types)
            {
                int bandIndex = GetBandIndex(type.TypeKind);
                switch (bandIndex)
                {
                    case 0:
                        band0.Add(type);
                        break;
                    case 1:
                        band1.Add(type);
                        break;
                    default:
                        band2.Add(type);
                        break;
                }
            }

            // Place each band with a hierarchy-aware layered layout: a type's row is one below
            // the deepest of its in-band bases (so children always render below everything they
            // inherit from — a proper Sugiyama-style layered layout), and within a row, a
            // barycenter crossing-minimization pass pulls related types toward the same columns
            // as their neighbors in adjacent rows. A row wider than the band's usual matrix-shape
            // column count wraps into sub-rows, so a band with no relationships at all — every
            // type lands in row 0 — still degrades to the familiar N-shaped matrix (R3.1) instead
            // of stretching into one very wide row. Rows are computed sequentially (not all three
            // bands up front) because each band's own row count now depends on its hierarchy
            // depth, not a fixed ceil(count/columns) formula.
            float band0StartY = LayoutConstants.StartY;
            int rows0 = LayeredPlaceBand(band0, band0StartY, result);
            float band0EndY = band0StartY + rows0 * LayoutConstants.YSpacing;

            float band1StartY = band0EndY;
            if (band0.Count > 0 && band1.Count > 0)
            {
                band1StartY += LayoutConstants.BandGap;
            }
            int rows1 = LayeredPlaceBand(band1, band1StartY, result);
            float band1EndY = band1StartY + rows1 * LayoutConstants.YSpacing;

            float band2StartY = band1EndY;
            if ((band0.Count > 0 || band1.Count > 0) && band2.Count > 0)
            {
                band2StartY += LayoutConstants.BandGap;
            }
            int rows2 = LayeredPlaceBand(band2, band2StartY, result);
            float band2EndY = band2StartY + rows2 * LayoutConstants.YSpacing;

            // Columns are still tracked for the single-node fallback in ComputePosition, which
            // appends to the end of a band using the plain matrix-shape formula rather than
            // re-running the full layered layout for one new node.
            int cols0 = LayoutConstants.CalculateColumnsForSquareGrid(band0.Count);
            int cols1 = LayoutConstants.CalculateColumnsForSquareGrid(band1.Count);
            int cols2 = LayoutConstants.CalculateColumnsForSquareGrid(band2.Count);

            // Cache band layout info for single-node fallback
            _lastBatchLayout = new Dictionary<string, NodeLayoutData>(result);
            _lastBand0EndY = band0EndY;
            _lastBand1EndY = band1EndY;
            _lastBand2EndY = band2EndY;
            _lastBand0Columns = cols0;
            _lastBand1Columns = cols1;
            _lastBand2Columns = cols2;
            _lastBand0Count = band0.Count;
            _lastBand1Count = band1.Count;
            _lastBand2Count = band2.Count;

            return result;
        }

        /// <summary>
        /// Compute a position for a single new type.
        /// Uses the cached band information from the last batch layout call
        /// to place the new type at the end of its appropriate band.
        /// Falls back to a simple grid if no batch layout has been computed.
        /// </summary>
        public NodeLayoutData ComputePosition(TypeModel type, IEnumerable<NodeVisual> existingNodes)
        {
            var layout = new NodeLayoutData();
            layout.Width = LayoutConstants.NodeWidth;
            layout.Height = LayoutConstants.NodeHeight;
            layout.IsCollapsed = false;

            int bandIndex = GetBandIndex(type.TypeKind);

            // If we have cached band info from a batch layout, place at end of the right band
            if (_lastBatchLayout.Count > 0)
            {
                float bandStartY;
                int bandColumns;
                int bandCount;

                switch (bandIndex)
                {
                    case 0:
                        bandStartY = LayoutConstants.StartY;
                        bandColumns = _lastBand0Columns;
                        bandCount = _lastBand0Count;
                        break;
                    case 1:
                        bandStartY = _lastBand0EndY;
                        if (_lastBand0Count > 0)
                        {
                            bandStartY += LayoutConstants.BandGap;
                        }
                        bandColumns = _lastBand1Columns;
                        bandCount = _lastBand1Count;
                        break;
                    default:
                        bandStartY = _lastBand1EndY;
                        if (_lastBand0Count > 0 || _lastBand1Count > 0)
                        {
                            bandStartY += LayoutConstants.BandGap;
                        }
                        bandColumns = _lastBand2Columns;
                        bandCount = _lastBand2Count;
                        break;
                }

                // Recalculate columns for the new count
                int newCount = bandCount + 1;
                int newColumns = LayoutConstants.CalculateColumnsForSquareGrid(newCount);

                // Place at the next position (append at end)
                int col = bandCount % newColumns;
                int row = bandCount / newColumns;

                layout.PositionX = LayoutConstants.StartX + col * LayoutConstants.XSpacing;
                layout.PositionY = bandStartY + row * LayoutConstants.YSpacing;

                // Update cached counts
                switch (bandIndex)
                {
                    case 0:
                        _lastBand0Count = newCount;
                        _lastBand0Columns = newColumns;
                        break;
                    case 1:
                        _lastBand1Count = newCount;
                        _lastBand1Columns = newColumns;
                        break;
                    default:
                        _lastBand2Count = newCount;
                        _lastBand2Columns = newColumns;
                        break;
                }

                return layout;
            }

            // Fallback: simple grid placement (no batch layout available)
            var nodesList = existingNodes != null ? existingNodes.ToList() : new List<NodeVisual>();

            if (_expectedCount == 0 && nodesList.Count > 0)
            {
                _columnsPerRow = LayoutConstants.CalculateColumnsForSquareGrid(nodesList.Count + 1);
            }

            var occupiedPositions = new HashSet<string>();
            foreach (var node in nodesList)
            {
                var bounds = node.WorldBounds;
                var gridX = (int)((bounds.X - LayoutConstants.StartX + LayoutConstants.XSpacing / 2) / LayoutConstants.XSpacing);
                var gridY = (int)((bounds.Y - LayoutConstants.StartY + LayoutConstants.YSpacing / 2) / LayoutConstants.YSpacing);
                occupiedPositions.Add(gridX + "," + gridY);
            }

            int targetGridX = 0;
            int targetGridY = 0;
            bool found = false;

            for (int rowIdx = 0; rowIdx < 100 && !found; rowIdx++)
            {
                for (int colIdx = 0; colIdx < _columnsPerRow && !found; colIdx++)
                {
                    var key = colIdx + "," + rowIdx;
                    if (!occupiedPositions.Contains(key))
                    {
                        targetGridX = colIdx;
                        targetGridY = rowIdx;
                        found = true;
                    }
                }
            }

            layout.PositionX = LayoutConstants.StartX + targetGridX * LayoutConstants.XSpacing;
            layout.PositionY = LayoutConstants.StartY + targetGridY * LayoutConstants.YSpacing;

            return layout;
        }

        /// <summary>
        /// Get the band index for a type kind.
        /// Band 0: Interface, Band 1: AbstractClass, Band 2: everything else.
        /// </summary>
        private static int GetBandIndex(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface:
                    return 0;
                case TypeKind.AbstractClass:
                    return 1;
                default:
                    return 2;
            }
        }

        /// <summary>
        /// Places one band with a hierarchy-aware layered layout (Sugiyama-style): each type's
        /// row ("layer") is one below the deepest of its in-band bases, computed via topological
        /// order so a multiply-inherited type (e.g. GuideDog(Trainable, Domesticated, Dog)) always
        /// renders below every one of its bases, not just the first. Within a layer, alternating
        /// downward/upward barycenter sweeps pull each type toward the average column of its
        /// neighbors in the adjacent layer, which is the standard crossing-minimization heuristic
        /// for layered graph drawing. A layer wider than the band's usual matrix-shape column
        /// count (LayoutConstants.CalculateColumnsForSquareGrid) wraps into sub-rows — so a band
        /// with no relationships at all (everything lands in layer 0) still degrades to the
        /// familiar N-shaped matrix (R3.1) instead of stretching into one very wide row.
        /// Returns the total number of (sub-)rows the band consumed, for band-to-band Y offsets.
        /// </summary>
        private static int LayeredPlaceBand(
            List<TypeModel> bandTypes,
            float bandStartY,
            Dictionary<string, NodeLayoutData> result)
        {
            if (bandTypes.Count == 0)
            {
                return 0;
            }

            int columnCap = LayoutConstants.CalculateColumnsForSquareGrid(bandTypes.Count);

            var byFqn = new Dictionary<string, TypeModel>();
            foreach (var t in bandTypes) byFqn[t.FullyQualifiedName] = t;

            // In-band base/child adjacency (directed). Only bases that are themselves part of
            // this band participate in layering — a cross-band base (e.g. Dog's Animal, which
            // lives in the AbstractClass band) can't anchor a layer here, so Dog starts at layer 0
            // like any other root.
            var inBandBases = new Dictionary<string, List<string>>();
            var inBandChildren = new Dictionary<string, List<string>>();
            foreach (var t in bandTypes)
            {
                inBandBases[t.FullyQualifiedName] = new List<string>();
                inBandChildren[t.FullyQualifiedName] = new List<string>();
            }
            foreach (var t in bandTypes)
            {
                if (t.BaseTypes == null) continue;
                foreach (var baseType in t.BaseTypes)
                {
                    if (baseType.IsExternal) continue;
                    string baseFqn = baseType.EffectiveFqn;
                    if (baseFqn != null && baseFqn != t.FullyQualifiedName && byFqn.ContainsKey(baseFqn))
                    {
                        inBandBases[t.FullyQualifiedName].Add(baseFqn);
                        inBandChildren[baseFqn].Add(t.FullyQualifiedName);
                    }
                }
            }

            // Layer assignment via Kahn's algorithm (types form a DAG — Python doesn't allow
            // inheritance cycles): a type's layer is one more than the deepest of its in-band
            // bases, so it always renders below everything it inherits from in this band.
            var layer = new Dictionary<string, int>();
            var remaining = new Dictionary<string, int>();
            foreach (var t in bandTypes) remaining[t.FullyQualifiedName] = inBandBases[t.FullyQualifiedName].Count;

            var roots = new List<string>();
            foreach (var t in bandTypes)
                if (remaining[t.FullyQualifiedName] == 0) roots.Add(t.FullyQualifiedName);
            roots.Sort(StringComparer.Ordinal);

            var queue = new Queue<string>(roots);
            foreach (var id in roots) layer[id] = 0;

            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                var children = new List<string>(inBandChildren[cur]);
                children.Sort(StringComparer.Ordinal);
                foreach (var child in children)
                {
                    int candidate = layer[cur] + 1;
                    int existing;
                    if (!layer.TryGetValue(child, out existing) || candidate > existing)
                    {
                        layer[child] = candidate;
                    }

                    remaining[child]--;
                    if (remaining[child] == 0)
                    {
                        queue.Enqueue(child);
                    }
                }
            }
            // Guard: anything left unlayered (shouldn't happen for a real DAG) defaults to 0 so
            // it's never dropped from the diagram.
            foreach (var t in bandTypes)
                if (!layer.ContainsKey(t.FullyQualifiedName)) layer[t.FullyQualifiedName] = 0;

            int maxLayer = 0;
            foreach (var kv in layer) if (kv.Value > maxLayer) maxLayer = kv.Value;

            var layers = new List<List<string>>();
            for (int i = 0; i <= maxLayer; i++) layers.Add(new List<string>());
            foreach (var t in bandTypes) layers[layer[t.FullyQualifiedName]].Add(t.FullyQualifiedName);
            foreach (var l in layers) l.Sort(StringComparer.Ordinal);

            // Barycenter crossing-minimization: alternate downward sweeps (reorder layer L by the
            // average position of its bases in layer L-1) and upward sweeps (reorder layer L by
            // the average position of its children in layer L+1) for a few passes — the standard
            // heuristic used by layered graph-drawing algorithms (e.g. Graphviz dot's ranking).
            // Positions are normalized to 0..1 WITHIN each layer (not a raw column index) because
            // layers can end up very different widths — a 13-wide root layer and a 6-wide child
            // layer — and a raw index would compare "column 5 of 13" against "column 5 of 6" as if
            // they were the same physical position, which they aren't.
            var columnOf = new Dictionary<string, double>();
            RefreshColumns(layers, columnOf);

            const int sweeps = 4;
            for (int sweep = 0; sweep < sweeps; sweep++)
            {
                if (sweep % 2 == 0)
                {
                    for (int li = 1; li <= maxLayer; li++)
                        BarycenterReorder(layers[li], inBandBases, columnOf);
                }
                else
                {
                    for (int li = maxLayer - 1; li >= 0; li--)
                        BarycenterReorder(layers[li], inBandChildren, columnOf);
                }
                RefreshColumns(layers, columnOf);
            }

            // Emit positions: flatten the layers (in topological order — every base before its
            // children — with each layer's own barycenter-refined order preserved) into ONE
            // sequence, then wrap that sequence at the band's usual matrix-shape column count,
            // exactly like the plain grid did before hierarchy-awareness existed. This keeps the
            // N-shaped matrix footprint (R3.1) unconditionally — a 19-node band is still ~5x4, not
            // a hierarchy-driven 13-wide/6-wide two-row band — while still placing related types
            // near each other in the flattened order, so the matrix-wrapped result reads far more
            // clustered than plain declaration order did. A wide root layer can share a physical
            // row with the start of the next layer where the column count doesn't divide evenly;
            // that's an acceptable trade against always keeping the same overall shape.
            var flattened = new List<string>(bandTypes.Count);
            for (int li = 0; li <= maxLayer; li++) flattened.AddRange(layers[li]);

            for (int i = 0; i < flattened.Count; i++)
            {
                int col = i % columnCap;
                int row = i / columnCap;

                var nodeLayout = new NodeLayoutData();
                nodeLayout.Width = LayoutConstants.NodeWidth;
                nodeLayout.Height = LayoutConstants.NodeHeight;
                nodeLayout.IsCollapsed = false;
                nodeLayout.PositionX = LayoutConstants.StartX + col * LayoutConstants.XSpacing;
                nodeLayout.PositionY = bandStartY + row * LayoutConstants.YSpacing;

                result[flattened[i]] = nodeLayout;
            }

            return (flattened.Count + columnCap - 1) / columnCap;
        }

        private static void RefreshColumns(List<List<string>> layers, Dictionary<string, double> columnOf)
        {
            foreach (var l in layers)
            {
                for (int i = 0; i < l.Count; i++)
                {
                    columnOf[l[i]] = l.Count > 1 ? (double)i / (l.Count - 1) : 0.5;
                }
            }
        }

        /// <summary>
        /// Reorders one layer by the average column position of each node's neighbors (per
        /// <paramref name="neighborMap"/> — either bases or children, depending on sweep
        /// direction), stable-sorted so ties keep their prior relative order (deterministic, R3.1).
        /// A node with no neighbors on this side keeps its current column rather than collapsing
        /// to 0, so isolated types don't get yanked to one edge of the layer.
        /// </summary>
        private static void BarycenterReorder(
            List<string> layerNodes,
            Dictionary<string, List<string>> neighborMap,
            Dictionary<string, double> columnOf)
        {
            if (layerNodes.Count <= 1) return;

            var barycenter = new Dictionary<string, double>();
            foreach (var id in layerNodes)
            {
                List<string> neighbors = neighborMap[id];
                if (neighbors.Count == 0)
                {
                    barycenter[id] = columnOf[id];
                    continue;
                }
                double sum = 0;
                foreach (var n in neighbors) sum += columnOf.ContainsKey(n) ? columnOf[n] : 0;
                barycenter[id] = sum / neighbors.Count;
            }

            var withIndex = new List<KeyValuePair<int, string>>(layerNodes.Count);
            for (int i = 0; i < layerNodes.Count; i++) withIndex.Add(new KeyValuePair<int, string>(i, layerNodes[i]));
            withIndex.Sort((a, b) =>
            {
                int cmp = barycenter[a.Value].CompareTo(barycenter[b.Value]);
                return cmp != 0 ? cmp : a.Key.CompareTo(b.Key);
            });

            layerNodes.Clear();
            foreach (var kv in withIndex) layerNodes.Add(kv.Value);
        }
    }
}
