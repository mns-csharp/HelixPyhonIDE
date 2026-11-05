using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.UI.Services
{
    /// <summary>
    /// Severity level for impact items.
    /// </summary>
    public enum ImpactSeverity
    {
        Info,
        Warning,
        Critical
    }

    /// <summary>
    /// A single impact item from deletion analysis.
    /// </summary>
    public sealed class ImpactItem
    {
        public ImpactItem(ImpactSeverity severity, string description)
        {
            Severity = severity;
            Description = description;
        }

        public ImpactSeverity Severity { get; }
        public string Description { get; }
    }

    /// <summary>
    /// Summary of the impact of a deletion operation.
    /// </summary>
    public sealed class DeletionImpactSummary
    {
        public DeletionImpactSummary()
        {
            Impacts = new List<ImpactItem>();
            AffectedTypes = new List<string>();
            AffectedFiles = new List<string>();
        }

        /// <summary>
        /// List of impact items.
        /// </summary>
        public List<ImpactItem> Impacts { get; }

        /// <summary>
        /// FQNs of types that will be affected.
        /// </summary>
        public List<string> AffectedTypes { get; }

        /// <summary>
        /// Files that will be modified or deleted.
        /// </summary>
        public List<string> AffectedFiles { get; }

        /// <summary>
        /// Whether there are any critical impacts.
        /// </summary>
        public bool HasCriticalImpacts
        {
            get { return Impacts.Any(i => i.Severity == ImpactSeverity.Critical); }
        }

        /// <summary>
        /// Whether there are any warning impacts.
        /// </summary>
        public bool HasWarnings
        {
            get { return Impacts.Any(i => i.Severity == ImpactSeverity.Warning); }
        }

        /// <summary>
        /// Total count of affected items.
        /// </summary>
        public int TotalAffectedCount
        {
            get { return AffectedTypes.Count + AffectedFiles.Count; }
        }
    }

    /// <summary>
    /// Analyzes the impact of deletion operations.
    /// </summary>
    public sealed class DeletionImpactAnalyzer
    {
        /// <summary>
        /// Analyze the impact of deleting a type.
        /// </summary>
        public DeletionImpactSummary AnalyzeTypeDeletion(SolutionModel solution, string fullyQualifiedName)
        {
            var summary = new DeletionImpactSummary();

            if (solution == null || string.IsNullOrEmpty(fullyQualifiedName))
            {
                return summary;
            }

            var type = solution.FindType(fullyQualifiedName);
            if (type == null)
            {
                return summary;
            }

            // Add the type's source file
            if (!string.IsNullOrEmpty(type.SourceFilePath))
            {
                summary.AffectedFiles.Add(type.SourceFilePath);
                summary.Impacts.Add(new ImpactItem(ImpactSeverity.Warning,
                    "Source file will be modified or deleted: " + type.SourceFilePath));
            }

            // Find types that derive from or implement this type
            var derivedTypes = FindDerivedTypes(solution, fullyQualifiedName);
            foreach (var derived in derivedTypes)
            {
                summary.AffectedTypes.Add(derived.FullyQualifiedName);
                summary.Impacts.Add(new ImpactItem(ImpactSeverity.Critical,
                    string.Format("{0} inherits from {1}", derived.Name, type.Name)));
            }

            // Find types that implement this interface
            var implementingTypes = FindImplementingTypes(solution, fullyQualifiedName);
            foreach (var impl in implementingTypes)
            {
                if (!summary.AffectedTypes.Contains(impl.FullyQualifiedName))
                {
                    summary.AffectedTypes.Add(impl.FullyQualifiedName);
                    summary.Impacts.Add(new ImpactItem(ImpactSeverity.Critical,
                        string.Format("{0} implements {1}", impl.Name, type.Name)));
                }
            }

            // Find types that have dependencies on this type
            var dependentTypes = FindDependentTypes(solution, fullyQualifiedName);
            foreach (var dep in dependentTypes)
            {
                if (!summary.AffectedTypes.Contains(dep.FullyQualifiedName))
                {
                    summary.AffectedTypes.Add(dep.FullyQualifiedName);
                    summary.Impacts.Add(new ImpactItem(ImpactSeverity.Warning,
                        string.Format("{0} depends on {1}", dep.Name, type.Name)));
                }
            }

            // Check if file contains only this type
            if (!string.IsNullOrEmpty(type.SourceFilePath))
            {
                var typesInFile = type.Project.Types
                    .Count(t => t.SourceFilePath == type.SourceFilePath);

                if (typesInFile == 1)
                {
                    summary.Impacts.Add(new ImpactItem(ImpactSeverity.Info,
                        "Source file will be deleted (contains only this type)"));
                }
                else
                {
                    summary.Impacts.Add(new ImpactItem(ImpactSeverity.Info,
                        string.Format("Source file contains {0} other type(s)", typesInFile - 1)));
                }
            }

            return summary;
        }

        /// <summary>
        /// Analyze the impact of removing a relationship.
        /// </summary>
        public DeletionImpactSummary AnalyzeRelationshipRemoval(
            SolutionModel solution,
            string sourceFQN,
            string targetFQN)
        {
            var summary = new DeletionImpactSummary();

            if (solution == null)
            {
                return summary;
            }

            var sourceType = solution.FindType(sourceFQN);
            var targetType = solution.FindType(targetFQN);

            if (sourceType == null || targetType == null)
            {
                return summary;
            }

            summary.AffectedTypes.Add(sourceFQN);

            if (!string.IsNullOrEmpty(sourceType.SourceFilePath))
            {
                summary.AffectedFiles.Add(sourceType.SourceFilePath);
            }

            // Determine relationship type
            var relationship = sourceType.BaseTypes
                .FirstOrDefault(bt => bt.FullyQualifiedName == targetFQN);

            if (relationship != null)
            {
                if (relationship.RelationshipKind == RelationshipKind.Implements)
                {
                    summary.Impacts.Add(new ImpactItem(ImpactSeverity.Warning,
                        string.Format("{0} will no longer implement {1}", sourceType.Name, targetType.Name)));

                    // Check for interface members that need to be removed
                    var interfaceMembers = targetType.Members.Count;
                    if (interfaceMembers > 0)
                    {
                        summary.Impacts.Add(new ImpactItem(ImpactSeverity.Info,
                            string.Format("{0} interface member(s) may need to be updated", interfaceMembers)));
                    }
                }
                else if (relationship.RelationshipKind == RelationshipKind.Inherits)
                {
                    summary.Impacts.Add(new ImpactItem(ImpactSeverity.Critical,
                        string.Format("{0} will no longer inherit from {1}", sourceType.Name, targetType.Name)));

                    // Check for inherited members
                    var inheritedMembers = targetType.Members.Count(m => !m.IsStatic);
                    if (inheritedMembers > 0)
                    {
                        summary.Impacts.Add(new ImpactItem(ImpactSeverity.Warning,
                            string.Format("{0} inherited member(s) will no longer be available", inheritedMembers)));
                    }
                }
            }

            return summary;
        }

        private List<TypeModel> FindDerivedTypes(SolutionModel solution, string baseTypeFQN)
        {
            var derived = new List<TypeModel>();

            foreach (var type in solution.GetAllTypes())
            {
                foreach (var baseType in type.BaseTypes)
                {
                    if (baseType.FullyQualifiedName == baseTypeFQN &&
                        baseType.RelationshipKind == RelationshipKind.Inherits)
                    {
                        derived.Add(type);
                        break;
                    }
                }
            }

            return derived;
        }

        private List<TypeModel> FindImplementingTypes(SolutionModel solution, string interfaceFQN)
        {
            var implementing = new List<TypeModel>();

            foreach (var type in solution.GetAllTypes())
            {
                foreach (var baseType in type.BaseTypes)
                {
                    if (baseType.FullyQualifiedName == interfaceFQN &&
                        baseType.RelationshipKind == RelationshipKind.Implements)
                    {
                        implementing.Add(type);
                        break;
                    }
                }
            }

            return implementing;
        }

        private List<TypeModel> FindDependentTypes(SolutionModel solution, string targetFQN)
        {
            var dependent = new List<TypeModel>();

            foreach (var type in solution.GetAllTypes())
            {
                // Check member return types and parameters
                foreach (var member in type.Members)
                {
                    if (member.ReturnType != null && member.ReturnType.Contains(GetSimpleName(targetFQN)))
                    {
                        dependent.Add(type);
                        break;
                    }

                    foreach (var param in member.Parameters)
                    {
                        if (param.TypeName != null && param.TypeName.Contains(GetSimpleName(targetFQN)))
                        {
                            dependent.Add(type);
                            break;
                        }
                    }
                }
            }

            return dependent;
        }

        private string GetSimpleName(string fqn)
        {
            if (string.IsNullOrEmpty(fqn))
            {
                return fqn;
            }
            var lastDot = fqn.LastIndexOf('.');
            return lastDot >= 0 ? fqn.Substring(lastDot + 1) : fqn;
        }
    }
}
