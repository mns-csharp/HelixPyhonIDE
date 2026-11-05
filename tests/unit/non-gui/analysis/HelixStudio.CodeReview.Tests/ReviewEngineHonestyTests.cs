namespace HelixPythonNamespace.CodeReview.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// The properties that make a verdict trustworthy. Each of these is a way a review could
    /// report "clean" while having examined less than it claims.
    /// </summary>
    [TestClass]
    public class ReviewEngineHonestyTests
    {
        [TestMethod]
        public void AFileThatDoesNotParseIsRecordedAndNotCountedAsReviewed()
        {
            var sources = new List<ReviewSource>
            {
                new ReviewSource("broken.py", "def f(:\n    return 1\n"),
                new ReviewSource("fine.py", "class A:\n    def m(self):\n        return 1\n"),
            };

            ReviewReport report = new CodeReviewEngine().Review(sources);

            Assert.AreEqual(1, report.Unparsed.Count, "the unparsable file must be named");
            Assert.IsTrue(report.Unparsed[0].Contains("broken.py"));
            Assert.AreEqual(2, report.Summary.FilesSubmitted);
            Assert.AreEqual(1, report.Summary.FilesReviewed, "the unparsable file was not reviewed");
            Assert.IsFalse(report.IsCompleteReview);
        }

        /// <summary>
        /// The rule that keeps the whole report honest: a review that could not read every file
        /// must not present as a clean approval, because silence from a file nobody opened is not
        /// evidence about that file.
        /// </summary>
        [TestMethod]
        public void AnIncompleteReviewNeverApprovesOutright()
        {
            var sources = new List<ReviewSource>
            {
                new ReviewSource("broken.py", "def f(:\n    return 1\n"),
            };

            ReviewReport report = new CodeReviewEngine().Review(sources);

            Assert.AreEqual(0, report.Findings.Count, "nothing was found — but nothing was read either");
            Assert.AreNotEqual(ReviewVerdict.Approve, report.Verdict);
            Assert.IsTrue(report.VerdictReason.IndexOf("could not be parsed", StringComparison.Ordinal) >= 0,
                "the reason must say why: " + report.VerdictReason);
        }

        [TestMethod]
        public void ARuleThatThrowsIsRecordedAndDoesNotAbortTheReview()
        {
            var engine = new CodeReviewEngine();
            engine.AddRule(new ThrowingRule());

            var sources = new List<ReviewSource>
            {
                new ReviewSource("a.py", "class A:\n    def m(self):\n        x = x\n"),
            };
            ReviewReport report = engine.Review(sources);

            Assert.AreEqual(1, report.RuleFailures.Count, "the crashing check must be named");
            Assert.IsTrue(report.RuleFailures[0].Contains("CR-TEST-BOOM"));
            Assert.IsFalse(report.IsCompleteReview);
            Assert.IsTrue(report.Has("CR-COR-006"), "the other rules still ran");
        }

        [TestMethod]
        public void CancellationIsReportedRatherThanPresentedAsAResult()
        {
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var sources = new List<ReviewSource> { new ReviewSource("a.py", "x = 1\n") };
            ReviewReport report = new CodeReviewEngine().Review(sources, ReviewOptions.Default, "x", cancelled.Token);

            Assert.IsTrue(report.Cancelled);
            Assert.IsFalse(report.IsCompleteReview);
            Assert.AreNotEqual(ReviewVerdict.Approve, report.Verdict);
        }

        [TestMethod]
        public void FindingsOutsideTheChangedLinesAreDemotedNotDeleted()
        {
            const string Source =
                "import subprocess\n" +
                "\n" +
                "def a(cmd):\n" +
                "    subprocess.run(cmd, shell=True)\n" +
                "\n" +
                "def b(cmd):\n" +
                "    subprocess.run(cmd, shell=True)\n";

            // Only the second call is inside the reviewed change.
            var changed = new List<LineRange> { new LineRange(6, 7) };
            var sources = new List<ReviewSource> { new ReviewSource("a.py", Source, changed) };

            var options = ReviewOptions.Default;
            options.DemoteUnchangedFindings = true;
            ReviewReport report = new CodeReviewEngine().Review(sources, options, "changes");

            int inChange = 0, outside = 0;
            foreach (ReviewFinding finding in report.Findings)
            {
                if (finding.RuleId != "CR-SEC-002") continue;
                if (finding.InReviewedLines) { inChange++; Assert.AreEqual(ReviewSeverity.Critical, finding.Severity); }
                else { outside++; Assert.AreEqual(ReviewSeverity.Info, finding.Severity); }
            }

            Assert.AreEqual(1, inChange, "the changed call keeps its severity");
            Assert.AreEqual(1, outside, "the pre-existing call is still shown, as context");
        }

        [TestMethod]
        public void AFindingInsideTheChangeOutweighsTheSameFindingOutsideIt()
        {
            var inside = new ReviewFinding { Severity = ReviewSeverity.Major, Confidence = ReviewConfidence.High, InReviewedLines = true };
            var outside = new ReviewFinding { Severity = ReviewSeverity.Major, Confidence = ReviewConfidence.High, InReviewedLines = false };

            Assert.IsTrue(inside.Weight > outside.Weight);
        }

        [TestMethod]
        public void LowConfidenceCountsForLessThanHigh()
        {
            var sure = new ReviewFinding { Severity = ReviewSeverity.Major, Confidence = ReviewConfidence.High };
            var unsure = new ReviewFinding { Severity = ReviewSeverity.Major, Confidence = ReviewConfidence.Low };

            Assert.IsTrue(sure.Weight > unsure.Weight);
        }

        [TestMethod]
        public void EveryRegisteredRuleHasAUniqueIdentifier()
        {
            var engine = new CodeReviewEngine();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (IReviewRule rule in engine.FileRules)
            {
                Assert.IsTrue(rule.RuleId.StartsWith("CR-", StringComparison.Ordinal), rule.RuleId);
                Assert.IsFalse(string.IsNullOrWhiteSpace(rule.Title), rule.RuleId + " has no title");
                Assert.IsTrue(seen.Add(rule.RuleId), "duplicate rule id: " + rule.RuleId);
            }
            foreach (ICrossFileReviewRule rule in engine.CrossFileRules)
            {
                Assert.IsTrue(rule.RuleId.StartsWith("CR-", StringComparison.Ordinal), rule.RuleId);
                Assert.IsTrue(seen.Add(rule.RuleId), "duplicate rule id: " + rule.RuleId);
            }
            Assert.AreEqual(engine.RuleCount, seen.Count);
        }

        [TestMethod]
        public void AnEmptySubmissionApprovesAndSaysSoPlainly()
        {
            ReviewReport report = new CodeReviewEngine().Review(new List<ReviewSource>());

            Assert.AreEqual(ReviewVerdict.Approve, report.Verdict);
            Assert.IsTrue(report.IsCompleteReview);
            Assert.AreEqual(0, report.Summary.FilesReviewed);
        }

        private sealed class ThrowingRule : IReviewRule
        {
            public string RuleId { get { return "CR-TEST-BOOM"; } }
            public string Title { get { return "Always throws"; } }
            public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

            public void Review(ReviewContext context, List<ReviewFinding> output)
            {
                throw new InvalidOperationException("deliberate");
            }
        }
    }
}
