namespace HelixPythonNamespace.Refactoring.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.ML;
    using HelixPythonNamespace.Refactoring;
    using HelixPythonNamespace.Refactoring.LambdaTransform;
    using K = HelixPythonNamespace.Refactoring.LambdaTransformCandidate.Kind;

    [TestClass]
    public class LambdaTransformTests
    {
        private static bool HasKind(List<LambdaTransformCandidate> cs, K k)
        {
            foreach (LambdaTransformCandidate c in cs) if (c.PatternKind == k) return true;
            return false;
        }

        private static LambdaTransformCandidate GetKind(List<LambdaTransformCandidate> cs, K k)
        {
            foreach (LambdaTransformCandidate c in cs) if (c.PatternKind == k) return c;
            return null;
        }

        private static bool HasIssue(LambdaTransformCandidate c, string code)
        {
            foreach (CandidateIssue i in c.Caveats) if (i.Code == code) return true;
            return false;
        }

        // ---- F2L1 ----

        [TestMethod]
        public void F2L1_MatchesSingleReturn()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("def add(x, y):\n    return x + y\n"), K.F2L1_DefToLambda);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("add = lambda x, y:"));
            Assert.IsTrue(m.ReplacementText.Contains("x + y"));
            Assert.AreEqual(100, m.Confidence);
        }

        [TestMethod]
        public void F2L1_RejectsMultiStatementBody()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("def f(x):\n    y = x + 1\n    return y\n"), K.F2L1_DefToLambda);
            if (m != null) Assert.IsTrue(m.Confidence < 100);
        }

        [TestMethod]
        public void F2L1_RejectsBareReturn()
        {
            Assert.IsFalse(HasKind(LambdaTransformRefactor.Match("def f(x):\n    return\n"), K.F2L1_DefToLambda));
        }

        [TestMethod]
        public void F2L1_RejectsAnnotatedStrict()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("def f(x: int) -> int:\n    return x + 1\n"), K.F2L1_DefToLambda);
            if (m != null) Assert.IsTrue(m.Confidence < 100);
        }

        // ---- F2L2 ----

        [TestMethod]
        public void F2L2_MatchesWithDefaults()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("def f(x, y=1):\n    return x + y\n"), K.F2L2_DefDefaultsToLambda);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("f = lambda x, y=1:"));
            Assert.IsTrue(m.ReplacementText.Contains("x + y"));
        }

        [TestMethod]
        public void F2L2_MatchesWithVarArgs()
        {
            Assert.IsTrue(HasKind(LambdaTransformRefactor.Match("def f(x, *args, **kw):\n    return x\n"), K.F2L2_DefDefaultsToLambda));
        }

        // ---- F2L3 ----

        [TestMethod]
        public void F2L3_MatchesIfElseReturn()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("def f(x):\n    if x > 0:\n        return x\n    else:\n        return -x\n"), K.F2L3_DefIfReturnToTernary);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("f = lambda x:"));
            Assert.IsTrue(m.ReplacementText.Contains(" if "));
            Assert.IsTrue(m.ReplacementText.Contains(" else "));
        }

        [TestMethod]
        public void F2L3_MatchesIfReturnFollowedByReturn()
        {
            Assert.IsTrue(HasKind(LambdaTransformRefactor.Match("def f(x):\n    if x > 0:\n        return x\n    return -x\n"), K.F2L3_DefIfReturnToTernary));
        }

        // ---- L2F1 ----

        [TestMethod]
        public void L2F1_MatchesNamedLambda()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("add = lambda x, y: x + y\n"), K.L2F1_NamedLambdaToDef);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("def add(x, y):"));
            Assert.IsTrue(m.ReplacementText.Contains("return"));
            Assert.IsTrue(m.ReplacementText.Contains("x + y"));
        }

        [TestMethod]
        public void L2F1_RejectsTupleAssignment()
        {
            Assert.IsFalse(HasKind(LambdaTransformRefactor.Match("a, b = 1, 2\n"), K.L2F1_NamedLambdaToDef));
        }

        // ---- L2F2 ----

        [TestMethod]
        public void L2F2_ExtractsKwargLambda()
        {
            Assert.IsTrue(HasKind(LambdaTransformRefactor.Match("nums = sorted(items, key=lambda x: x.priority)\n"), K.L2F2_AnonLambdaToDef));
        }

        [TestMethod]
        public void L2F2_ExtractsPositionalLambda()
        {
            Assert.IsTrue(HasKind(LambdaTransformRefactor.Match("ys = list(map(lambda x: x * 2, xs))\n"), K.L2F2_AnonLambdaToDef));
        }

        // ---- L2F3 ----

        [TestMethod]
        public void L2F3_MatchesCurry()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("f = lambda x: lambda y: x + y\n"), K.L2F3_CurriedLambdaToDef);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("def f(x):"));
            Assert.IsTrue(m.ReplacementText.Contains("lambda y:"));
        }

        // ---- Tier-2 relaxed ----

        [TestMethod]
        public void F2L1Relaxed_DocstringYieldsCaveat()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("def f(x):\n    \"docstring\"\n    return x + 1\n"), K.F2L1_DefToLambda);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasIssue(m, "PYR3001"));
        }

        [TestMethod]
        public void F2L1Relaxed_AnnotationsYieldCaveat()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("def f(x: int) -> int:\n    return x + 1\n"), K.F2L1_DefToLambda);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasIssue(m, "PYR3002"));
        }

        [TestMethod]
        public void F2L1Relaxed_DecoratorYieldsCaveat()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("@cached\ndef f(x):\n    return x + 1\n"), K.F2L1_DefToLambda);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasIssue(m, "PYR3003"));
        }

        [TestMethod]
        public void L2F2Relaxed_MultiSiteYieldsCaveat()
        {
            List<LambdaTransformCandidate> cs = LambdaTransformRefactor.Match("a = sorted(xs, key=lambda x: x.k)\nb = sorted(ys, key=lambda x: x.k)\n");
            bool sawMulti = false;
            foreach (LambdaTransformCandidate c in cs)
                if (c.PatternKind == K.L2F2_AnonLambdaToDef && HasIssue(c, "PYR3004")) sawMulti = true;
            Assert.IsTrue(sawMulti);
        }

        // ---- Ranker / synthetic ----

        [TestMethod]
        public void LambdaRanker_BuildContextDeterministic()
        {
            CompletionRanker rk = new CompletionRanker();
            rk.InitRandom(unchecked((int)0xBEEF1234));
            LambdaTransformRanker lr = new LambdaTransformRanker(rk);
            RankerContext a = lr.BuildContext("def f(x): return x", "def f(x): return x");
            RankerContext b = lr.BuildContext("def f(x): return x", "def f(x): return x");
            Assert.AreEqual(a.ContextBucket, b.ContextBucket);
            Assert.AreEqual(a.TriggerKind, b.TriggerKind);
        }

        [TestMethod]
        public void LambdaRanker_RerankIsStableOnSingleton()
        {
            CompletionRanker rk = new CompletionRanker();
            rk.InitRandom(unchecked((int)0xBEEF1234));
            LambdaTransformRanker lr = new LambdaTransformRanker(rk);
            List<LambdaTransformCandidate> cs = LambdaTransformRefactor.Match("add = lambda x, y: x + y\n", "", lr);
            Assert.IsTrue(cs.Count > 0);
        }

        [TestMethod]
        public void LambdaRanker_RecordAcceptDoesNotCrash()
        {
            CompletionRanker rk = new CompletionRanker();
            rk.InitRandom(unchecked((int)0xBEEF1234));
            LambdaTransformRanker lr = new LambdaTransformRanker(rk);
            List<LambdaTransformCandidate> cs = LambdaTransformRefactor.Match("add = lambda x, y: x + y\n", "", lr);
            Assert.IsTrue(cs.Count > 0);
            lr.RecordAccept(cs[0], cs, "add = lambda x, y: x + y\n", "");
        }

        [TestMethod]
        public void SyntheticLambdaData_GeneratesRequestedBatchSize()
        {
            SyntheticLambdaData gen = new SyntheticLambdaData(unchecked((int)0xBEEF1234));
            List<SyntheticLambdaExample> batch = gen.SampleBatch(64);
            Assert.AreEqual(64, batch.Count);
            foreach (SyntheticLambdaExample ex in batch)
            {
                Assert.IsTrue(ex.Shown.Count >= 3);
                Assert.IsTrue(ex.Shown.Count <= 5);
            }
        }

        // ---- Indent / catalog ----

        [TestMethod]
        public void Indent_FunctionInsideClassStillMatches()
        {
            LambdaTransformCandidate m = GetKind(LambdaTransformRefactor.Match("    def add(x, y):\n        return x + y\n"), K.F2L1_DefToLambda);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("    add = lambda x, y:"));
            Assert.IsTrue(m.ReplacementText.Contains("x + y"));
        }

        [TestMethod]
        public void Catalog_ListsAllEightKinds()
        {
            Assert.AreEqual(8, LambdaTransformRefactor.SupportedPatterns().Count);
        }
    }
}
