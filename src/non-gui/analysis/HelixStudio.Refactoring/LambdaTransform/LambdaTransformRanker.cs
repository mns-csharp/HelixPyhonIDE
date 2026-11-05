namespace HelixPythonNamespace.Refactoring.LambdaTransform
{
    using System.Collections.Generic;
    using HelixPythonNamespace.ML;
    using K = LambdaTransformCandidate.Kind;

    /// <summary>Tier-3 optional ML re-ranker for lambda-transform candidates. Port of the C++ <c>LambdaTransformRanker</c>.</summary>
    public sealed class LambdaTransformRanker
    {
        private readonly CompletionRanker _ranker;

        public LambdaTransformRanker(CompletionRanker ranker)
        {
            _ranker = ranker;
        }

        public static string KindName(K k)
        {
            switch (k)
            {
                case K.F2L1_DefToLambda: return "F2L1_DefToLambda";
                case K.F2L2_DefDefaultsToLambda: return "F2L2_DefDefaultsToLambda";
                case K.F2L3_DefIfReturnToTernary: return "F2L3_DefIfReturnToTernary";
                case K.F2L4_NestedDefToInlineLambda: return "F2L4_NestedDefToInlineLambda";
                case K.L2F1_NamedLambdaToDef: return "L2F1_NamedLambdaToDef";
                case K.L2F2_AnonLambdaToDef: return "L2F2_AnonLambdaToDef";
                case K.L2F3_CurriedLambdaToDef: return "L2F3_CurriedLambdaToDef";
                case K.L2F4_TypedLambdaToDef: return "L2F4_TypedLambdaToDef";
            }
            return "unknown";
        }

        private static bool ContainsLine(string haystack, string needle)
        {
            return haystack.IndexOf(needle, System.StringComparison.Ordinal) >= 0;
        }

        public RankerContext BuildContext(string selectionText, string fileText)
        {
            int numLines = 1;
            foreach (char c in selectionText) if (c == '\n') numLines++;
            bool hasDef = ContainsLine(selectionText, "def ");
            bool hasLambda = ContainsLine(selectionText, "lambda");
            bool hasReturn = ContainsLine(selectionText, "return");

            int lineBucket = numLines >= 16 ? 16 : numLines;
            string ctxKey = lineBucket.ToString() + "|"
                + (hasDef ? 'D' : 'd')
                + (hasLambda ? 'L' : 'l')
                + (hasReturn ? 'R' : 'r');

            RankerContext ctx = new RankerContext();
            ctx.ContextBucket = CompletionRanker.HashToBucket(ctxKey, CompletionRanker.ContextBuckets);
            ctx.TriggerKind = (ContainsLine(fileText, "def ") ? 1 : 0) | (ContainsLine(fileText, "lambda") ? 2 : 0);
            return ctx;
        }

        public RankerCandidate BuildCandidate(LambdaTransformCandidate c)
        {
            RankerCandidate rc = new RankerCandidate();
            rc.KindBucket = (int)c.PatternKind % CompletionRanker.KindBuckets;
            rc.NameBucket = CompletionRanker.HashToBucket(KindName(c.PatternKind), CompletionRanker.NameBuckets);
            return rc;
        }

        public float Score(LambdaTransformCandidate c, string selectionText, string fileText)
        {
            if (_ranker == null) return 0.0f;
            RankerContext ctx = BuildContext(selectionText, fileText);
            return _ranker.Score(ctx, BuildCandidate(c));
        }

        public void Rerank(List<LambdaTransformCandidate> candidates, string selectionText, string fileText)
        {
            if (_ranker == null || candidates.Count < 2) return;
            RankerContext ctx = BuildContext(selectionText, fileText);

            float[] scores = new float[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
                scores[i] = _ranker.Score(ctx, BuildCandidate(candidates[i]));

            int[] order = new int[candidates.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = 1; i < order.Length; i++)
            {
                int key = order[i];
                int j = i - 1;
                while (j >= 0 && scores[order[j]] < scores[key])
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }

            List<LambdaTransformCandidate> reordered = new List<LambdaTransformCandidate>(candidates.Count);
            foreach (int idx in order) reordered.Add(candidates[idx]);
            candidates.Clear();
            candidates.AddRange(reordered);
        }

        public void RecordAccept(LambdaTransformCandidate accepted, IReadOnlyList<LambdaTransformCandidate> shown, string selectionText, string fileText)
        {
            if (_ranker == null) return;
            RankerContext ctx = BuildContext(selectionText, fileText);
            RankerCandidate acceptedRC = BuildCandidate(accepted);
            List<RankerCandidate> shownRC = new List<RankerCandidate>(shown.Count);
            foreach (LambdaTransformCandidate c in shown) shownRC.Add(BuildCandidate(c));
            _ranker.Update(ctx, acceptedRC, shownRC, 0.05f);
        }
    }
}
