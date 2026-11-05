namespace HelixPythonNamespace.CodeReview.Tests
{
    using System;
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class ReviewMarkdownWriterTests
    {
        [TestMethod]
        public void TheExportLeadsWithTheVerdictAndItsReason()
        {
            string markdown = ReviewMarkdownWriter.Write(ReviewFixture.Review("faulty"));

            Assert.IsTrue(markdown.StartsWith("# Code review", StringComparison.Ordinal));
            Assert.IsTrue(markdown.Contains("**Verdict:** Request changes"));
            Assert.IsTrue(markdown.Contains("**Reason:**"));
        }

        [TestMethod]
        public void EveryFindingCarriesItsLocationRationaleAndSuggestion()
        {
            string markdown = ReviewMarkdownWriter.Write(ReviewFixture.Review("faulty"));

            Assert.IsTrue(markdown.Contains("CR-SEC-008"), "rule ids are the anchor for a discussion");
            Assert.IsTrue(markdown.Contains("**Why it matters.**"));
            Assert.IsTrue(markdown.Contains("**Suggested change.**"));
            Assert.IsTrue(markdown.Contains("store.py:"), "findings must name their file and line");
        }

        /// <summary>
        /// A reader who stops after the verdict must already have been told what was not examined,
        /// so the incompleteness section is emitted before the findings — not appended after them.
        /// </summary>
        [TestMethod]
        public void WhatWasNotReviewedAppearsBeforeTheFindings()
        {
            var sources = new List<ReviewSource>
            {
                new ReviewSource("broken.py", "def f(:\n    return 1\n"),
                new ReviewSource("a.py", "def f(x):\n    if x == x:\n        return 1\n    return 0\n"),
            };
            ReviewReport report = new CodeReviewEngine().Review(sources);
            string markdown = ReviewMarkdownWriter.Write(report);

            int notCovered = markdown.IndexOf("## What this review did not cover", StringComparison.Ordinal);
            int firstFinding = markdown.IndexOf("CR-COR-009", StringComparison.Ordinal);

            Assert.IsTrue(notCovered >= 0, "the section must be present:" + Environment.NewLine + markdown);
            Assert.IsTrue(firstFinding > notCovered, "it must come first");
            Assert.IsTrue(markdown.Contains("broken.py"));
        }

        [TestMethod]
        public void ACleanReviewSaysSoWithoutInventingSections()
        {
            string markdown = ReviewMarkdownWriter.Write(ReviewFixture.Review("clean"));

            Assert.IsTrue(markdown.Contains("**Verdict:** Approve"));
            Assert.IsTrue(markdown.Contains("No findings."));
            Assert.IsFalse(markdown.Contains("## What this review did not cover"));
        }
    }
}
