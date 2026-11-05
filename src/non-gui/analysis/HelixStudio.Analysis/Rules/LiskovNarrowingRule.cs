namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class LiskovNarrowingRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "LSP001"; }
        }

        public string Description
        {
            get { return "Possible Liskov Substitution Principle violation"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            // Build a map of node Id to TypeNode for quick lookup
            Dictionary<Guid, TypeNode> nodeMap = new Dictionary<Guid, TypeNode>();
            foreach (TypeNode node in diagram.Nodes)
            {
                nodeMap[node.Id] = node;
            }

            foreach (Relationship rel in diagram.Relationships)
            {
                if (rel.Kind != RelationshipKind.Inherits)
                {
                    continue;
                }

                TypeNode concreteNode = rel.Source;
                TypeNode baseNode = rel.Target;

                // Only check concrete classes inheriting from abstract classes
                if (concreteNode.TypeKind != TypeKind.ConcreteClass)
                {
                    continue;
                }

                if (baseNode.TypeKind != TypeKind.AbstractClass)
                {
                    continue;
                }

                // Build a set of member names in the concrete class
                HashSet<string> concreteMemberNames = new HashSet<string>();
                foreach (MemberDefinition member in concreteNode.Members)
                {
                    concreteMemberNames.Add(member.Name);
                }

                // Check each public member in the base class
                foreach (MemberDefinition baseMember in baseNode.Members)
                {
                    if (baseMember.AccessModifier != AccessModifier.Public)
                    {
                        continue;
                    }

                    if (!concreteMemberNames.Contains(baseMember.Name))
                    {
                        string message = string.Format(
                            "Type '{0}' may violate LSP relative to '{1}'. The base class has public members not mirrored in the derived type.",
                            concreteNode.Name,
                            baseNode.Name);

                        results.Add(new AnalysisResult(
                            RuleId,
                            AnalysisSeverity.Warning,
                            message,
                            concreteNode.Id));

                        // Only report once per concrete-base pair
                        break;
                    }

                    // Check for access modifier narrowing
                    MemberDefinition concreteMember = null;
                    foreach (MemberDefinition m in concreteNode.Members)
                    {
                        if (m.Name == baseMember.Name)
                        {
                            concreteMember = m;
                            break;
                        }
                    }

                    if (concreteMember != null && concreteMember.AccessModifier > baseMember.AccessModifier)
                    {
                        string message = string.Format(
                            "Type '{0}' may violate LSP relative to '{1}'. The base class has public members not mirrored in the derived type.",
                            concreteNode.Name,
                            baseNode.Name);

                        results.Add(new AnalysisResult(
                            RuleId,
                            AnalysisSeverity.Warning,
                            message,
                            concreteNode.Id));

                        // Only report once per concrete-base pair
                        break;
                    }
                }
            }

            return results;
        }
    }
}
