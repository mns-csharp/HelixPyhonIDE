namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class CircularDependencyRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "DEP001"; }
        }

        public string Description
        {
            get { return "Circular dependency detected"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            // Build adjacency list from DependsOn relationships
            Dictionary<Guid, List<Guid>> adjacency = new Dictionary<Guid, List<Guid>>();
            Dictionary<Guid, TypeNode> nodeMap = new Dictionary<Guid, TypeNode>();

            foreach (TypeNode node in diagram.Nodes)
            {
                nodeMap[node.Id] = node;
                adjacency[node.Id] = new List<Guid>();
            }

            foreach (Relationship rel in diagram.Relationships)
            {
                if (rel.Kind == RelationshipKind.DependsOn)
                {
                    if (adjacency.ContainsKey(rel.Source.Id))
                    {
                        adjacency[rel.Source.Id].Add(rel.Target.Id);
                    }
                }
            }

            // DFS cycle detection
            // 0 = unvisited, 1 = in current path, 2 = fully visited
            Dictionary<Guid, int> state = new Dictionary<Guid, int>();
            HashSet<Guid> nodesInCycles = new HashSet<Guid>();
            List<Guid> currentPath = new List<Guid>();

            foreach (TypeNode node in diagram.Nodes)
            {
                state[node.Id] = 0;
            }

            foreach (TypeNode node in diagram.Nodes)
            {
                if (state[node.Id] == 0)
                {
                    DetectCycles(node.Id, adjacency, state, currentPath, nodesInCycles);
                }
            }

            foreach (Guid nodeId in nodesInCycles)
            {
                if (nodeMap.ContainsKey(nodeId))
                {
                    TypeNode node = nodeMap[nodeId];
                    string message = string.Format(
                        "Type '{0}' is part of a circular dependency chain.",
                        node.Name);

                    results.Add(new AnalysisResult(
                        RuleId,
                        AnalysisSeverity.Error,
                        message,
                        node.Id));
                }
            }

            return results;
        }

        private static void DetectCycles(
            Guid nodeId,
            Dictionary<Guid, List<Guid>> adjacency,
            Dictionary<Guid, int> state,
            List<Guid> currentPath,
            HashSet<Guid> nodesInCycles)
        {
            state[nodeId] = 1;
            currentPath.Add(nodeId);

            if (adjacency.ContainsKey(nodeId))
            {
                foreach (Guid neighbor in adjacency[nodeId])
                {
                    if (!state.ContainsKey(neighbor))
                    {
                        continue;
                    }

                    if (state[neighbor] == 1)
                    {
                        // Found a cycle - mark all nodes from the cycle start to current
                        bool inCycle = false;
                        for (int i = 0; i < currentPath.Count; i++)
                        {
                            if (currentPath[i] == neighbor)
                            {
                                inCycle = true;
                            }
                            if (inCycle)
                            {
                                nodesInCycles.Add(currentPath[i]);
                            }
                        }
                    }
                    else if (state[neighbor] == 0)
                    {
                        DetectCycles(neighbor, adjacency, state, currentPath, nodesInCycles);
                    }
                }
            }

            currentPath.RemoveAt(currentPath.Count - 1);
            state[nodeId] = 2;
        }
    }
}
