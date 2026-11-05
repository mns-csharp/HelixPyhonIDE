namespace HelixPythonNamespace.CodeReview
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.CodeReview.Rules;
    using HelixPythonNamespace.Python;
    using HelixPythonNamespace.Python.Diagnostics;

    /// <summary>
    /// Reviews Python source the way a careful reviewer would: not "does this parse" (the virtual
    /// compiler answers that) and not "is this tidy" (the linter answers that), but "would I sign
    /// off on this, and what would I say".
    ///
    /// The engine is a rule host plus a scorer. Every rule is a heuristic over an AST with no type
    /// information, so three things are structural rather than incidental:
    ///
    /// * a rule that cannot prove its claim declares low <see cref="ReviewConfidence"/>, which
    ///   discounts its weight rather than its visibility — the reviewer still sees it;
    /// * a file that does not parse is recorded in <see cref="ReviewReport.Unparsed"/> and NOT
    ///   reviewed, because a report that quietly dropped it would read as "this file is clean";
    /// * a rule that throws is recorded in <see cref="ReviewReport.RuleFailures"/> and the review
    ///   is marked incomplete. A crashing check must never be indistinguishable from a passing one.
    /// </summary>
    public sealed class CodeReviewEngine
    {
        private readonly List<IReviewRule> _fileRules = new List<IReviewRule>();
        private readonly List<ICrossFileReviewRule> _solutionRules = new List<ICrossFileReviewRule>();

        /// <summary>A default-constructed engine carries the whole rule library.</summary>
        public CodeReviewEngine()
        {
            _fileRules.AddRange(CorrectnessRuleSet.All());
            _fileRules.AddRange(SecurityRuleSet.All());
            _fileRules.AddRange(ErrorHandlingRuleSet.All());
            _fileRules.AddRange(PerformanceRuleSet.All());
            _fileRules.AddRange(DesignRuleSet.All());
            _fileRules.AddRange(MaintainabilityRuleSet.All());
            _fileRules.AddRange(DocumentationRuleSet.All());
            _fileRules.AddRange(TestingRuleSet.All());
            _fileRules.AddRange(MachineLearningRuleSet.All());
            _solutionRules.AddRange(CrossFileRuleSet.All());
        }

        /// <summary>Every rule this engine will run — used by the panel's rule reference and by tests.</summary>
        public IReadOnlyList<IReviewRule> FileRules { get { return _fileRules; } }
        public IReadOnlyList<ICrossFileReviewRule> CrossFileRules { get { return _solutionRules; } }

        /// <summary>Total number of registered checks, per-file and cross-file.</summary>
        public int RuleCount { get { return _fileRules.Count + _solutionRules.Count; } }

        public void AddRule(IReviewRule rule) { if (rule != null) _fileRules.Add(rule); }
        public void AddRule(ICrossFileReviewRule rule) { if (rule != null) _solutionRules.Add(rule); }

        public ReviewReport Review(IReadOnlyList<ReviewSource> sources,
            CancellationToken cancel = default(CancellationToken))
        {
            return Review(sources, ReviewOptions.Default, null, cancel);
        }

        public ReviewReport Review(IReadOnlyList<ReviewSource> sources, ReviewOptions options,
            string scopeLabel, CancellationToken cancel = default(CancellationToken))
        {
            var report = new ReviewReport();
            report.ScopeLabel = scopeLabel ?? string.Empty;
            options = options ?? ReviewOptions.Default;
            if (sources == null) { Finish(report, options); return report; }

            report.Summary.FilesSubmitted = sources.Count;

            var contexts = new List<ReviewContext>();
            foreach (ReviewSource source in sources)
            {
                if (cancel.IsCancellationRequested) { report.Cancelled = true; break; }
                if (source == null || string.IsNullOrEmpty(source.FilePath)) continue;

                ParseResult parsed;
                try { parsed = Python.ParseModule(source.Text ?? string.Empty, source.FilePath, cancel); }
                catch (OperationCanceledException) { report.Cancelled = true; break; }
                catch (Exception ex)
                {
                    report.Unparsed.Add(source.FilePath + " — " + ex.Message);
                    continue;
                }

                // An error-recovered tree has holes in it, and a rule reading one would point at the
                // wrong symbol. Record the file as not reviewed instead of reviewing it badly.
                if (parsed.HasErrors)
                {
                    report.Unparsed.Add(source.FilePath + " — " + FirstError(parsed));
                    continue;
                }

                contexts.Add(new ReviewContext(source, parsed.Module, options));
            }

            var findings = new List<ReviewFinding>();

            foreach (ReviewContext context in contexts)
            {
                if (cancel.IsCancellationRequested) { report.Cancelled = true; break; }

                report.Summary.FilesReviewed++;
                report.Summary.FunctionsReviewed += context.Functions.Count;
                report.Summary.ClassesReviewed += context.Classes.Count;
                report.Summary.SourceLines += CountCodeLines(context);

                foreach (IReviewRule rule in _fileRules)
                {
                    if (cancel.IsCancellationRequested) { report.Cancelled = true; break; }
                    try { rule.Review(context, findings); }
                    catch (OperationCanceledException) { report.Cancelled = true; break; }
                    catch (Exception ex)
                    {
                        report.RuleFailures.Add(rule.RuleId + " on " + context.FilePath + " — " + ex.Message);
                    }
                }
            }

            if (!report.Cancelled)
            {
                var solutionContext = new ReviewSolutionContext(contexts, options);
                foreach (ICrossFileReviewRule rule in _solutionRules)
                {
                    if (cancel.IsCancellationRequested) { report.Cancelled = true; break; }
                    try { rule.Review(solutionContext, findings); }
                    catch (OperationCanceledException) { report.Cancelled = true; break; }
                    catch (Exception ex) { report.RuleFailures.Add(rule.RuleId + " — " + ex.Message); }
                }
            }

            Deduplicate(findings, options, report.Findings);
            Finish(report, options);
            return report;
        }

        // Two rules can legitimately reach the same conclusion at the same place (a bare `except`
        // that also swallows). Keeping both would inflate the risk score for one defect, so the
        // heavier finding wins and the lighter one is dropped.
        private static void Deduplicate(List<ReviewFinding> findings, ReviewOptions options, List<ReviewFinding> output)
        {
            var byKey = new Dictionary<string, ReviewFinding>(StringComparer.Ordinal);
            foreach (ReviewFinding finding in findings)
            {
                if (finding == null) continue;
                if (options.DemoteUnchangedFindings && !finding.InReviewedLines)
                {
                    // Context, not the author's work: keep it visible, stop it driving the verdict.
                    finding.Severity = ReviewSeverity.Info;
                }
                ReviewFinding existing;
                if (byKey.TryGetValue(finding.Key, out existing))
                {
                    if (finding.Weight > existing.Weight) byKey[finding.Key] = finding;
                    continue;
                }
                byKey[finding.Key] = finding;
            }
            output.AddRange(byKey.Values);
            output.Sort(CompareFindings);
        }

        // Severity first (a reviewer reads top-down and must not have to hunt for the blocker),
        // then weight, then source order so two runs over the same code produce the same list.
        private static int CompareFindings(ReviewFinding a, ReviewFinding b)
        {
            int bySeverity = ((int)b.Severity).CompareTo((int)a.Severity);
            if (bySeverity != 0) return bySeverity;
            int byWeight = b.Weight.CompareTo(a.Weight);
            if (byWeight != 0) return byWeight;
            int byFile = string.CompareOrdinal(a.FilePath ?? string.Empty, b.FilePath ?? string.Empty);
            if (byFile != 0) return byFile;
            int byLine = a.Line.CompareTo(b.Line);
            if (byLine != 0) return byLine;
            return string.CompareOrdinal(a.RuleId ?? string.Empty, b.RuleId ?? string.Empty);
        }

        private static void Finish(ReviewReport report, ReviewOptions options)
        {
            var risk = new Dictionary<string, ReviewHotspot>(StringComparer.OrdinalIgnoreCase);

            foreach (ReviewFinding finding in report.Findings)
            {
                int n;
                report.Summary.BySeverity.TryGetValue(finding.Severity, out n);
                report.Summary.BySeverity[finding.Severity] = n + 1;
                report.Summary.ByCategory.TryGetValue(finding.Category, out n);
                report.Summary.ByCategory[finding.Category] = n + 1;

                report.TotalRisk += finding.Weight;

                string path = finding.FilePath ?? string.Empty;
                ReviewHotspot hotspot;
                if (!risk.TryGetValue(path, out hotspot))
                {
                    hotspot = new ReviewHotspot { FilePath = path, WorstSeverity = finding.Severity };
                    risk[path] = hotspot;
                }
                hotspot.FindingCount++;
                hotspot.Risk += finding.Weight;
                if (finding.Severity > hotspot.WorstSeverity) hotspot.WorstSeverity = finding.Severity;
            }

            report.Hotspots.AddRange(risk.Values);
            report.Hotspots.Sort(delegate (ReviewHotspot a, ReviewHotspot b)
            {
                int byRisk = b.Risk.CompareTo(a.Risk);
                return byRisk != 0 ? byRisk : string.CompareOrdinal(a.FilePath, b.FilePath);
            });

            DecideVerdict(report);
        }

        private static void DecideVerdict(ReviewReport report)
        {
            int blockers = report.Summary.Count(ReviewSeverity.Blocker);
            int criticals = report.Summary.Count(ReviewSeverity.Critical);
            int majors = report.Summary.Count(ReviewSeverity.Major);
            int minors = report.Summary.Count(ReviewSeverity.Minor);

            if (blockers > 0)
            {
                report.Verdict = ReviewVerdict.RequestChanges;
                report.VerdictReason = Plural(blockers, "blocking finding") + " must be resolved before this ships.";
            }
            else if (criticals > 0)
            {
                report.Verdict = ReviewVerdict.RequestChanges;
                report.VerdictReason = Plural(criticals, "critical finding") + " — each is a defect, not a preference.";
            }
            else if (majors >= 5)
            {
                report.Verdict = ReviewVerdict.RequestChanges;
                report.VerdictReason = Plural(majors, "major finding") + " together outweigh a comment-only review.";
            }
            else if (majors > 0)
            {
                report.Verdict = ReviewVerdict.ApproveWithComments;
                report.VerdictReason = Plural(majors, "major finding") + " to address; nothing blocking.";
            }
            else if (minors >= 5)
            {
                report.Verdict = ReviewVerdict.ApproveWithComments;
                report.VerdictReason = Plural(minors, "minor finding") + " worth a pass before merge.";
            }
            else
            {
                report.Verdict = ReviewVerdict.Approve;
                report.VerdictReason = report.Findings.Count == 0
                    ? "No findings from " + report.Summary.FilesReviewed + " reviewed file(s)."
                    : "Only advisory findings.";
            }

            // An incomplete review cannot approve outright: files that were never read contributed
            // no findings, and their silence is absence of evidence, not evidence of absence.
            if (report.Cancelled)
            {
                report.Verdict = ReviewVerdict.ApproveWithComments;
                report.VerdictReason = "Review was cancelled before it finished — this is a partial result.";
            }
            else if (!report.IsCompleteReview && report.Verdict == ReviewVerdict.Approve)
            {
                report.Verdict = ReviewVerdict.ApproveWithComments;
                report.VerdictReason = IncompleteReason(report) + " Nothing was found in the files that were reviewed.";
            }
            else if (!report.IsCompleteReview)
            {
                report.VerdictReason += " " + IncompleteReason(report);
            }
        }

        private static string IncompleteReason(ReviewReport report)
        {
            var parts = new List<string>();
            if (report.Unparsed.Count > 0)
                parts.Add(Plural(report.Unparsed.Count, "file") + " could not be parsed and " +
                          (report.Unparsed.Count == 1 ? "was" : "were") + " not reviewed");
            if (report.RuleFailures.Count > 0)
                parts.Add(Plural(report.RuleFailures.Count, "check") + " failed to run");
            if (parts.Count == 0) return string.Empty;
            return "This review is incomplete: " + string.Join(", ", parts.ToArray()) + ".";
        }

        private static string Plural(int count, string noun)
        {
            return count + " " + noun + (count == 1 ? string.Empty : "s");
        }

        private static string FirstError(ParseResult parsed)
        {
            foreach (ParseDiagnostic diagnostic in parsed.Diagnostics)
                if (diagnostic.Severity >= ParseSeverity.Error)
                    return "line " + diagnostic.Span.Start.Line + ": " + diagnostic.Message;
            return "parse error";
        }

        private static int CountCodeLines(ReviewContext context)
        {
            int count = 0;
            foreach (string line in context.Lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && trimmed[0] != '#') count++;
            }
            return count;
        }
    }
}
