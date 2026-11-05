namespace HelixPythonNamespace.Refactoring.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.ML;
    using HelixPythonNamespace.Refactoring;
    using HelixPythonNamespace.Refactoring.Vectorization;
    using K = HelixPythonNamespace.Refactoring.VectorizationCandidate.Kind;

    [TestClass]
    public class VectorizationTests
    {
        private static bool HasKind(List<VectorizationCandidate> cs, K k)
        {
            foreach (VectorizationCandidate c in cs) if (c.PatternKind == k) return true;
            return false;
        }

        private static VectorizationCandidate GetKind(List<VectorizationCandidate> cs, K k)
        {
            foreach (VectorizationCandidate c in cs) if (c.PatternKind == k) return c;
            return null;
        }

        private static bool HasCaveatCode(VectorizationCandidate c, string code)
        {
            foreach (CandidateIssue i in c.Caveats) if (i.Code == code) return true;
            return false;
        }

        // ---- F1 ----

        [TestMethod]
        public void F1_MatchesAddition()
        {
            List<VectorizationCandidate> cs = VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + b[i]\n");
            VectorizationCandidate m = GetKind(cs, K.F1_ElementwiseBinop);
            Assert.IsNotNull(m);
            Assert.AreEqual("c = np.asarray(a) + np.asarray(b)", m.ReplacementText);
            Assert.AreEqual(1, m.RequiredImports.Count);
            Assert.AreEqual("import numpy as np", m.RequiredImports[0]);
        }

        [TestMethod]
        public void F1_MatchesMultiplication()
        {
            Assert.IsTrue(HasKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] * b[i]\n"), K.F1_ElementwiseBinop));
        }

        [TestMethod]
        public void F1_MismatchedIndexNotStrict()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i+1] = a[i] + b[i]\n"), K.F1_ElementwiseBinop);
            if (m != null) Assert.IsTrue(m.Confidence < 100);
        }

        [TestMethod]
        public void F1_RejectsMultiStatementBody()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + b[i]\n    print(c[i])\n"), K.F1_ElementwiseBinop));
        }

        [TestMethod]
        public void F1_RejectsNonRangeLenIter()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("for i in range(10):\n    c[i] = a[i] + b[i]\n"), K.F1_ElementwiseBinop));
        }

        [TestMethod]
        public void F1_RejectsForElseClause()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + b[i]\nelse:\n    pass\n"), K.F1_ElementwiseBinop));
        }

        // ---- F2 / F3 / F4 ----

        [TestMethod]
        public void F2_MatchesNegate()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = -a[i]\n"), K.F2_ElementwiseNegate);
            Assert.IsNotNull(m);
            Assert.AreEqual("c = -np.asarray(a)", m.ReplacementText);
        }

        [TestMethod]
        public void F3_MatchesScalarOnRight()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] * 2\n"), K.F3_ScalarBroadcast);
            Assert.IsNotNull(m);
            Assert.AreEqual("c = np.asarray(a) * 2", m.ReplacementText);
        }

        [TestMethod]
        public void F3_MatchesScalarOnLeft()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = 2 * a[i]\n"), K.F3_ScalarBroadcast);
            Assert.IsNotNull(m);
            Assert.AreEqual("c = 2 * np.asarray(a)", m.ReplacementText);
        }

        [TestMethod]
        public void F3_MatchesNamedScalar()
        {
            Assert.IsTrue(HasKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + bias\n"), K.F3_ScalarBroadcast));
        }

        [TestMethod]
        public void F4_MatchesMathSin()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = math.sin(a[i])\n"), K.F4_MathToNumpy);
            Assert.IsNotNull(m);
            Assert.AreEqual("c = np.sin(np.asarray(a))", m.ReplacementText);
        }

        [TestMethod]
        public void F4_RejectsUnknownMathFunction()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = math.gamma(a[i])\n"), K.F4_MathToNumpy));
        }

        // ---- F5 / F6 / F7 ----

        [TestMethod]
        public void F5_MatchesMapAppend()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("result = []\nfor x in xs:\n    result.append(f(x))\n"), K.F5_MapAppend);
            Assert.IsNotNull(m);
            Assert.AreEqual("result = [f(x) for x in xs]", m.ReplacementText);
        }

        [TestMethod]
        public void F5_RejectsAppendNotOnResult()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("result = []\nfor x in xs:\n    other.append(f(x))\n"), K.F5_MapAppend));
        }

        [TestMethod]
        public void F6_MatchesPureFilter()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("result = []\nfor x in xs:\n    if x > 0:\n        result.append(x)\n"), K.F6_FilterAppend);
            Assert.IsNotNull(m);
            Assert.AreEqual("result = [x for x in xs if (x > 0)]", m.ReplacementText);
        }

        [TestMethod]
        public void F7_MatchesFilterMap()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("result = []\nfor x in xs:\n    if x > 0:\n        result.append(f(x))\n"), K.F7_FilterMapAppend);
            Assert.IsNotNull(m);
            Assert.AreEqual("result = [f(x) for x in xs if (x > 0)]", m.ReplacementText);
        }

        // ---- F8 / F9 / F10 / F11 ----

        [TestMethod]
        public void F8_MatchesIntZeroInit()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("total = 0\nfor x in xs:\n    total += x\n"), K.F8_Sum);
            Assert.IsNotNull(m);
            Assert.AreEqual("total = sum(xs)", m.ReplacementText);
        }

        [TestMethod]
        public void F8_MatchesNonZeroInitAddsStart()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("total = 10\nfor x in xs:\n    total += x\n"), K.F8_Sum);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("sum(xs, 10)"));
        }

        [TestMethod]
        public void F9_MatchesProduct()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("prod = 1\nfor x in xs:\n    prod *= x\n"), K.F9_Product);
            Assert.IsNotNull(m);
            Assert.AreEqual("prod = math.prod(xs)", m.ReplacementText);
        }

        [TestMethod]
        public void F9_ZeroInitNotStrict()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("prod = 0\nfor x in xs:\n    prod *= x\n"), K.F9_Product);
            if (m != null) Assert.IsTrue(m.Confidence < 100);
        }

        [TestMethod]
        public void F10_MatchesMaxWithTailSlice()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("best = xs[0]\nfor x in xs[1:]:\n    if x > best:\n        best = x\n"), K.F10_MinMax);
            Assert.IsNotNull(m);
            Assert.AreEqual("best = max(xs)", m.ReplacementText);
        }

        [TestMethod]
        public void F10_MatchesMinWithFullList()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("best = xs[0]\nfor x in xs:\n    if x < best:\n        best = x\n"), K.F10_MinMax);
            Assert.IsNotNull(m);
            Assert.AreEqual("best = min(xs)", m.ReplacementText);
        }

        [TestMethod]
        public void F11_MatchesCount()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("n = 0\nfor x in xs:\n    if x > 0:\n        n += 1\n"), K.F11_Count);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("sum(1 for x in xs if (x > 0))"));
        }

        // ---- F12 / F13 / F14 / F15 ----

        [TestMethod]
        public void F12_MatchesAny()
        {
            Assert.IsTrue(HasKind(VectorizationRefactor.Match("for x in xs:\n    if x > 0:\n        return True\nreturn False\n"), K.F12_Any));
        }

        [TestMethod]
        public void F13_MatchesAll()
        {
            Assert.IsTrue(HasKind(VectorizationRefactor.Match("for x in xs:\n    if not (x > 0):\n        return False\nreturn True\n"), K.F13_All));
        }

        [TestMethod]
        public void F14_MatchesWhere()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] if cond[i] else b[i]\n"), K.F14_Where);
            Assert.IsNotNull(m);
            Assert.AreEqual("c = np.where(cond, a, b)", m.ReplacementText);
        }

        [TestMethod]
        public void F15_MatchesCumsum()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("running = 0\nout = []\nfor x in xs:\n    running += x\n    out.append(running)\n"), K.F15_Cumsum);
            Assert.IsNotNull(m);
            Assert.AreEqual("out = list(np.cumsum(xs))", m.ReplacementText);
        }

        // ---- Reverse ----

        [TestMethod]
        public void R1_MatchesAsarrayBinop()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("c = np.asarray(a) + np.asarray(b)\n"), K.R1_ElementwiseBinopToLoop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("for i in range(len(a)):"));
        }

        [TestMethod]
        public void R2_MatchesSumCall()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("total = sum(xs)\n"), K.R2_SumToLoop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("total = 0"));
            Assert.IsTrue(m.ReplacementText.Contains("total += x"));
        }

        [TestMethod]
        public void R5_MatchesSimpleListComp()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("result = [f(x) for x in xs]\n"), K.R5_ComprehensionToLoop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("result = []"));
            Assert.IsTrue(m.ReplacementText.Contains("result.append(f(x))"));
        }

        [TestMethod]
        public void R5_RejectsListCompWithFilter()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("result = [x for x in xs if x > 0]\n"), K.R5_ComprehensionToLoop));
        }

        [TestMethod]
        public void R10_MatchesMaxCall()
        {
            Assert.IsTrue(HasKind(VectorizationRefactor.Match("best = max(xs)\n"), K.R10_MaxToLoop));
        }

        [TestMethod]
        public void R14_MatchesNpWhere()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("c = np.where(cond, a, b)\n"), K.R14_WhereToLoop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.ReplacementText.Contains("c[i] = a[i] if cond[i] else b[i]"));
        }

        // ---- Indent / negative ----

        [TestMethod]
        public void Indent_ReappliesCommonIndent()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("    for i in range(len(a)):\n        c[i] = a[i] + b[i]\n"), K.F1_ElementwiseBinop);
            Assert.IsNotNull(m);
            Assert.AreEqual("    c = np.asarray(a) + np.asarray(b)", m.ReplacementText);
        }

        [TestMethod]
        public void Negative_EmptySource()
        {
            Assert.AreEqual(0, VectorizationRefactor.Match("").Count);
        }

        [TestMethod]
        public void Negative_ParseErrorReturnsNothing()
        {
            Assert.AreEqual(0, VectorizationRefactor.Match("for i in :\n  pass\n").Count);
        }

        [TestMethod]
        public void Negative_UnrelatedCode()
        {
            Assert.AreEqual(0, VectorizationRefactor.Match("x = 1\ny = 2\nz = x + y\n").Count);
        }

        // ---- Tier 2 relaxation ----

        [TestMethod]
        public void RelaxedF1_IndexMismatchOnTarget()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i+1] = a[i] + b[i]\n"), K.F1_ElementwiseBinop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(m.Caveats.Count > 0);
            Assert.IsTrue(HasCaveatCode(m, "PYR2002"));
            Assert.IsTrue(m.Label.Contains("Possible vectorization"));
        }

        [TestMethod]
        public void RelaxedF1_NonWhitelistOperator()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] << b[i]\n"), K.F1_ElementwiseBinop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasCaveatCode(m, "PYR2001"));
        }

        [TestMethod]
        public void RelaxedF1_TwoIssuesDropConfidenceFurther()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i+1] = a[i] << b[i]\n"), K.F1_ElementwiseBinop);
            Assert.IsNotNull(m);
            Assert.AreEqual(2, m.Caveats.Count);
            Assert.AreEqual(50, m.Confidence);
        }

        [TestMethod]
        public void RelaxedF1_StrictMatchSuppressesRelaxedDuplicate()
        {
            List<VectorizationCandidate> cs = VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + b[i]\n");
            int f1Count = 0;
            foreach (VectorizationCandidate c in cs) if (c.PatternKind == K.F1_ElementwiseBinop) f1Count++;
            Assert.AreEqual(1, f1Count);
            VectorizationCandidate m = GetKind(cs, K.F1_ElementwiseBinop);
            Assert.IsNotNull(m);
            Assert.AreEqual(100, m.Confidence);
            Assert.AreEqual(0, m.Caveats.Count);
        }

        [TestMethod]
        public void RelaxedF1_SkeletonMismatchProducesNothing()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("for x in xs:\n    c[x] = a[x] + b[x]\n"), K.F1_ElementwiseBinop));
        }

        [TestMethod]
        public void RelaxedF8_NonAddOperatorFiresAsRelaxedSum()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("total = 0\nfor x in xs:\n    total -= x\n"), K.F8_Sum);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasCaveatCode(m, "PYR2010"));
        }

        [TestMethod]
        public void RelaxedF8_NonNumericInitFiresWithStartArg()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("total = base\nfor x in xs:\n    total += x\n"), K.F8_Sum);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasCaveatCode(m, "PYR2011"));
            Assert.IsTrue(m.ReplacementText.Contains("sum(xs, base)"));
        }

        [TestMethod]
        public void RelaxedF9_NameInitFiresAsProduct()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("prod = factor\nfor x in xs:\n    prod *= x\n"), K.F9_Product);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasCaveatCode(m, "PYR2021"));
        }

        [TestMethod]
        public void RelaxedF11_NonOneIncrementFires()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("n = 0\nfor x in xs:\n    if x > 0:\n        n += 2\n"), K.F11_Count);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasCaveatCode(m, "PYR2030"));
        }

        [TestMethod]
        public void RelaxedF11_NonZeroInitFires()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("n = 5\nfor x in xs:\n    if x > 0:\n        n += 1\n"), K.F11_Count);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Confidence < 100);
            Assert.IsTrue(HasCaveatCode(m, "PYR2031"));
        }

        // ---- Tier 3 ranker ----

        [TestMethod]
        public void Ranker_SmokeNoCrashOnRandomInit()
        {
            CompletionRanker baseR = new CompletionRanker();
            baseR.InitRandom(unchecked((int)0xCAFEBABE));
            VectorizationRanker r = new VectorizationRanker(baseR);
            List<VectorizationCandidate> cs = new List<VectorizationCandidate>
            {
                VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + b[i]\n")[0],
            };
            r.Rerank(cs, "for i in range(len(a)): c[i] = a[i] + b[i]", "");
            r.RecordAccept(cs[0], cs, "any", "");
        }

        [TestMethod]
        public void Ranker_ContextTriggerEncodesImports()
        {
            CompletionRanker baseR = new CompletionRanker();
            baseR.InitRandom(unchecked((int)0xC0FFEE01));
            VectorizationRanker r = new VectorizationRanker(baseR);
            Assert.AreEqual(0, r.BuildContext("for x in xs: pass", "x = 1").TriggerKind);
            Assert.AreEqual(1, r.BuildContext("for x in xs: pass", "import numpy as np\n").TriggerKind);
            Assert.AreEqual(2, r.BuildContext("for x in xs: pass", "import math\n").TriggerKind);
            Assert.AreEqual(3, r.BuildContext("for x in xs: pass", "import numpy as np\nimport math\n").TriggerKind);
        }

        [TestMethod]
        public void Ranker_RerankIsDeterministic()
        {
            CompletionRanker baseR = new CompletionRanker();
            baseR.InitRandom(0xD3D3D3);
            VectorizationRanker r = new VectorizationRanker(baseR);
            List<VectorizationCandidate> cs1 = VectorizationRefactor.Match("result = []\nfor x in xs:\n    result.append(f(x))\n");
            List<VectorizationCandidate> cs2 = VectorizationRefactor.Match("result = []\nfor x in xs:\n    result.append(f(x))\n");
            r.Rerank(cs1, "selection", "");
            r.Rerank(cs2, "selection", "");
            Assert.AreEqual(cs1.Count, cs2.Count);
            for (int i = 0; i < cs1.Count; i++) Assert.AreEqual(cs1[i].PatternKind, cs2[i].PatternKind);
        }

        [TestMethod]
        public void Ranker_StableSortPreservesOrderForTies()
        {
            CompletionRanker baseR = new CompletionRanker();
            VectorizationRanker r = new VectorizationRanker(baseR);
            List<VectorizationCandidate> cs = new List<VectorizationCandidate>
            {
                new VectorizationCandidate { PatternKind = K.F1_ElementwiseBinop },
                new VectorizationCandidate { PatternKind = K.F2_ElementwiseNegate },
                new VectorizationCandidate { PatternKind = K.F3_ScalarBroadcast },
            };
            r.Rerank(cs, "selection", "");
            Assert.AreEqual(K.F1_ElementwiseBinop, cs[0].PatternKind);
            Assert.AreEqual(K.F2_ElementwiseNegate, cs[1].PatternKind);
            Assert.AreEqual(K.F3_ScalarBroadcast, cs[2].PatternKind);
        }

        [TestMethod]
        public void SyntheticData_BatchProducesValidExamples()
        {
            SyntheticVectorizationData gen = new SyntheticVectorizationData(unchecked((int)0xBEEFC0DE));
            List<SyntheticVectorizationExample> batch = gen.SampleBatch(64);
            Assert.AreEqual(64, batch.Count);
            foreach (SyntheticVectorizationExample ex in batch)
            {
                Assert.IsTrue(ex.Shown.Count >= 1);
                Assert.IsTrue(ex.Ctx.ContextBucket >= 0);
                Assert.IsTrue(ex.Ctx.TriggerKind >= 0);
                Assert.IsTrue(ex.Ctx.TriggerKind < 4);
            }
        }

        // ---- Subspan ----

        [TestMethod]
        public void Subspan_F8InsideFunctionBody()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("def total_sum(xs):\n    total = 0\n    for x in xs:\n        total += x\n    return total\n"), K.F8_Sum);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.HasSubrange);
            Assert.AreEqual(100, m.Confidence);
            Assert.AreEqual(2, m.SubrangeStartLine);
        }

        [TestMethod]
        public void Subspan_F8AtModuleScopeWithExtraStatements()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("x = 1\ntotal = 0\nfor n in xs:\n    total += n\ny = 2\n"), K.F8_Sum);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.HasSubrange);
            Assert.AreEqual(2, m.SubrangeStartLine);
            Assert.IsTrue(m.SubrangeEndLine >= 4);
        }

        [TestMethod]
        public void Subspan_F1WithTrailingStatement()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + b[i]\nprint(c)\n"), K.F1_ElementwiseBinop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.HasSubrange);
            Assert.AreEqual(1, m.SubrangeStartLine);
            Assert.IsTrue(m.SubrangeEndLine >= 2);
        }

        [TestMethod]
        public void Subspan_R2OnSingleLine()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("total = sum(xs)\n"), K.R2_SumToLoop);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.HasSubrange);
            Assert.AreEqual(1, m.SubrangeStartLine);
        }

        [TestMethod]
        public void Subspan_MultipleNonOverlappingPatterns()
        {
            List<VectorizationCandidate> cs = VectorizationRefactor.Match("total = 0\nfor x in xs:\n    total += x\nresult = []\nfor x in xs:\n    result.append(f(x))\n");
            VectorizationCandidate sum = GetKind(cs, K.F8_Sum);
            VectorizationCandidate comp = GetKind(cs, K.F5_MapAppend);
            Assert.IsNotNull(sum);
            Assert.IsNotNull(comp);
            Assert.AreNotEqual(sum.SubrangeStartLine, comp.SubrangeStartLine);
        }

        [TestMethod]
        public void Subspan_IndentedSelectionRestoresColumns()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("    total = 0\n    for x in xs:\n        total += x\n"), K.F8_Sum);
            Assert.IsNotNull(m);
            Assert.IsTrue(m.HasSubrange);
            Assert.AreEqual(5, m.SubrangeStartColumn);
        }

        [TestMethod]
        public void Subspan_ParseWarningStillProducesCandidates()
        {
            Assert.IsTrue(HasKind(VectorizationRefactor.Match("for x in xs:\n    if x > 0:\n        return True\nreturn False\n"), K.F12_Any));
        }

        // ---- F16 / supported ----

        [TestMethod]
        public void F16_RangeFillCanonical()
        {
            VectorizationCandidate m = GetKind(VectorizationRefactor.Match("out = [None] * 5\nfor i in range(5):\n    out[i] = i * 2\n"), K.F16_RangeFill);
            Assert.IsNotNull(m);
            Assert.AreEqual(100, m.Confidence);
            Assert.IsTrue(m.ReplacementText.Contains("for i in range(5)"));
            Assert.IsTrue(m.ReplacementText.Contains("(i * 2)"));
        }

        [TestMethod]
        public void F16_RejectsMismatchedSize()
        {
            Assert.IsFalse(HasKind(VectorizationRefactor.Match("out = [None] * 5\nfor i in range(10):\n    out[i] = i * 2\n"), K.F16_RangeFill));
        }

        [TestMethod]
        public void SupportedPatterns_ListsAllKinds()
        {
            List<SupportedPatternInfo> info = VectorizationRefactor.SupportedPatterns();
            Assert.AreEqual(21, info.Count);
            foreach (SupportedPatternInfo p in info)
            {
                Assert.IsFalse(string.IsNullOrEmpty(p.Name));
                Assert.IsFalse(string.IsNullOrEmpty(p.ExampleSource));
                Assert.IsFalse(string.IsNullOrEmpty(p.Replacement));
            }
        }

        [TestMethod]
        public void Ranker_MatchAcceptsRankerArgument()
        {
            CompletionRanker baseR = new CompletionRanker();
            baseR.InitRandom(0xFEED);
            VectorizationRanker r = new VectorizationRanker(baseR);
            List<VectorizationCandidate> cs = VectorizationRefactor.Match("for i in range(len(a)):\n    c[i] = a[i] + b[i]\n", "import numpy as np\n", r);
            Assert.IsTrue(HasKind(cs, K.F1_ElementwiseBinop));
        }
    }
}
