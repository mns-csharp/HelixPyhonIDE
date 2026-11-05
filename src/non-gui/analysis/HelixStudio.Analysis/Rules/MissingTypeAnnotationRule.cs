using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Services;

namespace HelixPythonNamespace.Analysis.Rules
{
    /// <summary>
    /// Flags public methods without return-type annotations. Python annotations
    /// are optional but improve readability, enable static checkers, and help
    /// downstream tooling (including this one) classify members correctly.
    /// </summary>
    public sealed class MissingTypeAnnotationRule : IAnalysisRule
    {
        public string RuleId => "PY-ANN-001";
        public string Description => "Public method lacks a return-type annotation";

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();
            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.Members == null) continue;
                foreach (MemberDefinition m in node.Members)
                {
                    if (m.MemberKind != MemberKind.Method) continue;
                    if (m.AccessModifier != AccessModifier.Public) continue;
                    if (!string.IsNullOrEmpty(m.ReturnType)) continue;
                    if (m.Name != null && m.Name.StartsWith("__")) continue; // dunders are conventional
                    results.Add(new AnalysisResult(
                        RuleId,
                        AnalysisSeverity.Info,
                        "Method '" + node.Name + "." + m.Name + "' has no return-type annotation.",
                        node.Id));
                }
            }
            return results;
        }
    }
}
