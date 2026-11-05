namespace HelixPythonNamespace.Refactoring
{
    using System;
    using System.Collections.Generic;
    using K = HelixPythonNamespace.Refactoring.StructuralCandidate.Kind;

    /// <summary>
    /// Pure-logic bridge from analysis findings (rule IDs) to structural refactoring
    /// candidates. Port of the C++ <c>DetectionBridge</c>.
    /// <para>
    /// The analysis rules (SRP001 / DIP001 / ISP001 / PAT001–006) report code smells and
    /// pattern opportunities but carry NO source spans, so they are consumed only as
    /// targeting HINTS: a fired rule enables the structural family it maps to, and the
    /// structural matchers own all span derivation. Only FORWARD families are ever
    /// auto-suggested — the analyzer flags opportunities to introduce a pattern, never to
    /// undo one.
    /// </para>
    /// </summary>
    public static class DetectionBridge
    {
        /// <summary>
        /// The forward candidate kinds an analysis rule id hints at. Empty if the rule maps
        /// to no structural family. Matching is by rule-id prefix so that suffixed variants
        /// (e.g. "PAT002.x") still map.
        /// </summary>
        public static List<K> KindsForRule(string ruleId)
        {
            List<K> outKinds = new List<K>();
            if (ruleId == null) return outKinds;

            // GoF pattern-suggestion rules (PAT001-006) and the SOLID smell rules map
            // each to the FORWARD introduce/split family.
            if (RuleIs(ruleId, "PAT001")) outKinds.Add(K.P1_FactoryIntroduce);
            else if (RuleIs(ruleId, "PAT002")) outKinds.Add(K.P2_StrategyIntroduce);
            else if (RuleIs(ruleId, "PAT003")) outKinds.Add(K.P3_ObserverIntroduce);
            else if (RuleIs(ruleId, "PAT004")) outKinds.Add(K.P4_DecoratorIntroduce);
            else if (RuleIs(ruleId, "PAT005")) outKinds.Add(K.P5_TemplateIntroduce);
            else if (RuleIs(ruleId, "PAT006")) outKinds.Add(K.P6_FacadeIntroduce);
            else if (RuleIs(ruleId, "SRP001")) outKinds.Add(K.S1_SrpSplit);
            else if (RuleIs(ruleId, "DIP001")) outKinds.Add(K.S2_DipIntroduce);
            else if (RuleIs(ruleId, "ISP001")) outKinds.Add(K.S3_IspSegregate);
            return outKinds;
        }

        /// <summary>True if <paramref name="kind"/> is a forward family hinted by any of <paramref name="firedRuleIds"/>.</summary>
        public static bool IsHinted(K kind, IEnumerable<string> firedRuleIds)
        {
            if (firedRuleIds == null) return false;
            foreach (string id in firedRuleIds)
            {
                if (KindsForRule(id).Contains(kind)) return true;
            }
            return false;
        }

        /// <summary>
        /// Filter <paramref name="candidates"/> down to those whose kind is hinted by some
        /// fired rule. Preserves input order.
        /// </summary>
        public static List<StructuralCandidate> Gate(
            IEnumerable<string> firedRuleIds, IEnumerable<StructuralCandidate> candidates)
        {
            List<StructuralCandidate> outList = new List<StructuralCandidate>();
            if (candidates == null) return outList;

            // Materialize once: `firedRuleIds` may be a lazily-evaluated sequence, and the
            // C++ signature takes a vector that is safe to re-scan per candidate.
            List<string> fired = firedRuleIds == null
                ? new List<string>()
                : new List<string>(firedRuleIds);

            foreach (StructuralCandidate c in candidates)
            {
                if (IsHinted(c.PatternKind, fired)) outList.Add(c);
            }
            return outList;
        }

        private static bool RuleIs(string ruleId, string prefix)
        {
            return ruleId.StartsWith(prefix, StringComparison.Ordinal);
        }
    }
}
