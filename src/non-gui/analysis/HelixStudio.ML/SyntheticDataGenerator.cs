namespace HelixPythonNamespace.ML
{
    using System;
    using System.Collections.Generic;

    public struct SyntheticTrainingExample
    {
        public RankerContext Ctx;
        public RankerCandidate Accepted;
        public List<RankerCandidate> Shown;
    }

    /// <summary>
    /// Procedural generator. Synthesizes plausible (context, accepted, distractors)
    /// triples that bias rankings toward what a Python user would naturally want:
    /// classes after <c>import</c>, methods after <c>.</c>, builtins from cold
    /// context, etc. Port of the C++ <c>helix::ml::SyntheticDataGenerator</c>.
    /// </summary>
    public sealed class SyntheticDataGenerator
    {
        // Five canonical kinds: variable, function, class, module, keyword.
        public enum SyntheticKind
        {
            Variable = 0,
            Function = 1,
            Class = 2,
            Module = 3,
            Keyword = 4,
        }

        public enum SyntheticTrigger
        {
            Manual = 0,
            Dot = 1,
            From = 2,
            Import = 3,
        }

        private const int CtxBuckets = CompletionRanker.ContextBuckets;
        private const int KindBuckets = CompletionRanker.KindBuckets;
        private const int NameBuckets = CompletionRanker.NameBuckets;

        // Probability of each of the 5 synthetic kinds, conditional on the trigger.
        // Each row sums to 1.0. Rows: Manual, Dot, From, Import.
        private static readonly float[][] KindProbsByTrigger = new[]
        {
            new[] { 0.40f, 0.30f, 0.10f, 0.05f, 0.15f }, // Manual
            new[] { 0.20f, 0.65f, 0.10f, 0.00f, 0.05f }, // Dot
            new[] { 0.05f, 0.05f, 0.30f, 0.55f, 0.05f }, // From
            new[] { 0.05f, 0.05f, 0.20f, 0.65f, 0.05f }, // Import
        };

        private readonly Random _rng;

        public SyntheticDataGenerator(int seed = unchecked((int)0xC0FFEEu))
        {
            _rng = new Random(seed);
        }

        public SyntheticTrainingExample Sample()
        {
            SyntheticTrainingExample ex = new SyntheticTrainingExample();
            int triggerIdx = RandBucket(4);
            ex.Ctx.ContextBucket = RandBucket(CtxBuckets);
            ex.Ctx.TriggerKind = triggerIdx;

            int acceptedKind = SampleKind(triggerIdx);
            ex.Accepted.KindBucket = CompletionRanker.HashToBucket(acceptedKind, KindBuckets);
            ex.Accepted.NameBucket = RandBucket(NameBuckets);

            // 4–8 distractors plus the accepted entry.
            int n = _rng.Next(4, 9); // inclusive 4..8
            ex.Shown = new List<RankerCandidate>(n + 1);
            ex.Shown.Add(ex.Accepted);
            for (int i = 0; i < n; i++)
            {
                int k = SampleKind(triggerIdx);
                // 70% pick a "wrong" kind for learning signal; 30% same kind, different name.
                if (_rng.NextDouble() > 0.30)
                {
                    int alt = (k + 1 + RandBucket(4)) % 5;
                    k = alt;
                }
                RankerCandidate c = new RankerCandidate();
                c.KindBucket = CompletionRanker.HashToBucket(k, KindBuckets);
                c.NameBucket = RandBucket(NameBuckets);
                ex.Shown.Add(c);
            }
            return ex;
        }

        public List<SyntheticTrainingExample> SampleBatch(int n)
        {
            List<SyntheticTrainingExample> outList = new List<SyntheticTrainingExample>(Math.Max(0, n));
            for (int i = 0; i < n; i++)
            {
                outList.Add(Sample());
            }
            return outList;
        }

        private int RandBucket(int max)
        {
            return _rng.Next(0, max);
        }

        private int SampleKind(int triggerIdx)
        {
            float r = (float)_rng.NextDouble();
            float acc = 0.0f;
            for (int k = 0; k < 5; k++)
            {
                acc += KindProbsByTrigger[triggerIdx][k];
                if (r <= acc)
                {
                    return k;
                }
            }
            return 4;
        }
    }
}
