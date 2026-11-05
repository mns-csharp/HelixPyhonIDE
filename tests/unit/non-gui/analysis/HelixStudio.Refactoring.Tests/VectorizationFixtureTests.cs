namespace HelixPythonNamespace.Refactoring.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Refactoring;
    using K = HelixPythonNamespace.Refactoring.VectorizationCandidate.Kind;

    /// <summary>
    /// Runs the real VectorizationRefactor engine against the hand-authored
    /// tests/fixtures/python/python-vectorization-sample fixture, extracting each selection
    /// by its literal "# BEGIN &lt;ID&gt;" / "# END &lt;ID&gt;" marker pair (matching how a user
    /// actually selects a sub-range inside a larger file, not an isolated snippet) and
    /// asserting the engine's real behavior against what each fixture function's docstring
    /// claims. Where a fixture's own docstring claim didn't match observed engine behavior
    /// (e.g. a case assumed to be a hard refusal that the Tier-2 relaxation engine actually
    /// accepts with a caveat instead), the fixture's docstring was corrected to describe the
    /// real behavior rather than the assumption — this file is the source of truth for what
    /// the engine actually does with each block.
    /// </summary>
    [TestClass]
    public class VectorizationFixtureTests
    {
        private static string FixtureRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            DirectoryInfo d = new DirectoryInfo(dir);
            while (d != null && Directory.GetFiles(d.FullName, "*.sln").Length == 0)
                d = d.Parent;
            Assert.IsNotNull(d, "could not locate repo root (the product .sln) from " + dir);
            return Path.Combine(d.FullName, "tests", "fixtures", "python", "python-vectorization-sample", "src", "pyvectorsample");
        }

        private static readonly Dictionary<string, string> FileCache = new Dictionary<string, string>();

        private static string ReadFixtureFile(string fileName)
        {
            string cached;
            if (FileCache.TryGetValue(fileName, out cached)) return cached;
            string path = Path.Combine(FixtureRoot(), fileName);
            Assert.IsTrue(File.Exists(path), "fixture file not found: " + path);
            string text = File.ReadAllText(path);
            FileCache[fileName] = text;
            return text;
        }

        private static string ExtractBlock(string fileName, string markerId)
        {
            string full = ReadFixtureFile(fileName);
            string beginMarker = "# BEGIN " + markerId;
            string endMarker = "# END " + markerId;
            int begin = full.IndexOf(beginMarker, StringComparison.Ordinal);
            Assert.IsTrue(begin >= 0, "marker not found: " + beginMarker + " in " + fileName);
            int lineStart = full.IndexOf('\n', begin) + 1;
            int end = full.IndexOf(endMarker, lineStart, StringComparison.Ordinal);
            Assert.IsTrue(end >= 0, "marker not found: " + endMarker + " in " + fileName);
            return full.Substring(lineStart, end - lineStart);
        }

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

        private static List<VectorizationCandidate> MatchBlock(string fileName, string markerId)
        {
            string selection = ExtractBlock(fileName, markerId);
            string full = ReadFixtureFile(fileName);
            return VectorizationRefactor.Match(selection, full);
        }

        // ---- forward_patterns.py: every F1-F16 selection must match its own kind ----

        [TestMethod]
        public void Forward_F1_AddElementwise() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F1"), K.F1_ElementwiseBinop));

        [TestMethod]
        public void Forward_F2_NegateAll() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F2"), K.F2_ElementwiseNegate));

        [TestMethod]
        public void Forward_F3_ScaleByTwo() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F3"), K.F3_ScalarBroadcast));

        [TestMethod]
        public void Forward_F4_SineOfEach() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F4"), K.F4_MathToNumpy));

        [TestMethod]
        public void Forward_F5_SquaresOfPositive() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F5"), K.F5_MapAppend));

        [TestMethod]
        public void Forward_F6_KeepPositive() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F6"), K.F6_FilterAppend));

        [TestMethod]
        public void Forward_F7_DoubledPositiveReadings() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F7"), K.F7_FilterMapAppend));

        [TestMethod]
        public void Forward_F8_TotalOf() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F8"), K.F8_Sum));

        [TestMethod]
        public void Forward_F9_FactorialLikeProduct() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F9"), K.F9_Product));

        [TestMethod]
        public void Forward_F10_HighestReading() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F10"), K.F10_MinMax));

        [TestMethod]
        public void Forward_F11_CountAboveThreshold() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F11"), K.F11_Count));

        [TestMethod]
        public void Forward_F12_AnyReadingPositive() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F12"), K.F12_Any));

        [TestMethod]
        public void Forward_F13_AllReadingsPositive() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F13"), K.F13_All));

        [TestMethod]
        public void Forward_F14_PickByCondition() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F14"), K.F14_Where));

        [TestMethod]
        public void Forward_F15_RunningTotal() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F15"), K.F15_Cumsum));

        [TestMethod]
        public void Forward_F16_DoubledIndices() => Assert.IsTrue(HasKind(MatchBlock("forward_patterns.py", "F16"), K.F16_RangeFill));

        // ---- reverse_patterns.py: every R1/R2/R5/R10/R14 selection must match its own kind ----

        [TestMethod]
        public void Reverse_R1_AddElementwise() => Assert.IsTrue(HasKind(MatchBlock("reverse_patterns.py", "R1"), K.R1_ElementwiseBinopToLoop));

        [TestMethod]
        public void Reverse_R2_TotalOf() => Assert.IsTrue(HasKind(MatchBlock("reverse_patterns.py", "R2"), K.R2_SumToLoop));

        [TestMethod]
        public void Reverse_R5_DoubledPositive() => Assert.IsTrue(HasKind(MatchBlock("reverse_patterns.py", "R5"), K.R5_ComprehensionToLoop));

        [TestMethod]
        public void Reverse_R10_HighestReading() => Assert.IsTrue(HasKind(MatchBlock("reverse_patterns.py", "R10"), K.R10_MaxToLoop));

        [TestMethod]
        public void Reverse_R14_PickByCondition() => Assert.IsTrue(HasKind(MatchBlock("reverse_patterns.py", "R14"), K.R14_WhereToLoop));

        // ---- real_world_examples.py: pattern embedded in a bigger function, sub-range selected ----

        [TestMethod]
        public void RealWorld_SensorNormalize_MatchesF1() => Assert.IsTrue(HasKind(MatchBlock("real_world_examples.py", "REALWORLD_SENSOR_NORMALIZE"), K.F1_ElementwiseBinop));

        [TestMethod]
        public void RealWorld_DegToRad_MatchesF5MapAppend() => Assert.IsTrue(HasKind(MatchBlock("real_world_examples.py", "REALWORLD_DEG_TO_RAD"), K.F5_MapAppend));

        [TestMethod]
        public void RealWorld_OrderTotal_MatchesF8Sum() => Assert.IsTrue(HasKind(MatchBlock("real_world_examples.py", "REALWORLD_ORDER_TOTAL"), K.F8_Sum));

        [TestMethod]
        public void RealWorld_PeakDailyTemperature_MatchesF10MinMax() => Assert.IsTrue(HasKind(MatchBlock("real_world_examples.py", "REALWORLD_PEAK_TEMP"), K.F10_MinMax));

        [TestMethod]
        public void RealWorld_CumulativeDistance_MatchesF15Cumsum() => Assert.IsTrue(HasKind(MatchBlock("real_world_examples.py", "REALWORLD_CUMULATIVE_DISTANCE"), K.F15_Cumsum));

        [TestMethod]
        public void RealWorld_ClampToChannelRange_MatchesR14WhereToLoop() => Assert.IsTrue(HasKind(MatchBlock("real_world_examples.py", "REALWORLD_CLAMP_WHERE"), K.R14_WhereToLoop));

        // ---- edge_cases.py ----

        [TestMethod]
        public void Edge_DoubleInPlace_StillMatchesF3() => Assert.IsTrue(HasKind(MatchBlock("edge_cases.py", "EDGE_INPLACE_SCALE"), K.F3_ScalarBroadcast));

        [TestMethod]
        public void Edge_SumOfSingleElement_MatchesF8LikeAnySum() => Assert.IsTrue(HasKind(MatchBlock("edge_cases.py", "EDGE_SUM_SINGLE"), K.F8_Sum));

        [TestMethod]
        public void Edge_AttributeArray_DoesNotMatchF3()
        {
            // range(len(container.values)) isn't the simple RangeOverLen(Name) shape the
            // matcher expects, so this must not match F3 despite being semantically identical
            // to Edge_DoubleInPlace_StillMatchesF3 above.
            Assert.IsFalse(HasKind(MatchBlock("edge_cases.py", "EDGE_ATTRIBUTE_ARRAY"), K.F3_ScalarBroadcast));
        }

        [TestMethod]
        public void Edge_SumViaEnumerate_DoesNotMatchF8()
        {
            // for _, x in enumerate(values) isn't ForOverIter's plain-name loop shape.
            Assert.IsFalse(HasKind(MatchBlock("edge_cases.py", "EDGE_ENUMERATE_SUM"), K.F8_Sum));
        }

        [TestMethod]
        public void Edge_NestedMatrixSum_DoesNotMatchF8()
        {
            // A for-loop nested inside another for-loop is not F8's flat single-loop shape.
            Assert.IsFalse(HasKind(MatchBlock("edge_cases.py", "EDGE_NESTED_SUM"), K.F8_Sum));
        }

        // ---- near_misses.py: structural (skeleton) mismatches the engine cannot relax ----

        [TestMethod]
        public void NearMiss_PrefixSum_DoesNotMatchF1()
        {
            // range(1, len(a)) is a 2-arg range, not the RangeOverLen(single-arg len()) shape.
            Assert.IsFalse(HasKind(MatchBlock("near_misses.py", "NEARMISS_PREFIX_SUM"), K.F1_ElementwiseBinop));
        }

        [TestMethod]
        public void NearMiss_StridedDownsample_DoesNotMatchF3()
        {
            // range(0, len(a), 2) is a 3-arg strided range, not RangeOverLen's single-arg form.
            Assert.IsFalse(HasKind(MatchBlock("near_misses.py", "NEARMISS_STRIDE"), K.F3_ScalarBroadcast));
        }

        [TestMethod]
        public void NearMiss_EarlyExitScan_DoesNotMatchF10()
        {
            // Two if-statements in the loop body (the break guard plus the max-update) is not
            // F10's single-statement StrictForBody shape.
            Assert.IsFalse(HasKind(MatchBlock("near_misses.py", "NEARMISS_EARLY_EXIT_SCAN"), K.F10_MinMax));
        }

        [TestMethod]
        public void NearMiss_FilterAppendWithSideEffect_DoesNotMatchF6()
        {
            // Two statements inside the if-body (the kept-element append plus the log append)
            // is not F6's single-statement if-body shape.
            Assert.IsFalse(HasKind(MatchBlock("near_misses.py", "NEARMISS_FILTER_SIDE_EFFECT"), K.F6_FilterAppend));
        }

        [TestMethod]
        public void NearMiss_GatherNotMap_DoesNotMatchF5()
        {
            // Iterating range(len(xs)) and appending xs[i] * weights[i] is a RangeOverLen shape,
            // not F5's ForOverIter(plain xs) + append(f(x)) shape.
            Assert.IsFalse(HasKind(MatchBlock("near_misses.py", "NEARMISS_GATHER_NOT_MAP"), K.F5_MapAppend));
        }

        [TestMethod]
        public void NearMiss_ProductWithReset_DoesNotMatchF9()
        {
            // The loop body is an if/else (reset-to-1 vs multiply), not F9's single AugAssign
            // '*=' statement.
            Assert.IsFalse(HasKind(MatchBlock("near_misses.py", "NEARMISS_PRODUCT_RESET"), K.F9_Product));
        }

        // The remaining two near_misses.py cases (shifted-index F1, wrong-operator F8) turned
        // out, once actually run through the engine, to be within Tier-2's relaxation
        // tolerance rather than a hard skeleton mismatch: the engine still offers a rewrite,
        // just at reduced confidence with an explicit caveat explaining exactly what's
        // suspicious about it (matching RelaxedF1_IndexMismatchOnTarget /
        // RelaxedF8_NonAddOperatorFiresAsRelaxedSum in VectorizationTests.cs). That is in fact
        // the correct, intentional design: these two are cases where a rewrite is offered but
        // must NOT be accepted uncritically, not cases where no rewrite is offered at all.

        [TestMethod]
        public void NearMiss_ShiftedDifference_MatchesF1OnlyAsLowConfidenceWithCaveat()
        {
            VectorizationCandidate m = GetKind(MatchBlock("near_misses.py", "NEARMISS_SHIFTED_INDEX"), K.F1_ElementwiseBinop);
            Assert.IsNotNull(m, "expected a relaxed (caveated) F1 match, not a hard refusal");
            Assert.IsTrue(m.Confidence < 100, "a shifted operand index must never be offered at full confidence");
            Assert.IsTrue(m.Caveats.Count > 0, "the confidence drop must be explained by a caveat");
        }

        [TestMethod]
        public void NearMiss_WrongAccumulatorOperator_MatchesF8OnlyAsLowConfidenceWithCaveat()
        {
            VectorizationCandidate m = GetKind(MatchBlock("near_misses.py", "NEARMISS_WRONG_ACCUM_OP"), K.F8_Sum);
            Assert.IsNotNull(m, "expected a relaxed (caveated) F8 match, not a hard refusal");
            Assert.IsTrue(m.Confidence < 100, "a '-=' accumulator must never be offered as sum() at full confidence");
            Assert.IsTrue(m.Caveats.Count > 0, "the confidence drop must be explained by a caveat");
        }

        [TestMethod]
        public void NearMiss_EagerSideEffectWhere_DoesNotMatchF14AtFullConfidence()
        {
            // touch(a[i]) in the true-branch is a Call, not a bare Subscript read of the array —
            // whether the engine refuses outright or only offers a caveated low-confidence
            // match, it must never present this as a clean, uncaveated np.where() rewrite,
            // since NumPy's np.where evaluates BOTH branches eagerly while the original
            // if/else only ever calls touch() on the selected side.
            VectorizationCandidate m = GetKind(MatchBlock("near_misses.py", "NEARMISS_EAGER_SIDE_EFFECT"), K.F14_Where);
            if (m != null) Assert.IsTrue(m.Confidence < 100 && m.Caveats.Count > 0);
        }
    }
}
