namespace HelixPythonNamespace.CodeReview.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// The acceptance pair for the whole rule library, driven over the real fixture directories
    /// rather than inline strings — see <c>tests/fixtures/python/code-review/README.md</c>.
    /// </summary>
    [TestClass]
    public class CodeReviewFixtureTests
    {
        /// <summary>
        /// The false-positive tripwire. A review engine that comments on correct code is noise,
        /// and the first thing anyone does with a noisy one is stop reading it — so this assertion
        /// is the one that constrains every new rule. When a rule breaks it, the rule is wrong.
        /// </summary>
        [TestMethod]
        public void TheCleanFixtureProducesNoFindings()
        {
            ReviewReport report = ReviewFixture.Review("clean");

            Assert.AreEqual(0, report.Unparsed.Count,
                "clean fixture must parse: " + string.Join("; ", report.Unparsed.ToArray()));
            Assert.AreEqual(0, report.RuleFailures.Count,
                "no rule may throw: " + string.Join("; ", report.RuleFailures.ToArray()));
            Assert.IsTrue(report.IsCompleteReview);
            Assert.AreEqual(0, report.Findings.Count,
                "the clean fixture must produce nothing:" + ReviewFixture.Describe(report));
            Assert.AreEqual(ReviewVerdict.Approve, report.Verdict);
        }

        /// <summary>Every defect the fixture README claims is planted must actually be reported.</summary>
        [TestMethod]
        public void TheFaultyFixtureFindsEveryPlantedDefect()
        {
            ReviewReport report = ReviewFixture.Review("faulty");

            Assert.AreEqual(0, report.Unparsed.Count,
                "the faulty fixture must still parse, or its rules never ran: " +
                string.Join("; ", report.Unparsed.ToArray()));

            // Rule id -> the file the README says holds it. Both halves are asserted: a rule that
            // fires on the wrong file is as wrong as one that does not fire.
            var planted = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "CR-COR-006", "models.py" },
                { "CR-DES-007", "models.py" },
                { "CR-COR-007", "models.py" },
                { "CR-SEC-008", "store.py" },
                { "CR-ERR-001", "store.py" },
                { "CR-COR-003", "store.py" },
                { "CR-ERR-005", "store.py" },
                { "CR-SEC-004", "service.py" },
                { "CR-SEC-002", "service.py" },
                { "CR-COR-001", "service.py" },
                { "CR-COR-004", "service.py" },
                { "CR-MNT-003", "service.py" },
                { "CR-MNT-006", "service.py" },
                { "CR-PERF-001", "service.py" },
                { "CR-TST-001", "test_orders.py" },
            };

            var missing = new List<string>();
            foreach (KeyValuePair<string, string> pair in planted)
            {
                ReviewFinding found = null;
                foreach (ReviewFinding finding in report.Findings)
                {
                    if (!string.Equals(finding.RuleId, pair.Key, StringComparison.Ordinal)) continue;
                    if (!string.Equals(Path.GetFileName(finding.FilePath), pair.Value, StringComparison.Ordinal)) continue;
                    found = finding;
                    break;
                }
                if (found == null) { missing.Add(pair.Key + " in " + pair.Value); continue; }

                // A finding without all three parts is a lint message wearing a review's clothes.
                Assert.IsFalse(string.IsNullOrWhiteSpace(found.Message), pair.Key + " has no message");
                Assert.IsFalse(string.IsNullOrWhiteSpace(found.Rationale), pair.Key + " has no rationale");
                Assert.IsFalse(string.IsNullOrWhiteSpace(found.Suggestion), pair.Key + " has no suggestion");
                Assert.IsTrue(found.Line > 0, pair.Key + " has no line");
            }

            Assert.AreEqual(0, missing.Count,
                "planted defects not reported: " + string.Join(", ", missing.ToArray()) +
                ReviewFixture.Describe(report));
        }

        [TestMethod]
        public void TheFaultyFixtureRequestsChanges()
        {
            ReviewReport report = ReviewFixture.Review("faulty");

            Assert.AreEqual(ReviewVerdict.RequestChanges, report.Verdict, report.VerdictReason);
            Assert.IsTrue(report.VerdictReason.Length > 0);
            Assert.IsTrue(report.TotalRisk > 0);
            Assert.IsTrue(report.Hotspots.Count > 0, "risk must be attributed to files");
            Assert.AreEqual("service.py", Path.GetFileName(report.Hotspots[0].FilePath),
                "service.py carries the most planted defects, so it must rank first");
        }

        /// <summary>Severity ordering is what makes the panel readable top-down.</summary>
        [TestMethod]
        public void FindingsAreOrderedBySeverity()
        {
            ReviewReport report = ReviewFixture.Review("faulty");

            for (int i = 1; i < report.Findings.Count; i++)
                Assert.IsTrue(report.Findings[i - 1].Severity >= report.Findings[i].Severity,
                    "findings must be ordered worst-first");
        }

        /// <summary>Same input, same output — the panel must not reshuffle between runs.</summary>
        [TestMethod]
        public void TheReviewIsDeterministic()
        {
            ReviewReport first = ReviewFixture.Review("faulty");
            ReviewReport second = ReviewFixture.Review("faulty");

            Assert.AreEqual(first.Findings.Count, second.Findings.Count);
            for (int i = 0; i < first.Findings.Count; i++)
                Assert.AreEqual(first.Findings[i].Key, second.Findings[i].Key, "order changed at index " + i);
        }
    }
}
