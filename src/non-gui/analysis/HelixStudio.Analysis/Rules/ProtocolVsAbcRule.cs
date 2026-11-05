using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Services;

namespace HelixPythonNamespace.Analysis.Rules
{
    /// <summary>
    /// Suggests <c>typing.Protocol</c> over <c>abc.ABC</c> when a class has only
    /// abstract methods and no state. Structural subtyping fits that shape
    /// better and removes the need for callers to inherit explicitly.
    /// </summary>
    public sealed class ProtocolVsAbcRule : IAnalysisRule
    {
        public string RuleId => "PY-PROT-001";
        public string Description => "Abstract class with only abstract methods and no state may be better as a Protocol";

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();
            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.AbstractClass) continue;
                if (node.Members == null || node.Members.Count == 0) continue;
                bool hasState = false;
                bool hasConcreteMethod = false;
                foreach (MemberDefinition m in node.Members)
                {
                    if (m.MemberKind == MemberKind.Field || m.MemberKind == MemberKind.Property) hasState = true;
                    if (m.MemberKind == MemberKind.Method && !m.IsAbstract) hasConcreteMethod = true;
                }
                if (!hasState && !hasConcreteMethod)
                {
                    results.Add(new AnalysisResult(
                        RuleId,
                        AnalysisSeverity.Info,
                        "Abstract class '" + node.Name + "' has only abstract methods and no state. Consider declaring it as a typing.Protocol for structural subtyping.",
                        node.Id));
                }
            }
            return results;
        }
    }
}
