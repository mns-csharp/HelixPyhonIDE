namespace HelixPythonNamespace.ML.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.ML;

    [TestClass]
    public class TensorTests
    {
        [TestMethod]
        public void ZerosHasRightShapeAndElements()
        {
            Tensor t = Tensor.Zeros(new[] { 3, 4 });
            Assert.AreEqual(2, t.Rank);
            Assert.AreEqual(3, t.Dim(0));
            Assert.AreEqual(4, t.Dim(1));
            Assert.AreEqual(12, t.NumElements());
            Assert.AreEqual(0.0f, t[0]);
            Assert.AreEqual(0.0f, t[11]);
        }

        [TestMethod]
        public void FillSetsAllElements()
        {
            Tensor t = new Tensor(new[] { 5 });
            t.Fill(2.5f);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(2.5f, t[i]);
            }
            t.ZeroFill();
            Assert.AreEqual(0.0f, t[2]);
        }

        [TestMethod]
        public void RandomFillsWithFiniteValues()
        {
            Tensor t = Tensor.Random(new[] { 100 }, 0.1f, new System.Random(1));
            float sum = 0f;
            for (int i = 0; i < 100; i++)
            {
                Assert.IsFalse(float.IsNaN(t[i]));
                sum += t[i];
            }
            // Mean near zero for a 0-centered Gaussian (loose bound).
            Assert.IsTrue(System.Math.Abs(sum / 100f) < 0.1f);
        }
    }

    [TestClass]
    public class SgdTests
    {
        [TestMethod]
        public void StepMovesWeightsAlongNegativeGradient()
        {
            Parameter p = new Parameter(Tensor.Zeros(new[] { 3 }));
            p.Grad[0] = 1.0f;
            p.Grad[1] = -2.0f;
            p.Grad[2] = 0.5f;
            Sgd opt = new Sgd(new[] { p }, 0.1f);
            opt.Step();
            Assert.AreEqual(-0.1f, p.Data[0], 1e-6f);
            Assert.AreEqual(0.2f, p.Data[1], 1e-6f);
            Assert.AreEqual(-0.05f, p.Data[2], 1e-6f);
        }

        [TestMethod]
        public void ZeroGradClearsGradients()
        {
            Parameter p = new Parameter(Tensor.Zeros(new[] { 2 }));
            p.Grad[0] = 5f;
            Sgd opt = new Sgd(new[] { p }, 0.1f);
            opt.ZeroGrad();
            Assert.AreEqual(0f, p.Grad[0]);
        }
    }

    [TestClass]
    public class HashTests
    {
        [TestMethod]
        public void StringHashIsDeterministicAndInRange()
        {
            int a = CompletionRanker.HashToBucket("print", CompletionRanker.NameBuckets);
            int b = CompletionRanker.HashToBucket("print", CompletionRanker.NameBuckets);
            Assert.AreEqual(a, b);
            Assert.IsTrue(a >= 0 && a < CompletionRanker.NameBuckets);
            Assert.AreNotEqual(
                CompletionRanker.HashToBucket("print", CompletionRanker.NameBuckets),
                CompletionRanker.HashToBucket("range", CompletionRanker.NameBuckets));
        }

        [TestMethod]
        public void KnownFnv1aValue()
        {
            // FNV-1a of "a" = 0x e40c292c; mod a large bucket count is stable.
            int bucket = CompletionRanker.HashToBucket("a", 1 << 30);
            Assert.AreEqual((int)(0xe40c292cu % (1u << 30)), bucket);
        }
    }

    [TestClass]
    public class CompletionRankerTests
    {
        [TestMethod]
        public void ScoreOfZeroModelIsZero()
        {
            CompletionRanker r = new CompletionRanker();
            RankerContext ctx = new RankerContext { ContextBucket = 1, TriggerKind = 1 };
            RankerCandidate c = new RankerCandidate { KindBucket = 2, NameBucket = 3 };
            Assert.AreEqual(0.0f, r.Score(ctx, c), 1e-6f);
        }

        [TestMethod]
        public void UpdatePushesAcceptedAboveDistractor()
        {
            CompletionRanker r = new CompletionRanker();
            r.InitRandom(7);
            RankerContext ctx = new RankerContext { ContextBucket = 10, TriggerKind = 1 };
            RankerCandidate accepted = new RankerCandidate { KindBucket = 1, NameBucket = 100 };
            RankerCandidate distractor = new RankerCandidate { KindBucket = 5, NameBucket = 200 };
            List<RankerCandidate> shown = new List<RankerCandidate> { accepted, distractor };

            // Many SGD steps on the same example should drive accepted above distractor.
            for (int i = 0; i < 200; i++)
            {
                r.Update(ctx, accepted, shown, 0.5f);
            }
            Assert.IsTrue(r.Score(ctx, accepted) > r.Score(ctx, distractor),
                "accepted should outscore the distractor after training");
        }

        [TestMethod]
        public void RankerLearnsTriggerConditionalKindOnSyntheticData()
        {
            // Regression (iter 56): the additive model (ctx+kind+name+trigger embeddings,
            // no interaction) could NOT learn the synthetic ranking task — the accepted
            // kind is conditioned on the trigger (kind 3 good for Import, bad for Dot), an
            // interaction an additive model can't represent, and ctx/trigger cancel in the
            // margin loss so trigger never even trained. Accuracy stayed near chance (~0.20,
            // chance ~0.14). Adding a (trigger,kind) cross feature lets it learn. This test
            // asserts trained accuracy@1 clears the additive plateau.
            List<SyntheticTrainingExample> test = new SyntheticDataGenerator(12345).SampleBatch(500);
            CompletionRanker r = new CompletionRanker();
            r.InitRandom(7);
            double before = Accuracy(r, test);
            SyntheticDataGenerator gen = new SyntheticDataGenerator(999);
            for (int i = 0; i < 20000; i++)
            {
                SyntheticTrainingExample ex = gen.Sample();
                r.Update(ex.Ctx, ex.Accepted, ex.Shown, 0.03f);
            }
            double after = Accuracy(r, test);
            Assert.IsTrue(after > 0.25, "trained acc@1 (" + after + ") should clear the ~0.20 additive plateau");
            Assert.IsTrue(after > before + 0.05, "training should raise acc@1 (" + before + " -> " + after + ")");
        }

        private static double Accuracy(CompletionRanker r, List<SyntheticTrainingExample> set)
        {
            int correct = 0;
            foreach (SyntheticTrainingExample ex in set)
            {
                float sa = r.Score(ex.Ctx, ex.Accepted);
                bool beaten = false;
                foreach (RankerCandidate c in ex.Shown)
                {
                    if (r.Score(ex.Ctx, c) > sa + 1e-6f) { beaten = true; break; }
                }
                if (!beaten) correct++;
            }
            return (double)correct / set.Count;
        }

        [TestMethod]
        public void EmptyShownIsNoOp()
        {
            CompletionRanker r = new CompletionRanker();
            float loss = r.Update(
                new RankerContext(), new RankerCandidate(),
                new List<RankerCandidate>(), 0.5f);
            Assert.AreEqual(0.0f, loss);
        }

        [TestMethod]
        public void SaveLoadRoundTripsParameters()
        {
            CompletionRanker a = new CompletionRanker();
            a.InitRandom(3);
            RankerContext ctx = new RankerContext { ContextBucket = 4, TriggerKind = 2 };
            RankerCandidate cand = new RankerCandidate { KindBucket = 6, NameBucket = 9 };
            float before = a.Score(ctx, cand);

            string path = Path.Combine(Path.GetTempPath(), "helix_ranker_test.bin");
            try
            {
                Assert.IsTrue(a.Save(path));
                CompletionRanker b = new CompletionRanker();
                Assert.IsTrue(b.Load(path));
                Assert.AreEqual(before, b.Score(ctx, cand), 1e-6f);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [TestClass]
    public class SyntheticDataTests
    {
        [TestMethod]
        public void BatchHasRequestedSizeAndAcceptedInShown()
        {
            SyntheticDataGenerator gen = new SyntheticDataGenerator(123);
            List<SyntheticTrainingExample> batch = gen.SampleBatch(20);
            Assert.AreEqual(20, batch.Count);
            foreach (SyntheticTrainingExample ex in batch)
            {
                Assert.IsTrue(ex.Shown.Count >= 5, "accepted + 4..8 distractors");
                Assert.IsTrue(ex.Shown.Contains(ex.Accepted));
                Assert.IsTrue(ex.Ctx.TriggerKind >= 0 && ex.Ctx.TriggerKind < 4);
            }
        }

        [TestMethod]
        public void TrainingOnSyntheticDataReducesAverageLoss()
        {
            CompletionRanker r = new CompletionRanker();
            r.InitRandom(11);
            SyntheticDataGenerator gen = new SyntheticDataGenerator(42);
            List<SyntheticTrainingExample> batch = gen.SampleBatch(500);

            float Avg()
            {
                float total = 0f;
                int n = 0;
                foreach (SyntheticTrainingExample ex in batch)
                {
                    total += r.Update(ex.Ctx, ex.Accepted, ex.Shown, 0.0f); // lr=0 => measure loss, no update
                    n++;
                }
                return total / n;
            }

            float lossBefore = Avg();
            for (int epoch = 0; epoch < 5; epoch++)
            {
                foreach (SyntheticTrainingExample ex in batch)
                {
                    r.Update(ex.Ctx, ex.Accepted, ex.Shown, 0.2f);
                }
            }
            float lossAfter = Avg();
            Assert.IsTrue(lossAfter < lossBefore,
                "average margin loss should drop after training (before=" + lossBefore + ", after=" + lossAfter + ")");
        }
    }
}
