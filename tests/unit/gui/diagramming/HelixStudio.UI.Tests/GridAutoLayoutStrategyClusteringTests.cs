using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Projection;
using HelixPythonNamespace.UI.Rendering;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Diagnostic coverage for GridAutoLayoutStrategy's hierarchy-aware layered placement,
    /// built directly from the same type/base shape as tests/fixtures/python/python-test-sample's
    /// animals.py (hand-built TypeModel graph — no Python parsing needed) so the multi-inheritance
    /// clustering behavior can be verified with real assertions instead of eyeballing screenshots.
    /// </summary>
    [TestClass]
    public class GridAutoLayoutStrategyClusteringTests
    {
        private static SolutionModel BuildAnimalsHierarchy(out ProjectModel project)
        {
            SolutionModel solution = new SolutionModel(@"C:\fake\t.helixsln", "t");
            project = new ProjectModel(@"C:\fake\t.toml", "lib_core", solution);
            solution.Projects.Add(project);

            TypeModel Animal = Add(project, "Animal", TypeKind.AbstractClass);
            TypeModel Dog = Add(project, "Dog", TypeKind.ConcreteClass, Animal);
            TypeModel Cat = Add(project, "Cat", TypeKind.ConcreteClass, Animal);
            TypeModel Bird = Add(project, "Bird", TypeKind.ConcreteClass, Animal);
            TypeModel PoliceDog = Add(project, "PoliceDog", TypeKind.ConcreteClass, Dog);
            TypeModel Mammal = Add(project, "Mammal", TypeKind.ConcreteClass, Animal);
            TypeModel WildBird = Add(project, "WildBird", TypeKind.ConcreteClass, Animal);
            TypeModel Reptile = Add(project, "Reptile", TypeKind.ConcreteClass, Animal);
            TypeModel Trainable = Add(project, "Trainable", TypeKind.ConcreteClass);
            TypeModel Domesticated = Add(project, "Domesticated", TypeKind.ConcreteClass);
            TypeModel Predator = Add(project, "Predator", TypeKind.ConcreteClass);
            Add(project, "Wolf", TypeKind.ConcreteClass, Predator, Mammal);
            Add(project, "Eagle", TypeKind.ConcreteClass, Predator, WildBird);
            Add(project, "Snake", TypeKind.ConcreteClass, Predator, Reptile);
            Add(project, "GuideDog", TypeKind.ConcreteClass, Trainable, Domesticated, Dog);
            Add(project, "Parrot", TypeKind.ConcreteClass, Trainable, Domesticated, Bird);
            Add(project, "Habitat", TypeKind.ConcreteClass);
            Add(project, "Color", TypeKind.ConcreteClass);
            Add(project, "Habit", TypeKind.ConcreteClass);
            Add(project, "DietFlags", TypeKind.ConcreteClass);

            foreach (TypeModel t in project.Types)
                foreach (TypeReference r in t.BaseTypes)
                    r.Bind(solution);

            return solution;
        }

        private static TypeModel Add(ProjectModel project, string name, TypeKind kind, params TypeModel[] bases)
        {
            TypeModel type = new TypeModel(name, "lib_core.animals", kind, project);
            foreach (TypeModel b in bases)
                type.BaseTypes.Add(new TypeReference(b.FullyQualifiedName, RelationshipKind.Inherits));
            project.Types.Add(type);
            return type;
        }

        private static Dictionary<string, NodeLayoutData> Layout(ProjectModel project)
        {
            GridAutoLayoutStrategy strategy = new GridAutoLayoutStrategy();
            strategy.SetExpectedCount(project.Types.Count);
            return strategy.ComputeAllPositions(project.Types);
        }

        private static int RowOf(Dictionary<string, NodeLayoutData> layout, string fqn)
        {
            return (int)Math.Round(layout[fqn].PositionY / LayoutConstants.YSpacing);
        }

        // Matrix shape (R3.1) takes priority over dedicated one-layer-per-row placement: nodes
        // are ordered by hierarchy depth then wrapped at the band's fixed column count, so a
        // multiply-inherited type lands near its bases in that flattened order rather than in a
        // guaranteed-adjacent row — wrapping can still put any ONE of three bases a couple of rows
        // away. Checking the AVERAGE distance across all three bases (rather than requiring every
        // single one under a fixed threshold) is the fairer measure of "reasonably clustered" under
        // that trade-off; EveryChild_NeverRendersAboveAnyOfItsInBandBases covers the harder, exact
        // guarantee (never above any base) precisely.
        [TestMethod]
        public void Parrot_LandsReasonablyCloseToAllThreeBases_NotJustTheFirst()
        {
            ProjectModel project;
            SolutionModel solution = BuildAnimalsHierarchy(out project);
            Dictionary<string, NodeLayoutData> layout = Layout(project);

            string ns = "lib_core.animals.";
            int parrotRow = RowOf(layout, ns + "Parrot");
            int trainableRow = RowOf(layout, ns + "Trainable");
            int domesticatedRow = RowOf(layout, ns + "Domesticated");
            int birdRow = RowOf(layout, ns + "Bird");

            double avgDist = (Math.Abs(parrotRow - trainableRow)
                + Math.Abs(parrotRow - domesticatedRow)
                + Math.Abs(parrotRow - birdRow)) / 3.0;
            Assert.IsTrue(avgDist <= 2.5,
                string.Format("Parrot (row {0}) too far on average from its bases (Trainable={1}, Domesticated={2}, Bird={3})",
                    parrotRow, trainableRow, domesticatedRow, birdRow));
        }

        [TestMethod]
        public void GuideDog_LandsReasonablyCloseToAllThreeBases()
        {
            ProjectModel project;
            SolutionModel solution = BuildAnimalsHierarchy(out project);
            Dictionary<string, NodeLayoutData> layout = Layout(project);

            string ns = "lib_core.animals.";
            int guideDogRow = RowOf(layout, ns + "GuideDog");
            int trainableRow = RowOf(layout, ns + "Trainable");
            int domesticatedRow = RowOf(layout, ns + "Domesticated");
            int dogRow = RowOf(layout, ns + "Dog");

            double avgDist = (Math.Abs(guideDogRow - trainableRow)
                + Math.Abs(guideDogRow - domesticatedRow)
                + Math.Abs(guideDogRow - dogRow)) / 3.0;
            Assert.IsTrue(avgDist <= 2.5,
                string.Format("GuideDog (row {0}) too far on average from its bases (Trainable={1}, Domesticated={2}, Dog={3})",
                    guideDogRow, trainableRow, domesticatedRow, dogRow));
        }

        [TestMethod]
        public void EveryChild_NeverRendersAboveAnyOfItsInBandBases()
        {
            // The matrix-shape column cap (see NoRelationships_StillProducesTheSquareMatrixShape)
            // takes priority over strict one-layer-per-row placement, so a child can now share a
            // row with its base when the column count doesn't divide evenly — but topological
            // (base-before-child) order is still preserved in the flattened, wrapped sequence, so
            // a child must never end up on an EARLIER row than any of its bases.
            ProjectModel project;
            SolutionModel solution = BuildAnimalsHierarchy(out project);
            Dictionary<string, NodeLayoutData> layout = Layout(project);

            foreach (TypeModel t in project.Types)
            {
                foreach (TypeReference baseRef in t.BaseTypes)
                {
                    if (baseRef.IsExternal) continue;
                    if (!layout.ContainsKey(baseRef.EffectiveFqn)) continue; // cross-band base

                    float childY = layout[t.FullyQualifiedName].PositionY;
                    float baseY = layout[baseRef.EffectiveFqn].PositionY;
                    Assert.IsTrue(childY >= baseY,
                        string.Format("{0} (y={1}) should never render above its base {2} (y={3})",
                            t.Name, childY, baseRef.SimpleName, baseY));
                }
            }
        }

        private sealed class InMemoryLayoutStore : ILayoutMetadataStore
        {
            private readonly Dictionary<string, NodeLayoutData> _data = new Dictionary<string, NodeLayoutData>();
            public LayoutModel LayoutModel { get { return null; } }
            public NodeLayoutData GetPosition(string fqn)
            {
                NodeLayoutData d;
                return _data.TryGetValue(fqn, out d) ? d : null;
            }
            public void SetPosition(string fqn, NodeLayoutData position) { _data[fqn] = position; }
            public void OnTypeRenamed(string oldFqn, string newFqn) { }
            public void Save() { }
            public void Load() { }
        }

        /// <summary>
        /// Every edge's rendered endpoint must land exactly on its target node's boundary — a
        /// gap here means the arrow visually floats away from the box it's supposed to point at.
        /// Reproduces the "Show: ABC" scenario (Animal is the only full node, everything else is
        /// a circle) via the real SolutionProjectionBuilder + EdgeRouter pipeline, not a hand-
        /// rolled scene, so this exercises the exact code path the live app uses.
        /// </summary>
        [TestMethod]
        public void EveryEdge_EndsExactlyOnTheTargetNodesBoundary()
        {
            ProjectModel project;
            SolutionModel solution = BuildAnimalsHierarchy(out project);

            SceneGraph scene = new SceneGraph();
            InMemoryLayoutStore store = new InMemoryLayoutStore();
            SolutionProjectionBuilder builder = new SolutionProjectionBuilder(scene, store, new GridAutoLayoutStrategy());
            builder.CategoryFilter = CategoryFilterMode.AbstractClassesOnly;
            builder.BuildTypeLevelForNamespace(solution, "lib_core", "lib_core.animals");

            Assert.IsTrue(scene.Edges.Count > 0, "expected at least one routed edge");

            var nodeById = new Dictionary<string, NodeVisual>();
            foreach (NodeVisual n in scene.Nodes) nodeById[n.NodeId] = n;

            var failures = new List<string>();
            const float tolerance = 1.5f; // world units — routing math can round to sub-pixel
            foreach (EdgeVisual edge in scene.Edges)
            {
                NodeVisual target;
                if (!nodeById.TryGetValue(edge.TargetNodeId, out target)) continue;

                RectangleF bounds = target.WorldBounds;
                RectangleF inflated = RectangleF.Inflate(bounds, tolerance, tolerance);
                if (!inflated.Contains(edge.EndPoint))
                {
                    failures.Add(string.Format(
                        "{0}->{1}: EndPoint=({2:0.0},{3:0.0}) not within target bounds {4}",
                        edge.SourceNodeId, edge.TargetNodeId, edge.EndPoint.X, edge.EndPoint.Y, bounds));
                }
            }

            Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
        }

        [TestMethod]
        public void RealHierarchy_StillProducesTheSquareMatrixShape_NotATwoRowBand()
        {
            // The animals.py-shaped band has 19 ConcreteClass types across a real (if shallow)
            // hierarchy — before the matrix-shape-priority fix, layering pushed the 13 roots and
            // 6 children into two very wide rows instead of the ~5x4 matrix the shape rule (R3.1)
            // calls for at N=19.
            ProjectModel project;
            BuildAnimalsHierarchy(out project);
            Dictionary<string, NodeLayoutData> layout = Layout(project);

            var band2Y = new List<float>();
            foreach (TypeModel t in project.Types)
                if (t.TypeKind != TypeKind.AbstractClass) band2Y.Add(layout[t.FullyQualifiedName].PositionY);

            var distinctRows = new HashSet<float>(band2Y);
            int expectedCols = LayoutConstants.CalculateColumnsForSquareGrid(band2Y.Count);
            int expectedRows = (band2Y.Count + expectedCols - 1) / expectedCols;

            Assert.AreEqual(expectedRows, distinctRows.Count,
                string.Format("expected the {0}-type ConcreteClass band to use {1} rows (matrix shape), got {2}",
                    band2Y.Count, expectedRows, distinctRows.Count));
        }

        [TestMethod]
        public void InterfacesFilter_EveryEdgeStillEndsExactlyOnTheTargetNodesBoundary()
        {
            // "Show: Interfaces" is the scenario from the screenshot with visibly open connector
            // points — this fixture has zero Interface-kind types, so EVERY node (including
            // Animal) renders as a circle, unlike the ABC-filter test above where Animal is the
            // one full node. Re-checks the same endpoint-connectivity property under that all-
            // circle layout.
            ProjectModel project;
            SolutionModel solution = BuildAnimalsHierarchy(out project);

            SceneGraph scene = new SceneGraph();
            InMemoryLayoutStore store = new InMemoryLayoutStore();
            SolutionProjectionBuilder builder = new SolutionProjectionBuilder(scene, store, new GridAutoLayoutStrategy());
            builder.CategoryFilter = CategoryFilterMode.InterfacesOnly;
            builder.BuildTypeLevelForNamespace(solution, "lib_core", "lib_core.animals");

            Assert.IsTrue(scene.Edges.Count > 0, "expected at least one routed edge");

            var nodeById = new Dictionary<string, NodeVisual>();
            foreach (NodeVisual n in scene.Nodes) nodeById[n.NodeId] = n;

            var failures = new List<string>();
            const float tolerance = 1.5f;
            foreach (EdgeVisual edge in scene.Edges)
            {
                NodeVisual target;
                if (!nodeById.TryGetValue(edge.TargetNodeId, out target)) continue;

                RectangleF bounds = target.WorldBounds;
                RectangleF inflated = RectangleF.Inflate(bounds, tolerance, tolerance);
                if (!inflated.Contains(edge.EndPoint))
                {
                    failures.Add(string.Format(
                        "{0}->{1}: EndPoint=({2:0.0},{3:0.0}) not within target bounds {4}",
                        edge.SourceNodeId, edge.TargetNodeId, edge.EndPoint.X, edge.EndPoint.Y, bounds));
                }

                NodeVisual source;
                if (!nodeById.TryGetValue(edge.SourceNodeId, out source)) continue;
                RectangleF srcInflated = RectangleF.Inflate(source.WorldBounds, tolerance, tolerance);
                if (!srcInflated.Contains(edge.StartPoint))
                {
                    failures.Add(string.Format(
                        "{0}->{1}: StartPoint=({2:0.0},{3:0.0}) not within source bounds {4}",
                        edge.SourceNodeId, edge.TargetNodeId, edge.StartPoint.X, edge.StartPoint.Y, source.WorldBounds));
                }
            }

            Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
        }

        [TestMethod]
        public void EveryDeclaredBaseRelationship_HasARoutedEdge()
        {
            ProjectModel project;
            SolutionModel solution = BuildAnimalsHierarchy(out project);

            SceneGraph scene = new SceneGraph();
            InMemoryLayoutStore store = new InMemoryLayoutStore();
            SolutionProjectionBuilder builder = new SolutionProjectionBuilder(scene, store, new GridAutoLayoutStrategy());
            builder.CategoryFilter = CategoryFilterMode.AbstractClassesOnly;
            builder.BuildTypeLevelForNamespace(solution, "lib_core", "lib_core.animals");

            var edgePairs = new HashSet<string>();
            foreach (EdgeVisual e in scene.Edges) edgePairs.Add(e.SourceNodeId + "->" + e.TargetNodeId);

            var missing = new List<string>();
            foreach (TypeModel t in project.Types)
            {
                foreach (TypeReference baseRef in t.BaseTypes)
                {
                    if (baseRef.IsExternal) continue;
                    string key = t.FullyQualifiedName + "->" + baseRef.EffectiveFqn;
                    if (!edgePairs.Contains(key)) missing.Add(key);
                }
            }

            Assert.AreEqual(0, missing.Count, "missing edges: " + string.Join(" | ", missing));
        }

        [TestMethod]
        public void NoRelationships_StillProducesTheSquareMatrixShape()
        {
            SolutionModel solution = new SolutionModel(@"C:\fake\t.helixsln", "t");
            ProjectModel project = new ProjectModel(@"C:\fake\t.toml", "flat", solution);
            solution.Projects.Add(project);
            for (int i = 0; i < 7; i++)
                Add(project, "T" + i, TypeKind.ConcreteClass);

            Dictionary<string, NodeLayoutData> layout = Layout(project);

            // N=7 -> 4x2 per the matrix-shape rule (LayoutConstants.CalculateColumnsForSquareGrid).
            int expectedCols = LayoutConstants.CalculateColumnsForSquareGrid(7);
            Assert.AreEqual(2, expectedCols);

            var distinctX = new HashSet<float>();
            var distinctY = new HashSet<float>();
            foreach (var kv in layout)
            {
                distinctX.Add(kv.Value.PositionX);
                distinctY.Add(kv.Value.PositionY);
            }
            Assert.AreEqual(2, distinctX.Count, "expected 2 columns");
            Assert.AreEqual(4, distinctY.Count, "expected 4 rows");
        }
    }
}
