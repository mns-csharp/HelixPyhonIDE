namespace HelixPythonNamespace.Refactoring
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Refactoring.LambdaTransform;
    using HelixPythonNamespace.Refactoring.Vectorization;
    using K = LambdaTransformCandidate.Kind;

    /// <summary>
    /// Public facade: Parse → strict Tier-1 → relaxed Tier-2 (additive) →
    /// optional Tier-3 re-rank → re-indent. Port of the C++ <c>LambdaTransformRefactor</c>.
    /// </summary>
    public static class LambdaTransformRefactor
    {
        public static List<LambdaTransformCandidate> Match(string selectedSource)
        {
            return Match(selectedSource, "", null);
        }

        public static List<LambdaTransformCandidate> Match(string selectedSource, string fullFileSource)
        {
            return Match(selectedSource, fullFileSource, null);
        }

        public static List<LambdaTransformCandidate> Match(string selectedSource, string fullFileSource, LambdaTransformRanker ranker)
        {
            ParsedSelection parsed = SelectionParser.Parse(selectedSource);
            if (parsed.Module == null) return new List<LambdaTransformCandidate>();

            List<LambdaTransformCandidate> candidates = LambdaPatternMatcher.MatchAll(parsed.Module);

            List<LambdaTransformCandidate> relaxed = LambdaRelaxationEngine.MatchAllRelaxed(parsed.Module, selectedSource);
            candidates.AddRange(relaxed);

            if (ranker != null)
            {
                ranker.Rerank(candidates, selectedSource, fullFileSource);
            }

            int indentCols = parsed.CommonIndent.Length;
            foreach (LambdaTransformCandidate c in candidates)
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

        public static List<LambdaTransformPatternInfo> SupportedPatterns()
        {
            return new List<LambdaTransformPatternInfo>
            {
                Info(K.F2L1_DefToLambda, "F2L1 — def -> lambda", "def add(x, y):\n    return x + y", "add = lambda x, y: x + y"),
                Info(K.F2L2_DefDefaultsToLambda, "F2L2 — def with defaults -> lambda", "def f(x, y=1, *args, **kw):\n    return x + y", "f = lambda x, y=1, *args, **kw: x + y"),
                Info(K.F2L3_DefIfReturnToTernary, "F2L3 — def if/else return -> ternary lambda", "def f(x):\n    if x > 0:\n        return x\n    else:\n        return -x", "f = lambda x: x if x > 0 else -x"),
                Info(K.F2L4_NestedDefToInlineLambda, "F2L4 — nested def -> inline lambda", "def helper(x):\n    return x * 2\nresult = list(map(helper, xs))", "result = list(map(lambda x: x * 2, xs))"),
                Info(K.L2F1_NamedLambdaToDef, "L2F1 — named lambda -> def", "add = lambda x, y: x + y", "def add(x, y):\n    return x + y"),
                Info(K.L2F2_AnonLambdaToDef, "L2F2 — anonymous lambda -> hoisted def", "nums = sorted(items, key=lambda x: x.priority)", "def __key_priority(x):\n    return x.priority\nnums = sorted(items, key=__key_priority)"),
                Info(K.L2F3_CurriedLambdaToDef, "L2F3 — curried lambda -> def", "f = lambda x: lambda y: x + y", "def f(x):\n    return lambda y: x + y"),
                Info(K.L2F4_TypedLambdaToDef, "L2F4 — typed lambda -> def", "# type: (int) -> int\nsq = lambda x: x * x", "def sq(x: int) -> int:\n    return x * x"),
            };
        }

        private static LambdaTransformPatternInfo Info(K kind, string name, string example, string replacement)
        {
            return new LambdaTransformPatternInfo { PatternKind = kind, Name = name, ExampleSource = example, Replacement = replacement };
        }
    }
}
