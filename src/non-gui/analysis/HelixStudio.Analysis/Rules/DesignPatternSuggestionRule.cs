namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    /// <summary>
    /// Analyzes the diagram structure and suggests applicable design patterns
    /// based on actual code relationships and usage patterns found in the solution.
    /// </summary>
    public sealed class DesignPatternSuggestionRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "PAT001"; }
        }

        public string Description
        {
            get { return "Design pattern opportunity detected"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            if (diagram == null || diagram.Nodes == null || diagram.Nodes.Count == 0)
            {
                return results;
            }

            // Build lookup structures for efficient analysis
            var nodeMap = new Dictionary<Guid, TypeNode>();
            foreach (var node in diagram.Nodes)
            {
                nodeMap[node.Id] = node;
            }

            var implementors = BuildImplementorsMap(diagram);
            var inheritors = BuildInheritorsMap(diagram);
            var dependsOn = BuildDependencyMap(diagram);

            // Run each pattern detector
            results.AddRange(DetectFactoryOpportunities(diagram, nodeMap, implementors, inheritors));
            results.AddRange(DetectStrategyOpportunities(diagram, nodeMap, implementors));
            results.AddRange(DetectObserverOpportunities(diagram, nodeMap, implementors, dependsOn));
            results.AddRange(DetectDecoratorOpportunities(diagram, nodeMap, implementors, inheritors));
            results.AddRange(DetectTemplateMethodOpportunities(diagram, nodeMap, inheritors));
            results.AddRange(DetectFacadeOpportunities(diagram, nodeMap, dependsOn));

            return results;
        }

        /// <summary>
        /// Build a map: interface/abstract Guid -> list of concrete implementors
        /// </summary>
        private static Dictionary<Guid, List<TypeNode>> BuildImplementorsMap(Diagram diagram)
        {
            var map = new Dictionary<Guid, List<TypeNode>>();
            foreach (var rel in diagram.Relationships)
            {
                if (rel.Kind == RelationshipKind.Implements && rel.Source != null && rel.Target != null)
                {
                    List<TypeNode> list;
                    if (!map.TryGetValue(rel.Target.Id, out list))
                    {
                        list = new List<TypeNode>();
                        map[rel.Target.Id] = list;
                    }
                    list.Add(rel.Source);
                }
            }
            return map;
        }

        /// <summary>
        /// Build a map: base class Guid -> list of derived classes
        /// </summary>
        private static Dictionary<Guid, List<TypeNode>> BuildInheritorsMap(Diagram diagram)
        {
            var map = new Dictionary<Guid, List<TypeNode>>();
            foreach (var rel in diagram.Relationships)
            {
                if (rel.Kind == RelationshipKind.Inherits && rel.Source != null && rel.Target != null)
                {
                    List<TypeNode> list;
                    if (!map.TryGetValue(rel.Target.Id, out list))
                    {
                        list = new List<TypeNode>();
                        map[rel.Target.Id] = list;
                    }
                    list.Add(rel.Source);
                }
            }
            return map;
        }

        /// <summary>
        /// Build a map: node Guid -> list of nodes it depends on
        /// </summary>
        private static Dictionary<Guid, List<TypeNode>> BuildDependencyMap(Diagram diagram)
        {
            var map = new Dictionary<Guid, List<TypeNode>>();
            foreach (var rel in diagram.Relationships)
            {
                if (rel.Kind == RelationshipKind.DependsOn && rel.Source != null && rel.Target != null)
                {
                    List<TypeNode> list;
                    if (!map.TryGetValue(rel.Source.Id, out list))
                    {
                        list = new List<TypeNode>();
                        map[rel.Source.Id] = list;
                    }
                    list.Add(rel.Target);
                }
            }
            return map;
        }

        /// <summary>
        /// Detect Factory pattern opportunities:
        /// An interface or abstract class with 3+ concrete implementations suggests
        /// a Factory could centralize object creation.
        /// </summary>
        private static IEnumerable<AnalysisResult> DetectFactoryOpportunities(
            Diagram diagram,
            Dictionary<Guid, TypeNode> nodeMap,
            Dictionary<Guid, List<TypeNode>> implementors,
            Dictionary<Guid, List<TypeNode>> inheritors)
        {
            var results = new List<AnalysisResult>();

            foreach (var node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.Interface && node.TypeKind != TypeKind.AbstractClass)
                {
                    continue;
                }

                int implCount = 0;
                List<TypeNode> implList;
                if (implementors.TryGetValue(node.Id, out implList))
                {
                    implCount += implList.Count;
                }
                List<TypeNode> inhList;
                if (inheritors.TryGetValue(node.Id, out inhList))
                {
                    implCount += inhList.Count;
                }

                if (implCount >= 3)
                {
                    var concreteNames = new List<string>();
                    if (implList != null)
                    {
                        foreach (var n in implList)
                        {
                            concreteNames.Add(n.Name);
                        }
                    }
                    if (inhList != null)
                    {
                        foreach (var n in inhList)
                        {
                            concreteNames.Add(n.Name);
                        }
                    }

                    string message = string.Format(
                        "Factory Pattern: '{0}' has {1} implementations ({2}). " +
                        "Consider a Factory to centralize creation of these variants and improve extensibility.",
                        node.Name,
                        implCount,
                        string.Join(", ", concreteNames));

                    results.Add(new AnalysisResult(
                        "PAT001",
                        AnalysisSeverity.Info,
                        message,
                        node.Id));
                }
            }

            return results;
        }

        /// <summary>
        /// Detect Strategy pattern opportunities:
        /// An interface with exactly 2+ implementations where the interface has
        /// a single primary method (or small cohesive set) suggests Strategy pattern.
        /// </summary>
        private static IEnumerable<AnalysisResult> DetectStrategyOpportunities(
            Diagram diagram,
            Dictionary<Guid, TypeNode> nodeMap,
            Dictionary<Guid, List<TypeNode>> implementors)
        {
            var results = new List<AnalysisResult>();

            foreach (var node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.Interface)
                {
                    continue;
                }

                // Strategy interfaces typically have 1-3 methods
                if (node.Members.Count < 1 || node.Members.Count > 3)
                {
                    continue;
                }

                List<TypeNode> impls;
                if (!implementors.TryGetValue(node.Id, out impls) || impls.Count < 2)
                {
                    continue;
                }

                // Check that implementations actually override/implement the interface methods
                bool allImplement = true;
                foreach (var impl in impls)
                {
                    bool hasMatchingMethod = false;
                    foreach (var ifaceMethod in node.Members)
                    {
                        foreach (var implMethod in impl.Members)
                        {
                            if (implMethod.Name == ifaceMethod.Name)
                            {
                                hasMatchingMethod = true;
                                break;
                            }
                        }
                        if (hasMatchingMethod)
                        {
                            break;
                        }
                    }
                    if (!hasMatchingMethod && impl.Members.Count > 0)
                    {
                        allImplement = false;
                        break;
                    }
                }

                if (!allImplement)
                {
                    continue;
                }

                // Already reported as Factory if 3+ impls, only suggest Strategy for 2 impls
                // or when the interface has exactly 1 method (clear strategy signature)
                if (impls.Count >= 3 && node.Members.Count > 1)
                {
                    continue;
                }

                var implNames = new List<string>();
                foreach (var impl in impls)
                {
                    implNames.Add(impl.Name);
                }

                string methodNames;
                var mNames = new List<string>();
                foreach (var m in node.Members)
                {
                    mNames.Add(m.Name);
                }
                methodNames = string.Join(", ", mNames);

                string message = string.Format(
                    "Strategy Pattern: '{0}' defines [{1}] with {2} interchangeable implementations ({3}). " +
                    "This is a natural Strategy pattern - consider documenting it as such for clarity.",
                    node.Name,
                    methodNames,
                    impls.Count,
                    string.Join(", ", implNames));

                results.Add(new AnalysisResult(
                    "PAT002",
                    AnalysisSeverity.Info,
                    message,
                    node.Id));
            }

            return results;
        }

        /// <summary>
        /// Detect Observer pattern opportunities:
        /// Types with event-like members (names containing "Event", "Handler", "Listener",
        /// "Changed", "Notify", "Subscribe") suggest Observer pattern.
        /// </summary>
        private static IEnumerable<AnalysisResult> DetectObserverOpportunities(
            Diagram diagram,
            Dictionary<Guid, TypeNode> nodeMap,
            Dictionary<Guid, List<TypeNode>> implementors,
            Dictionary<Guid, List<TypeNode>> dependsOn)
        {
            var results = new List<AnalysisResult>();

            string[] eventKeywords = new string[]
            {
                "Event", "Handler", "Listener", "Changed", "Notify",
                "Subscribe", "Unsubscribe", "Observer", "OnChanged",
                "Callback", "Delegate"
            };

            foreach (var node in diagram.Nodes)
            {
                int eventMemberCount = 0;
                var eventMembers = new List<string>();

                foreach (var member in node.Members)
                {
                    foreach (string keyword in eventKeywords)
                    {
                        if (member.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            eventMemberCount++;
                            eventMembers.Add(member.Name);
                            break;
                        }
                    }
                }

                if (eventMemberCount >= 2)
                {
                    string message = string.Format(
                        "Observer Pattern: '{0}' has {1} event-related members ({2}). " +
                        "Consider formalizing with an Observer or Pub/Sub pattern to decouple event producers from consumers.",
                        node.Name,
                        eventMemberCount,
                        string.Join(", ", eventMembers));

                    results.Add(new AnalysisResult(
                        "PAT003",
                        AnalysisSeverity.Info,
                        message,
                        node.Id));
                }
            }

            return results;
        }

        /// <summary>
        /// Detect Decorator pattern opportunities:
        /// A concrete class that both implements an interface AND has a dependency
        /// on that same interface suggests a Decorator/Wrapper pattern.
        /// Also: an interface with implementations that hold a reference to the same interface type.
        /// </summary>
        private static IEnumerable<AnalysisResult> DetectDecoratorOpportunities(
            Diagram diagram,
            Dictionary<Guid, TypeNode> nodeMap,
            Dictionary<Guid, List<TypeNode>> implementors,
            Dictionary<Guid, List<TypeNode>> inheritors)
        {
            var results = new List<AnalysisResult>();

            // Build set of (source, target) for Implements relationships
            var implementsSet = new HashSet<string>();
            foreach (var rel in diagram.Relationships)
            {
                if (rel.Kind == RelationshipKind.Implements && rel.Source != null && rel.Target != null)
                {
                    implementsSet.Add(rel.Source.Id + "|" + rel.Target.Id);
                }
            }

            // Find types that both implement an interface AND depend on it
            foreach (var rel in diagram.Relationships)
            {
                if (rel.Kind == RelationshipKind.DependsOn && rel.Source != null && rel.Target != null)
                {
                    TypeNode target;
                    if (!nodeMap.TryGetValue(rel.Target.Id, out target))
                    {
                        continue;
                    }

                    if (target.TypeKind != TypeKind.Interface)
                    {
                        continue;
                    }

                    // Check if source also implements target
                    string key = rel.Source.Id + "|" + rel.Target.Id;
                    if (implementsSet.Contains(key))
                    {
                        string message = string.Format(
                            "Decorator Pattern: '{0}' both implements and depends on '{1}'. " +
                            "This is characteristic of the Decorator pattern - " +
                            "consider using it to add behavior transparently.",
                            rel.Source.Name,
                            target.Name);

                        results.Add(new AnalysisResult(
                            "PAT004",
                            AnalysisSeverity.Info,
                            message,
                            rel.Source.Id));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Detect Template Method pattern opportunities:
        /// An abstract class with a mix of abstract and concrete methods suggests
        /// Template Method pattern.
        /// </summary>
        private static IEnumerable<AnalysisResult> DetectTemplateMethodOpportunities(
            Diagram diagram,
            Dictionary<Guid, TypeNode> nodeMap,
            Dictionary<Guid, List<TypeNode>> inheritors)
        {
            var results = new List<AnalysisResult>();

            foreach (var node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.AbstractClass)
                {
                    continue;
                }

                int abstractCount = 0;
                int concreteCount = 0;
                var abstractNames = new List<string>();
                var concreteNames = new List<string>();

                foreach (var member in node.Members)
                {
                    if (member.IsAbstract)
                    {
                        abstractCount++;
                        abstractNames.Add(member.Name);
                    }
                    else if (member.MemberKind == MemberKind.Method)
                    {
                        concreteCount++;
                        concreteNames.Add(member.Name);
                    }
                }

                // Template Method: has both abstract steps and concrete orchestration
                if (abstractCount >= 1 && concreteCount >= 1)
                {
                    List<TypeNode> derived;
                    int derivedCount = 0;
                    if (inheritors.TryGetValue(node.Id, out derived))
                    {
                        derivedCount = derived.Count;
                    }

                    if (derivedCount >= 1)
                    {
                        string message = string.Format(
                            "Template Method Pattern: '{0}' has {1} abstract step(s) [{2}] and {3} concrete method(s) [{4}] " +
                            "with {5} derived class(es). This matches the Template Method pattern where the base class " +
                            "defines the algorithm skeleton and subclasses override specific steps.",
                            node.Name,
                            abstractCount,
                            string.Join(", ", abstractNames),
                            concreteCount,
                            string.Join(", ", concreteNames),
                            derivedCount);

                        results.Add(new AnalysisResult(
                            "PAT005",
                            AnalysisSeverity.Info,
                            message,
                            node.Id));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Detect Facade pattern opportunities:
        /// A concrete class that depends on 4+ other types suggests it might benefit
        /// from being organized as a Facade.
        /// </summary>
        private static IEnumerable<AnalysisResult> DetectFacadeOpportunities(
            Diagram diagram,
            Dictionary<Guid, TypeNode> nodeMap,
            Dictionary<Guid, List<TypeNode>> dependsOn)
        {
            var results = new List<AnalysisResult>();

            foreach (var node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.ConcreteClass)
                {
                    continue;
                }

                List<TypeNode> deps;
                if (!dependsOn.TryGetValue(node.Id, out deps) || deps.Count < 4)
                {
                    continue;
                }

                var depNames = new List<string>();
                foreach (var dep in deps)
                {
                    depNames.Add(dep.Name);
                }

                string message = string.Format(
                    "Facade Pattern: '{0}' depends on {1} types ({2}). " +
                    "If this class coordinates access to these subsystems, consider documenting it " +
                    "as a Facade to clarify its role as a simplified interface.",
                    node.Name,
                    deps.Count,
                    string.Join(", ", depNames));

                results.Add(new AnalysisResult(
                    "PAT006",
                    AnalysisSeverity.Info,
                    message,
                    node.Id));
            }

            return results;
        }
    }
}
