namespace HelixPythonNamespace.Refactoring.LambdaTransform
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.ML;
    using K = LambdaTransformCandidate.Kind;

    /// <summary>One synthetic accept event for cold-start lambda-ranker pretraining.</summary>
    public sealed class SyntheticLambdaExample
    {
        public RankerContext Ctx;
        public RankerCandidate Accepted;
        public List<RankerCandidate> Shown = new List<RankerCandidate>();
    }

    /// <summary>Weighted-prior synthetic accept events for the lambda ranker. Port of the C++ <c>SyntheticLambdaData</c>.</summary>
    public sealed class SyntheticLambdaData
    {
        private struct PatternWeight
        {
            public K Kind;
            public double Weight;
            public PatternWeight(K kind, double weight) { Kind = kind; Weight = weight; }
        }

        private static readonly PatternWeight[] PriorWeights = new[]
        {
            new PatternWeight(K.F2L1_DefToLambda, 0.22),
            new PatternWeight(K.F2L2_DefDefaultsToLambda, 0.10),
            new PatternWeight(K.F2L3_DefIfReturnToTernary, 0.08),
            new PatternWeight(K.F2L4_NestedDefToInlineLambda, 0.05),
            new PatternWeight(K.L2F1_NamedLambdaToDef, 0.20),
            new PatternWeight(K.L2F2_AnonLambdaToDef, 0.20),
            new PatternWeight(K.L2F3_CurriedLambdaToDef, 0.08),
            new PatternWeight(K.L2F4_TypedLambdaToDef, 0.07),
        };

        private readonly Random _rng;

        public SyntheticLambdaData(int seed)
        {
            _rng = new Random(seed);
        }

        private static RankerCandidate BucketsForKind(K kind)
        {
            RankerCandidate rc = new RankerCandidate();
            rc.KindBucket = (int)kind % CompletionRanker.KindBuckets;
            rc.NameBucket = CompletionRanker.HashToBucket(LambdaTransformRanker.KindName(kind), CompletionRanker.NameBuckets);
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

        public SyntheticLambdaExample Sample()
        {
            SyntheticLambdaExample ex = new SyntheticLambdaExample();
            ex.Ctx = new RankerContext();
            ex.Ctx.ContextBucket = _rng.Next(0, CompletionRanker.ContextBuckets);
            ex.Ctx.TriggerKind = _rng.Next(0, 4);

            K acceptedKind = PickWeightedKind();
            ex.Accepted = BucketsForKind(acceptedKind);

            int n = _rng.Next(2, 5);
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

        public List<SyntheticLambdaExample> SampleBatch(int n)
        {
            List<SyntheticLambdaExample> outList = new List<SyntheticLambdaExample>(n);
            for (int i = 0; i < n; i++) outList.Add(Sample());
            return outList;
        }
    }
}
