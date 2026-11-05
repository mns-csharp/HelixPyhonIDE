namespace HelixPythonNamespace.ML
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Embedding+linear scorer:
    ///   v = ctxEmb[ctxBucket] + kindEmb[kindBucket] + nameEmb[nameBucket] + triggerEmb[triggerKind]
    ///   score = dot(v, w) + b
    /// A tiny model (~70K floats) built on the <see cref="Parameter"/> primitives.
    /// Online updates use a pairwise margin loss: the accepted candidate should
    /// outscore the rest of the displayed list. Port of the C++
    /// <c>helix::ml::CompletionRanker</c>.
    /// </summary>
    public sealed class CompletionRanker
    {
        public const int EmbDim = 16;
        public const int ContextBuckets = 1024;
        public const int KindBuckets = 16;
        public const int NameBuckets = 4096;
        public const int TriggerBuckets = 8;

        // Joint (trigger, kind) cross feature. The additive sum of independent embeddings
        // cannot represent "kind K is good ONLY for trigger T" (different triggers prefer
        // different kinds), and — worse — ctx/trigger cancel in the margin loss so the
        // trigger embedding never even trains. This cross row (indexed by trigger*KindBuckets
        // + kind) DEPENDS on the candidate's kind, so it differs between accepted and
        // distractor and receives real gradient, letting the ranker learn the interaction.
        public const int CrossBuckets = TriggerBuckets * KindBuckets;

        private Parameter _ctxEmb;     // [ContextBuckets, EmbDim]
        private Parameter _kindEmb;    // [KindBuckets, EmbDim]
        private Parameter _nameEmb;    // [NameBuckets, EmbDim]
        private Parameter _triggerEmb; // [TriggerBuckets, EmbDim]
        private Parameter _crossEmb;   // [CrossBuckets, EmbDim] — (trigger, kind) interaction
        private Parameter _w;          // [EmbDim]
        private Parameter _b;          // [1]

        public CompletionRanker()
        {
            _ctxEmb = new Parameter(Tensor.Zeros(new[] { ContextBuckets, EmbDim }));
            _kindEmb = new Parameter(Tensor.Zeros(new[] { KindBuckets, EmbDim }));
            _nameEmb = new Parameter(Tensor.Zeros(new[] { NameBuckets, EmbDim }));
            _triggerEmb = new Parameter(Tensor.Zeros(new[] { TriggerBuckets, EmbDim }));
            _crossEmb = new Parameter(Tensor.Zeros(new[] { CrossBuckets, EmbDim }));
            _w = new Parameter(Tensor.Zeros(new[] { EmbDim }));
            _b = new Parameter(Tensor.Zeros(new[] { 1 }));
        }

        public void InitRandom(int seed)
        {
            Random rng = new Random(seed);
            _ctxEmb = new Parameter(Tensor.Random(new[] { ContextBuckets, EmbDim }, 0.05f, rng));
            _kindEmb = new Parameter(Tensor.Random(new[] { KindBuckets, EmbDim }, 0.05f, rng));
            _nameEmb = new Parameter(Tensor.Random(new[] { NameBuckets, EmbDim }, 0.02f, rng));
            _triggerEmb = new Parameter(Tensor.Random(new[] { TriggerBuckets, EmbDim }, 0.05f, rng));
            _crossEmb = new Parameter(Tensor.Random(new[] { CrossBuckets, EmbDim }, 0.05f, rng));
            _w = new Parameter(Tensor.Random(new[] { EmbDim }, 0.1f, rng));
            _b = new Parameter(Tensor.Zeros(new[] { 1 }));
        }

        public float Score(RankerContext ctx, RankerCandidate cand)
        {
            float[] v = new float[EmbDim];
            Combine(ctx, cand, v);
            float[] w = _w.Data.Data;
            float s = _b.Data[0];
            for (int i = 0; i < EmbDim; i++)
            {
                s += v[i] * w[i];
            }
            return s;
        }

        /// <summary>
        /// One SGD step on a margin loss. <paramref name="accepted"/> should rank higher
        /// than each entry in <paramref name="shown"/> (excluding itself). Returns the
        /// average loss before the update, for diagnostics.
        /// </summary>
        public float Update(RankerContext ctx, RankerCandidate accepted, IList<RankerCandidate> shown, float lr, float margin = 0.5f)
        {
            if (shown == null || shown.Count == 0)
            {
                return 0.0f;
            }

            float[] vPos = new float[EmbDim];
            Combine(ctx, accepted, vPos);
            float[] w = _w.Data.Data;
            float scorePos = _b.Data[0];
            for (int i = 0; i < EmbDim; i++)
            {
                scorePos += vPos[i] * w[i];
            }

            int posKind = Clamp(accepted.KindBucket, 0, KindBuckets - 1);
            int posName = Clamp(accepted.NameBucket, 0, NameBuckets - 1);
            int posCross = CrossRow(ctx, accepted);

            float[] gKind = _kindEmb.Grad.Data;
            float[] gName = _nameEmb.Grad.Data;
            float[] gCross = _crossEmb.Grad.Data;
            float[] gW = _w.Grad.Data;

            int contributingPairs = 0;
            float lossSum = 0.0f;
            float[] vNeg = new float[EmbDim];
            foreach (RankerCandidate neg in shown)
            {
                if (neg.KindBucket == accepted.KindBucket && neg.NameBucket == accepted.NameBucket)
                {
                    continue; // skip the accepted candidate itself if present in shown
                }
                Combine(ctx, neg, vNeg);
                float scoreNeg = _b.Data[0];
                for (int i = 0; i < EmbDim; i++)
                {
                    scoreNeg += vNeg[i] * w[i];
                }

                float diff = scoreNeg - scorePos + margin;
                lossSum += SoftPlus(diff);
                float dDiff = Sigmoid(diff); // d loss / d diff

                for (int i = 0; i < EmbDim; i++)
                {
                    gW[i] += dDiff * (vNeg[i] - vPos[i]);
                }

                int negKind = Clamp(neg.KindBucket, 0, KindBuckets - 1);
                int negName = Clamp(neg.NameBucket, 0, NameBuckets - 1);
                int negCross = CrossRow(ctx, neg);
                for (int i = 0; i < EmbDim; i++)
                {
                    float gPos = -dDiff * w[i];
                    float gNeg = +dDiff * w[i];
                    gKind[posKind * EmbDim + i] += gPos;
                    gName[posName * EmbDim + i] += gPos;
                    gCross[posCross * EmbDim + i] += gPos;
                    gKind[negKind * EmbDim + i] += gNeg;
                    gName[negName * EmbDim + i] += gNeg;
                    gCross[negCross * EmbDim + i] += gNeg;
                    // ctx + trigger appear in both vPos and vNeg with the same row,
                    // so their net gradient per pair is 0 — left untouched. The (trigger,
                    // kind) CROSS row differs whenever kinds differ, so it does train.
                }
                contributingPairs++;
            }
            if (contributingPairs == 0)
            {
                return 0.0f;
            }
            float scale = lr / contributingPairs;

            ApplyVec(_kindEmb, posKind, scale);
            ApplyVec(_nameEmb, posName, scale);
            ApplyVec(_crossEmb, posCross, scale);
            foreach (RankerCandidate neg in shown)
            {
                if (neg.KindBucket == accepted.KindBucket && neg.NameBucket == accepted.NameBucket)
                {
                    continue;
                }
                ApplyVec(_kindEmb, Clamp(neg.KindBucket, 0, KindBuckets - 1), scale);
                ApplyVec(_nameEmb, Clamp(neg.NameBucket, 0, NameBuckets - 1), scale);
                ApplyVec(_crossEmb, CrossRow(ctx, neg), scale);
            }
            ApplyW(_w, scale);

            _kindEmb.ZeroGrad();
            _nameEmb.ZeroGrad();
            _crossEmb.ZeroGrad();
            _w.ZeroGrad();
            _b.ZeroGrad();
            _ctxEmb.ZeroGrad();
            _triggerEmb.ZeroGrad();

            return lossSum / contributingPairs;
        }

        /// <summary>FNV-1a 32-bit hash of a string, mapped into <paramref name="buckets"/>.</summary>
        public static int HashToBucket(string s, int buckets)
        {
            if (buckets <= 0)
            {
                return 0;
            }
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                byte c = (byte)s[i];
                h ^= c;
                h *= 16777619u;
            }
            return (int)(h % (uint)buckets);
        }

        public static int HashToBucket(int kindEnum, int buckets)
        {
            if (buckets <= 0)
            {
                return 0;
            }
            uint h = (uint)kindEnum * 2654435761u;
            return (int)(h % (uint)buckets);
        }

        public bool Save(string path)
        {
            return TensorSerializer.SaveParameters(path, Parameters());
        }

        public bool Load(string path)
        {
            return TensorSerializer.LoadParameters(path, Parameters());
        }

        public List<Parameter> Parameters()
        {
            return new List<Parameter> { _ctxEmb, _kindEmb, _nameEmb, _triggerEmb, _crossEmb, _w, _b };
        }

        private void Combine(RankerContext ctx, RankerCandidate cand, float[] out16)
        {
            int ctxRow = Clamp(ctx.ContextBucket, 0, ContextBuckets - 1);
            int kindRow = Clamp(cand.KindBucket, 0, KindBuckets - 1);
            int nameRow = Clamp(cand.NameBucket, 0, NameBuckets - 1);
            int trigRow = Clamp(ctx.TriggerKind, 0, TriggerBuckets - 1);
            int crossRow = trigRow * KindBuckets + kindRow;

            float[] a = _ctxEmb.Data.Data;
            float[] k = _kindEmb.Data.Data;
            float[] n = _nameEmb.Data.Data;
            float[] t = _triggerEmb.Data.Data;
            float[] x = _crossEmb.Data.Data;
            for (int i = 0; i < EmbDim; i++)
            {
                out16[i] = a[ctxRow * EmbDim + i] + k[kindRow * EmbDim + i]
                    + n[nameRow * EmbDim + i] + t[trigRow * EmbDim + i]
                    + x[crossRow * EmbDim + i];
            }
        }

        private static int CrossRow(RankerContext ctx, RankerCandidate cand)
        {
            int kindRow = Clamp(cand.KindBucket, 0, KindBuckets - 1);
            int trigRow = Clamp(ctx.TriggerKind, 0, TriggerBuckets - 1);
            return trigRow * KindBuckets + kindRow;
        }

        private static void ApplyVec(Parameter p, int row, float scale)
        {
            if (row < 0)
            {
                return;
            }
            float[] d = p.Data.Data;
            float[] g = p.Grad.Data;
            for (int i = 0; i < EmbDim; i++)
            {
                d[row * EmbDim + i] -= scale * g[row * EmbDim + i];
            }
        }

        private static void ApplyW(Parameter p, float scale)
        {
            float[] d = p.Data.Data;
            float[] g = p.Grad.Data;
            for (int i = 0; i < d.Length; i++)
            {
                d[i] -= scale * g[i];
            }
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo)
            {
                return lo;
            }
            return v > hi ? hi : v;
        }

        private static float Sigmoid(float x)
        {
            if (x >= 0.0f)
            {
                float z = (float)Math.Exp(-x);
                return 1.0f / (1.0f + z);
            }
            else
            {
                float z = (float)Math.Exp(x);
                return z / (1.0f + z);
            }
        }

        private static float SoftPlus(float x)
        {
            // log(1 + exp(x)) — numerically stable.
            if (x > 20.0f)
            {
                return x;
            }
            if (x < -20.0f)
            {
                return (float)Math.Exp(x);
            }
            return (float)Math.Log(1.0 + Math.Exp(x));
        }
    }
}
