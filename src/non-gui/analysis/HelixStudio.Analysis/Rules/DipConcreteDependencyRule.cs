namespace HelixPythonNamespace.Analysis.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    /// <summary>
    /// DIP001: Flags types that depend (via inheritance or DependsOn) on concrete classes
    /// rather than abstractions. High-level modules should depend on interfaces or abstract
    /// classes, not concrete classes.
    /// </summary>
    public sealed class DipConcreteDependencyRule : IAnalysisRule
    {
        public string RuleId { get { return "DIP001"; } }

        public string Description { get { return "Dependency Inversion violation: depends on concrete class"; } }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();
            if (diagram == null || diagram.Nodes == null) return results;

            foreach (Relationship rel in diagram.Relationships)
            {
                if (rel.Source == null || rel.Target == null) continue;

                bool isDependency = rel.Kind == RelationshipKind.DependsOn
                                    || rel.Kind == RelationshipKind.Inherits;
                if (!isDependency) continue;

                if (rel.Target.TypeKind == TypeKind.ConcreteClass
                    && !rel.Target.Name.StartsWith("Base")
                    && !IsNameBaselike(rel.Target.Name))
                {
                    string message = string.Format(
                        "Type '{0}' depends on concrete class '{1}'. Prefer an interface or abstract class to satisfy the Dependency Inversion Principle.",
                        rel.Source.Name,
                        rel.Target.Name);

                    results.Add(new AnalysisResult(
                        RuleId,
                        AnalysisSeverity.Warning,
                        message,
                        rel.Source.Id));
                }
            }

            return results;
        }

        private static bool IsNameBaselike(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.EndsWith("Base") || name.StartsWith("Abstract");
        }
    }
}
