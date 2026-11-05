namespace HelixPythonNamespace.CodeReview
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Renders a <see cref="ReviewReport"/> as the review write-up a person would post: verdict
    /// first, then what was and was not examined, then the comments grouped by severity.
    ///
    /// The incompleteness section is emitted BEFORE the findings, deliberately. A reader who stops
    /// after the verdict must still have been told which files were not read.
    /// </summary>
    public static class ReviewMarkdownWriter
    {
        public static string Write(ReviewReport report)
        {
            var text = new StringBuilder();
            if (report == null) return string.Empty;

            text.AppendLine("# Code review");
            text.AppendLine();
            if (!string.IsNullOrEmpty(report.ScopeLabel))
                text.AppendLine("**Scope:** " + report.ScopeLabel + "  ");
            text.AppendLine("**Verdict:** " + VerdictText(report.Verdict) + "  ");
            text.AppendLine("**Reason:** " + report.VerdictReason);
            text.AppendLine();

            text.AppendLine("| Reviewed | Findings |");
            text.AppendLine("|---|---|");
            text.AppendLine("| " + report.Summary.FilesReviewed + " of " + report.Summary.FilesSubmitted +
                " file(s), " + report.Summary.ClassesReviewed + " class(es), " +
                report.Summary.FunctionsReviewed + " function(s), " + report.Summary.SourceLines + " code lines" +
                " | " + SeverityBreakdown(report) + " |");
            text.AppendLine();

            if (!report.IsCompleteReview)
            {
                text.AppendLine("## What this review did not cover");
                text.AppendLine();
                if (report.Cancelled)
                    text.AppendLine("- The run was cancelled before finishing; the findings below are partial.");
                foreach (string file in report.Unparsed)
                    text.AppendLine("- Not parsed, so not reviewed: `" + file + "`");
                foreach (string failure in report.RuleFailures)
                    text.AppendLine("- Check did not run: `" + failure + "`");
                text.AppendLine();
            }

            if (report.Findings.Count == 0)
            {
                text.AppendLine("No findings.");
                return text.ToString();
            }

            if (report.Hotspots.Count > 1)
            {
                text.AppendLine("## Where the risk is");
                text.AppendLine();
                text.AppendLine("| File | Findings | Worst | Risk |");
                text.AppendLine("|---|---:|---|---:|");
                int shown = 0;
                foreach (ReviewHotspot hotspot in report.Hotspots)
                {
                    if (shown++ == 10) break;
                    text.AppendLine("| `" + FileName(hotspot.FilePath) + "` | " + hotspot.FindingCount +
                        " | " + hotspot.WorstSeverity + " | " + hotspot.Risk.ToString("0") + " |");
                }
                if (report.Hotspots.Count > 10)
                    text.AppendLine();
                if (report.Hotspots.Count > 10)
                    text.AppendLine("…and " + (report.Hotspots.Count - 10) + " more file(s).");
                text.AppendLine();
            }

            var order = new[]
            {
                ReviewSeverity.Blocker, ReviewSeverity.Critical, ReviewSeverity.Major,
                ReviewSeverity.Minor, ReviewSeverity.Info,
            };
            foreach (ReviewSeverity severity in order)
            {
                var group = new List<ReviewFinding>();
                foreach (ReviewFinding finding in report.Findings)
                    if (finding.Severity == severity) group.Add(finding);
                if (group.Count == 0) continue;

                text.AppendLine("## " + severity + " (" + group.Count + ")");
                text.AppendLine();
                foreach (ReviewFinding finding in group) WriteFinding(text, finding);
            }

            return text.ToString();
        }

        private static void WriteFinding(StringBuilder text, ReviewFinding finding)
        {
            text.AppendLine("### " + finding.RuleId + " — " + finding.Title);
            text.AppendLine();
            text.AppendLine("`" + FileName(finding.FilePath) + ":" + finding.Line + "`" +
                (finding.MemberName.Length > 0 ? " in `" + finding.MemberName + "`" : string.Empty) +
                " · " + finding.Category + " · confidence " + finding.Confidence +
                " · effort " + finding.Effort);
            text.AppendLine();
            if (!string.IsNullOrEmpty(finding.Snippet))
            {
                text.AppendLine("```python");
                text.AppendLine(finding.Snippet);
                text.AppendLine("```");
                text.AppendLine();
            }
            text.AppendLine(finding.Message);
            text.AppendLine();
            if (!string.IsNullOrEmpty(finding.Rationale))
            {
                text.AppendLine("**Why it matters.** " + finding.Rationale);
                text.AppendLine();
            }
            if (!string.IsNullOrEmpty(finding.Suggestion))
            {
                text.AppendLine("**Suggested change.** " + finding.Suggestion);
                text.AppendLine();
            }
        }

        private static string SeverityBreakdown(ReviewReport report)
        {
            var parts = new List<string>();
            var order = new[]
            {
                ReviewSeverity.Blocker, ReviewSeverity.Critical, ReviewSeverity.Major,
                ReviewSeverity.Minor, ReviewSeverity.Info,
            };
            foreach (ReviewSeverity severity in order)
            {
                int count = report.Summary.Count(severity);
                if (count > 0) parts.Add(count + " " + severity.ToString().ToLowerInvariant());
            }
            return parts.Count == 0 ? "none" : string.Join(", ", parts.ToArray());
        }

        public static string VerdictText(ReviewVerdict verdict)
        {
            switch (verdict)
            {
                case ReviewVerdict.RequestChanges: return "Request changes";
                case ReviewVerdict.ApproveWithComments: return "Approve with comments";
                default: return "Approve";
            }
        }

        private static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return System.IO.Path.GetFileName(path); }
            catch { return path; }
        }
    }
}
