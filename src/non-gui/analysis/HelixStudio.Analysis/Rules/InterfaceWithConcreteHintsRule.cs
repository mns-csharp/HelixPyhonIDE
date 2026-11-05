namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class InterfaceWithConcreteHintsRule : IAnalysisRule
    {
        private static readonly string[] TechnologyKeywords = new string[]
        {
            "Sql", "File", "Http", "Xml", "Json"
        };

        public string RuleId
        {
            get { return "INT001"; }
        }

        public string Description
        {
            get { return "Interface members suggest implementation details"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.Interface)
                {
                    continue;
                }

                foreach (MemberDefinition member in node.Members)
                {
                    bool isSuspicious = false;

                    // Check for technology-specific keywords in member name
                    for (int i = 0; i < TechnologyKeywords.Length; i++)
                    {
                        if (member.Name.IndexOf(TechnologyKeywords[i], StringComparison.Ordinal) >= 0)
                        {
                            isSuspicious = true;
                            break;
                        }
                    }

                    // Check for action-verb prefixes that suggest concrete behavior
                    if (!isSuspicious)
                    {
                        isSuspicious = StartsWithActionVerb(member.Name);
                    }

                    if (isSuspicious)
                    {
                        string message = string.Format(
                            "Interface '{0}' member '{1}' suggests implementation-specific behavior. Interfaces should describe capabilities, not implementations.",
                            node.Name,
                            member.Name);

                        results.Add(new AnalysisResult(
                            RuleId,
                            AnalysisSeverity.Warning,
                            message,
                            node.Id));
                    }
                }
            }

            return results;
        }

        private static bool StartsWithActionVerb(string name)
        {
            string[] prefixes = new string[]
            {
                "Get", "Set", "Do", "Execute", "Process", "Handle", "Save", "Load", "Write", "Read"
            };

            for (int i = 0; i < prefixes.Length; i++)
            {
                string prefix = prefixes[i];
                if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
                {
                    // Ensure the character after the prefix is uppercase (indicating a suffix word)
                    char next = name[prefix.Length];
                    if (char.IsUpper(next))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
