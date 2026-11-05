namespace HelixPythonNamespace.Refactoring.Vectorization
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.ML;
    using K = VectorizationCandidate.Kind;

    /// <summary>One synthetic accept event for cold-start ranker pretraining.</summary>
    public sealed class SyntheticVectorizationExample
    {
        public RankerContext Ctx;
        public RankerCandidate Accepted;
        public List<RankerCandidate> Shown = new List<RankerCandidate>();
    }

    /// <summary>
    /// Generates weighted-prior synthetic accept events so the ranker has a
    /// sensible cold-start ordering before any real user feedback. Port of the
    /// C++ <c>SyntheticVectorizationData</c>.
    /// </summary>
    public sealed class SyntheticVectorizationData
    {
        private struct PatternWeight
        {
            public K Kind;
            public double Weight;
            public PatternWeight(K kind, double weight) { Kind = kind; Weight = weight; }
        }

        private static readonly PatternWeight[] PriorWeights = new[]
        {
            new PatternWeight(K.F1_ElementwiseBinop, 0.18),
            new PatternWeight(K.F5_MapAppend, 0.15),
            new PatternWeight(K.F8_Sum, 0.12),
            new PatternWeight(K.F2_ElementwiseNegate, 0.05),
            new PatternWeight(K.F3_ScalarBroadcast, 0.08),
            new PatternWeight(K.F6_FilterAppend, 0.06),
            new PatternWeight(K.F7_FilterMapAppend, 0.06),
            new PatternWeight(K.F10_MinMax, 0.05),
            new PatternWeight(K.F4_MathToNumpy, 0.025),
            new PatternWeight(K.F9_Product, 0.020),
            new PatternWeight(K.F11_Count, 0.025),
            new PatternWeight(K.F12_Any, 0.020),
            new PatternWeight(K.F13_All, 0.020),
            new PatternWeight(K.F14_Where, 0.020),
            new PatternWeight(K.F15_Cumsum, 0.020),
            new PatternWeight(K.R1_ElementwiseBinopToLoop, 0.025),
            new PatternWeight(K.R2_SumToLoop, 0.025),
            new PatternWeight(K.R5_ComprehensionToLoop, 0.020),
            new PatternWeight(K.R10_MaxToLoop, 0.015),
            new PatternWeight(K.R14_WhereToLoop, 0.015),
        };

        private readonly Random _rng;

        public SyntheticVectorizationData(int seed)
        {
            _rng = new Random(seed);
        }

        private static RankerCandidate BucketsForKind(K kind)
        {
            RankerCandidate rc = new RankerCandidate();
            rc.KindBucket = (int)kind % CompletionRanker.KindBuckets;
            rc.NameBucket = CompletionRanker.HashToBucket(VectorizationRanker.KindName(kind), CompletionRanker.NameBuckets);
            return rc;
        }

        public K PickWeightedKind()
        {
            double total = 0.0;
            foreach (PatternWeight w in PriorWeights) total += w.Weight;
            double draw = _rng.NextDouble() * total;
            double acc = 0.0;
            foreach (PatternWeight w in PriorWeights)
            {
                acc += w.Weight;
                if (draw <= acc) return w.Kind;
            }
            return PriorWeights[PriorWeights.Length - 1].Kind;
        }

        public SyntheticVectorizationExample Sample()
        {
            SyntheticVectorizationExample ex = new SyntheticVectorizationExample();
            ex.Ctx = new RankerContext();
            ex.Ctx.ContextBucket = _rng.Next(0, CompletionRanker.ContextBuckets);
            ex.Ctx.TriggerKind = _rng.Next(0, 4); // 0..3 inclusive

            K acceptedKind = PickWeightedKind();
            ex.Accepted = BucketsForKind(acceptedKind);

            int n = _rng.Next(2, 5); // 2..4 inclusive
            ex.Shown.Add(ex.Accepted);
            for (int i = 0; i < n; i++)
            {
                K other = acceptedKind;
                for (int tries = 0; tries < 8; tries++)
                {
                    other = PickWeightedKind();
                    if (other != acceptedKind) break;
                }
                ex.Shown.Add(BucketsForKind(other));
            }
            return ex;
        }

        public List<SyntheticVectorizationExample> SampleBatch(int n)
        {
            List<SyntheticVectorizationExample> outList = new List<SyntheticVectorizationExample>(n);
            for (int i = 0; i < n; i++) outList.Add(Sample());
            return outList;
        }
    }
}
