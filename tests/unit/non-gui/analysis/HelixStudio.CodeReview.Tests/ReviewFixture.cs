namespace HelixPythonNamespace.CodeReview.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Shared plumbing: locate <c>tests/fixtures/python/code-review</c> and submit a directory to
    /// the engine the way the IDE does — real files off disk, not inline strings. An inline snippet
    /// carries no imports, no sibling modules and no test file, so the cross-file rules cannot run
    /// at all and the per-file rules see a shape no real project has.
    /// </summary>
    internal static class ReviewFixture
    {
        /// <summary>Absolute path of a directory under the code-review fixture.</summary>
        public static string Directory(string leaf)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "HelixPython.sln")))
                {
                    return Path.Combine(directory.FullName,
                        Path.Combine("tests", Path.Combine("fixtures", Path.Combine("python",
                            Path.Combine("code-review", leaf)))));
                }
                directory = directory.Parent;
            }
            Assert.Fail("could not locate the repository root");
            return null;
        }

        /// <summary>Every .py file in a fixture directory, submitted as one review.</summary>
        public static List<ReviewSource> Sources(string leaf)
        {
            string root = Directory(leaf);
            Assert.IsTrue(System.IO.Directory.Exists(root), "missing fixture directory: " + root);

            var sources = new List<ReviewSource>();
            foreach (string path in System.IO.Directory.GetFiles(root, "*.py", SearchOption.AllDirectories))
                sources.Add(new ReviewSource(path, File.ReadAllText(path)));

            Assert.IsTrue(sources.Count >= 4, "fixture " + leaf + " should hold the whole example");
            return sources;
        }

        public static ReviewReport Review(string leaf)
        {
            return new CodeReviewEngine().Review(Sources(leaf), ReviewOptions.Default, leaf);
        }

        /// <summary>A one-file review of inline source — for pinning a single rule's boundary.</summary>
        public static ReviewReport ReviewSnippet(string fileName, string source)
        {
            var sources = new List<ReviewSource> { new ReviewSource(fileName, source) };
            ReviewReport report = new CodeReviewEngine().Review(sources, ReviewOptions.Default, "snippet");
            Assert.AreEqual(0, report.Unparsed.Count,
                "the snippet must parse, otherwise the rule under test never ran: " +
                string.Join("; ", report.Unparsed.ToArray()));
            Assert.AreEqual(0, report.RuleFailures.Count,
                string.Join("; ", report.RuleFailures.ToArray()));
            return report;
        }

        public static bool Has(this ReviewReport report, string ruleId)
        {
            foreach (ReviewFinding finding in report.Findings)
                if (string.Equals(finding.RuleId, ruleId, StringComparison.Ordinal)) return true;
            return false;
        }

        public static ReviewFinding First(this ReviewReport report, string ruleId)
        {
            foreach (ReviewFinding finding in report.Findings)
                if (string.Equals(finding.RuleId, ruleId, StringComparison.Ordinal)) return finding;
            Assert.Fail(ruleId + " did not fire. Findings: " + Describe(report));
            return null;
        }

        public static string Describe(ReviewReport report)
        {
            var parts = new List<string>();
            foreach (ReviewFinding finding in report.Findings)
                parts.Add(finding.RuleId + " @ " + Path.GetFileName(finding.FilePath ?? string.Empty) +
                          ":" + finding.Line + " — " + finding.Message);
            return parts.Count == 0 ? "(none)" : Environment.NewLine + string.Join(Environment.NewLine, parts.ToArray());
        }
    }
}
