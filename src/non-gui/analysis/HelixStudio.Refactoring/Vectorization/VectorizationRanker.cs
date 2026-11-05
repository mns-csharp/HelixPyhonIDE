namespace HelixPythonNamespace.Refactoring.Vectorization
{
    using System.Collections.Generic;
    using HelixPythonNamespace.ML;
    using K = VectorizationCandidate.Kind;

    /// <summary>
    /// Tier-3 optional ML re-ranker over Tier-1/Tier-2 candidates, wrapping a
    /// shared <see cref="CompletionRanker"/>. Port of the C++ <c>VectorizationRanker</c>.
    /// A null ranker makes every method a no-op.
    /// </summary>
    public sealed class VectorizationRanker
    {
        private readonly CompletionRanker _ranker;

        public VectorizationRanker(CompletionRanker ranker)
        {
            _ranker = ranker;
        }

        public static string KindName(K k)
        {
            switch (k)
            {
                case K.F1_ElementwiseBinop: return "F1_ElementwiseBinop";
                case K.F2_ElementwiseNegate: return "F2_ElementwiseNegate";
                case K.F3_ScalarBroadcast: return "F3_ScalarBroadcast";
                case K.F4_MathToNumpy: return "F4_MathToNumpy";
                case K.F5_MapAppend: return "F5_MapAppend";
                case K.F6_FilterAppend: return "F6_FilterAppend";
                case K.F7_FilterMapAppend: return "F7_FilterMapAppend";
                case K.F8_Sum: return "F8_Sum";
                case K.F9_Product: return "F9_Product";
                case K.F10_MinMax: return "F10_MinMax";
                case K.F11_Count: return "F11_Count";
                case K.F12_Any: return "F12_Any";
                case K.F13_All: return "F13_All";
                case K.F14_Where: return "F14_Where";
                case K.F15_Cumsum: return "F15_Cumsum";
                case K.F16_RangeFill: return "F16_RangeFill";
                case K.R1_ElementwiseBinopToLoop: return "R1_ElementwiseBinopToLoop";
                case K.R2_SumToLoop: return "R2_SumToLoop";
                case K.R5_ComprehensionToLoop: return "R5_ComprehensionToLoop";
                case K.R10_MaxToLoop: return "R10_MaxToLoop";
                case K.R14_WhereToLoop: return "R14_WhereToLoop";
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
            bool hasFor = ContainsLine(selectionText, "for ");
            bool hasNumpy = ContainsLine(fileText, "import numpy");
            bool hasMath = ContainsLine(fileText, "import math");

            int lineBucket = numLines >= 16 ? 16 : numLines;
            string ctxKey = lineBucket.ToString() + "|"
                + (hasFor ? 'F' : 'f')
                + (hasNumpy ? 'N' : 'n')
                + (hasMath ? 'M' : 'm');

            RankerContext ctx = new RankerContext();
            ctx.ContextBucket = CompletionRanker.HashToBucket(ctxKey, CompletionRanker.ContextBuckets);
            ctx.TriggerKind = (hasNumpy ? 1 : 0) | (hasMath ? 2 : 0);
            return ctx;
        }

        public RankerCandidate BuildCandidate(VectorizationCandidate c)
        {
            RankerCandidate rc = new RankerCandidate();
            rc.KindBucket = (int)c.PatternKind % CompletionRanker.KindBuckets;
            rc.NameBucket = CompletionRanker.HashToBucket(KindName(c.PatternKind), CompletionRanker.NameBuckets);
            return rc;
        }

        public float Score(VectorizationCandidate c, string selectionText, string fileText)
        {
            if (_ranker == null) return 0.0f;
            RankerContext ctx = BuildContext(selectionText, fileText);
            return _ranker.Score(ctx, BuildCandidate(c));
        }

        public void Rerank(List<VectorizationCandidate> candidates, string selectionText, string fileText)
        {
            if (_ranker == null || candidates.Count < 2) return;
            RankerContext ctx = BuildContext(selectionText, fileText);

            float[] scores = new float[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
                scores[i] = _ranker.Score(ctx, BuildCandidate(candidates[i]));

            int[] order = new int[candidates.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            // Stable sort by descending score (insertion sort preserves ties' input order).
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

            List<VectorizationCandidate> reordered = new List<VectorizationCandidate>(candidates.Count);
            foreach (int idx in order) reordered.Add(candidates[idx]);
            candidates.Clear();
            candidates.AddRange(reordered);
        }

        public void RecordAccept(VectorizationCandidate accepted, IReadOnlyList<VectorizationCandidate> shown, string selectionText, string fileText)
        {
            if (_ranker == null) return;
            RankerContext ctx = BuildContext(selectionText, fileText);
            RankerCandidate acceptedRC = BuildCandidate(accepted);
            List<RankerCandidate> shownRC = new List<RankerCandidate>(shown.Count);
            foreach (VectorizationCandidate c in shown) shownRC.Add(BuildCandidate(c));
            _ranker.Update(ctx, acceptedRC, shownRC, 0.05f);
        }
    }
}
