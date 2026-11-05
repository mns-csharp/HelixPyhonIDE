namespace HelixPythonNamespace.Refactoring
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Refactoring.Vectorization;
    using K = VectorizationCandidate.Kind;

    /// <summary>
    /// Public facade: Parse selection → strict Tier-1 → relaxed Tier-2 (deduped
    /// against strict kinds) → optional Tier-3 re-rank → re-indent. Port of the
    /// C++ <c>VectorizationRefactor</c>.
    /// </summary>
    public static class VectorizationRefactor
    {
        public static List<VectorizationCandidate> Match(string selectedSource)
        {
            return Match(selectedSource, "", null);
        }

        public static List<VectorizationCandidate> Match(string selectedSource, string fullFileSource)
        {
            return Match(selectedSource, fullFileSource, null);
        }

        public static List<VectorizationCandidate> Match(string selectedSource, string fullFileSource, VectorizationRanker ranker)
        {
            ParsedSelection parsed = SelectionParser.Parse(selectedSource);
            if (parsed.Module == null) return new List<VectorizationCandidate>();

            List<VectorizationCandidate> candidates = VecPatternMatcher.MatchAll(parsed.Module);

            HashSet<int> strictKinds = new HashSet<int>();
            foreach (VectorizationCandidate c in candidates) strictKinds.Add((int)c.PatternKind);

            List<VectorizationCandidate> relaxed = VecRelaxationEngine.MatchAllRelaxed(parsed.Module);
            foreach (VectorizationCandidate c in relaxed)
            {
                if (strictKinds.Contains((int)c.PatternKind)) continue;
                candidates.Add(c);
            }

            if (ranker != null)
            {
                ranker.Rerank(candidates, selectedSource, fullFileSource);
            }

            int indentCols = parsed.CommonIndent.Length;
            foreach (VectorizationCandidate c in candidates)
            {
                c.ReplacementText = SelectionParser.ReapplyIndent(c.ReplacementText, parsed.CommonIndent, parsed.LineEnding);
                if (c.HasSubrange && indentCols > 0)
                {
                    c.SubrangeStartColumn += indentCols;
                    c.SubrangeEndColumn += indentCols;
                }
            }
            return candidates;
        }

        public static List<SupportedPatternInfo> SupportedPatterns()
        {
            return new List<SupportedPatternInfo>
            {
                Info(K.F1_ElementwiseBinop, "F1 — element-wise binop", "for i in range(len(a)):\n    c[i] = a[i] + b[i]", "c = np.asarray(a) + np.asarray(b)"),
                Info(K.F2_ElementwiseNegate, "F2 — element-wise negate", "for i in range(len(a)):\n    c[i] = -a[i]", "c = -np.asarray(a)"),
                Info(K.F3_ScalarBroadcast, "F3 — scalar broadcast", "for i in range(len(a)):\n    c[i] = a[i] * 2", "c = np.asarray(a) * 2"),
                Info(K.F4_MathToNumpy, "F4 — math.fn → numpy", "for i in range(len(a)):\n    c[i] = math.sin(a[i])", "c = np.sin(np.asarray(a))"),
                Info(K.F5_MapAppend, "F5 — map append → comprehension", "result = []\nfor x in xs:\n    result.append(f(x))", "result = [f(x) for x in xs]"),
                Info(K.F6_FilterAppend, "F6 — filter append → comprehension", "result = []\nfor x in xs:\n    if x > 0:\n        result.append(x)", "result = [x for x in xs if x > 0]"),
                Info(K.F7_FilterMapAppend, "F7 — filter+map append → comprehension", "result = []\nfor x in xs:\n    if x > 0:\n        result.append(f(x))", "result = [f(x) for x in xs if x > 0]"),
                Info(K.F8_Sum, "F8 — additive accumulator → sum()", "total = 0\nfor x in xs:\n    total += x", "total = sum(xs)"),
                Info(K.F9_Product, "F9 — multiplicative accumulator → math.prod()", "prod = 1\nfor x in xs:\n    prod *= x", "prod = math.prod(xs)"),
                Info(K.F10_MinMax, "F10 — min/max scan → min() / max()", "best = xs[0]\nfor x in xs[1:]:\n    if x > best:\n        best = x", "best = max(xs)"),
                Info(K.F11_Count, "F11 — conditional counter → sum(1 for …)", "n = 0\nfor x in xs:\n    if x > 0:\n        n += 1", "n = sum(1 for x in xs if x > 0)"),
                Info(K.F12_Any, "F12 — early-true scan → any()", "for x in xs:\n    if x > 0:\n        return True\nreturn False", "return any(x > 0 for x in xs)"),
                Info(K.F13_All, "F13 — early-false scan → all()", "for x in xs:\n    if not (x > 0):\n        return False\nreturn True", "return all(x > 0 for x in xs)"),
                Info(K.F14_Where, "F14 — conditional selection → np.where()", "for i in range(len(a)):\n    c[i] = a[i] if cond[i] else b[i]", "c = np.where(cond, a, b)"),
                Info(K.F15_Cumsum, "F15 — running sum → np.cumsum()", "running = 0\nout = []\nfor x in xs:\n    running += x\n    out.append(running)", "out = list(np.cumsum(xs))"),
                Info(K.F16_RangeFill, "F16 — range index fill → comprehension", "out = [None] * 5\nfor i in range(5):\n    out[i] = i * 2", "out = [i * 2 for i in range(5)]"),
                Info(K.R1_ElementwiseBinopToLoop, "R1 — devectorize element-wise binop", "c = np.asarray(a) + np.asarray(b)", "for i in range(len(a)):\n    c[i] = a[i] + b[i]"),
                Info(K.R2_SumToLoop, "R2 — devectorize sum()", "total = sum(xs)", "total = 0\nfor x in xs:\n    total += x"),
                Info(K.R5_ComprehensionToLoop, "R5 — devectorize comprehension", "result = [f(x) for x in xs]", "result = []\nfor x in xs:\n    result.append(f(x))"),
                Info(K.R10_MaxToLoop, "R10 — devectorize max() / min()", "best = max(xs)", "best = xs[0]\nfor x in xs[1:]:\n    if x > best:\n        best = x"),
                Info(K.R14_WhereToLoop, "R14 — devectorize np.where()", "c = np.where(cond, a, b)", "for i in range(len(a)):\n    c[i] = a[i] if cond[i] else b[i]"),
            };
        }

        private static SupportedPatternInfo Info(K kind, string name, string example, string replacement)
        {
            return new SupportedPatternInfo { PatternKind = kind, Name = name, ExampleSource = example, Replacement = replacement };
        }
    }
}
