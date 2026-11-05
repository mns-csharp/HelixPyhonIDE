namespace HelixPythonNamespace.CodeReview
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What kind of concern a finding is. These are reviewer categories, not rule-engine
    /// buckets: a reviewer decides whether to block a change on "this is wrong" long before
    /// "this is untidy", and the panel's default ordering follows that judgement.
    /// </summary>
    public enum ReviewCategory
    {
        Correctness,
        Security,
        ErrorHandling,
        Performance,
        Design,
        Maintainability,
        Documentation,
        Testing,
    }

    /// <summary>
    /// How much a finding should weigh on the verdict. Deliberately five levels rather than
    /// the linter's three: a review needs to separate "this must not ship" (Blocker) from
    /// "this is wrong and I am confident" (Critical), because only the first is a hard stop.
    /// </summary>
    public enum ReviewSeverity { Info, Minor, Major, Critical, Blocker }

    /// <summary>
    /// How sure the rule is. Every rule here is a heuristic over an AST with no type
    /// information, so a rule that cannot prove its claim must SAY so rather than dress a
    /// guess as a fact — confidence discounts the finding's weight in the verdict.
    /// </summary>
    public enum ReviewConfidence { Low, Medium, High }

    /// <summary>The reviewer's overall call on the code that was submitted.</summary>
    public enum ReviewVerdict { Approve, ApproveWithComments, RequestChanges }

    /// <summary>Roughly how much work the suggested fix is — what a reviewer writes in the margin.</summary>
    public enum ReviewEffort { Trivial, Small, Moderate, Large }

    /// <summary>An inclusive 1-based line range, as produced by a diff hunk.</summary>
    public struct LineRange
    {
        public readonly int Start;
        public readonly int End;

        public LineRange(int start, int end)
        {
            Start = start;
            End = end < start ? start : end;
        }

        public bool Contains(int line) { return line >= Start && line <= End; }

        public override string ToString() { return Start + "-" + End; }
    }

    /// <summary>
    /// One file submitted for review: its path, its full text, and — when the caller is
    /// reviewing a change rather than a whole tree — the lines that actually changed.
    /// </summary>
    public sealed class ReviewSource
    {
        public string FilePath;
        public string Text;

        /// <summary>
        /// The changed line ranges, when known. Null (not empty) means "the whole file is in
        /// scope"; an EMPTY list means "this file was submitted but nothing in it changed",
        /// which is a different statement and must not be confused with the first.
        /// </summary>
        public IReadOnlyList<LineRange> ChangedLines;

        public ReviewSource() { }

        public ReviewSource(string filePath, string text)
        {
            FilePath = filePath;
            Text = text;
        }

        public ReviewSource(string filePath, string text, IReadOnlyList<LineRange> changedLines)
        {
            FilePath = filePath;
            Text = text;
            ChangedLines = changedLines;
        }

        /// <summary>True when <paramref name="line"/> is inside the reviewed change, or when the whole file is in scope.</summary>
        public bool IsChanged(int line)
        {
            if (ChangedLines == null) return true;
            for (int i = 0; i < ChangedLines.Count; i++)
                if (ChangedLines[i].Contains(line)) return true;
            return false;
        }
    }

    /// <summary>
    /// One review comment. Message says what is wrong, Rationale says why a reviewer cares,
    /// Suggestion says what to do instead — the three parts of a review comment that is worth
    /// receiving. A rule that cannot fill all three is usually a lint rule, not a review rule.
    /// </summary>
    public sealed class ReviewFinding
    {
        public string RuleId;
        public string Title;
        public ReviewCategory Category;
        public ReviewSeverity Severity = ReviewSeverity.Major;
        public ReviewConfidence Confidence = ReviewConfidence.Medium;
        public ReviewEffort Effort = ReviewEffort.Small;

        public string FilePath;
        public int Line;
        public int EndLine;
        public int Column = 1;

        /// <summary>Dotted owner of the finding (<c>Class.method</c>), or empty at module level.</summary>
        public string MemberName = string.Empty;

        public string Message;
        public string Rationale;
        public string Suggestion;

        /// <summary>The offending source line, trimmed — what a diff comment would quote.</summary>
        public string Snippet = string.Empty;

        /// <summary>
        /// True when this sits inside the lines under review. On a whole-solution run every
        /// finding is "in scope"; on a changed-lines run this is what separates the comments the
        /// author caused from the ones they merely inherited.
        /// </summary>
        public bool InReviewedLines = true;

        /// <summary>Severity x confidence, boosted for findings the author actually introduced.</summary>
        public double Weight
        {
            get
            {
                double s;
                switch (Severity)
                {
                    case ReviewSeverity.Blocker: s = 100; break;
                    case ReviewSeverity.Critical: s = 40; break;
                    case ReviewSeverity.Major: s = 12; break;
                    case ReviewSeverity.Minor: s = 4; break;
                    default: s = 1; break;
                }
                double c;
                switch (Confidence)
                {
                    case ReviewConfidence.High: c = 1.0; break;
                    case ReviewConfidence.Medium: c = 0.7; break;
                    default: c = 0.4; break;
                }
                return s * c * (InReviewedLines ? 1.5 : 1.0);
            }
        }

        /// <summary>"file.py:12 — CR-SEC-002" — the identity used for de-duplication.</summary>
        public string Key
        {
            get { return (FilePath ?? string.Empty) + ":" + Line + ":" + Column + ":" + RuleId; }
        }
    }

    /// <summary>A file ranked by how much reviewer attention it needs.</summary>
    public sealed class ReviewHotspot
    {
        public string FilePath;
        public int FindingCount;
        public double Risk;
        public ReviewSeverity WorstSeverity;
    }

    /// <summary>The counted facts behind the verdict — what a reviewer would put in the summary line.</summary>
    public sealed class ReviewSummary
    {
        public int FilesSubmitted;
        public int FilesReviewed;
        public int FunctionsReviewed;
        public int ClassesReviewed;
        public int SourceLines;

        public readonly Dictionary<ReviewSeverity, int> BySeverity = new Dictionary<ReviewSeverity, int>();
        public readonly Dictionary<ReviewCategory, int> ByCategory = new Dictionary<ReviewCategory, int>();

        public int Count(ReviewSeverity severity)
        {
            int n;
            return BySeverity.TryGetValue(severity, out n) ? n : 0;
        }

        public int Count(ReviewCategory category)
        {
            int n;
            return ByCategory.TryGetValue(category, out n) ? n : 0;
        }
    }

    /// <summary>
    /// The result of one review pass.
    ///
    /// <see cref="Unparsed"/> is load-bearing, not diagnostics: a file the parser could not read
    /// contributed NO findings, and a report that quietly dropped it would read as "this file is
    /// clean". Every surface that shows a verdict must show this count too.
    /// </summary>
    public sealed class ReviewReport
    {
        public readonly List<ReviewFinding> Findings = new List<ReviewFinding>();
        public readonly ReviewSummary Summary = new ReviewSummary();
        public readonly List<ReviewHotspot> Hotspots = new List<ReviewHotspot>();

        /// <summary>Files that could not be parsed, with the reason. These were NOT reviewed.</summary>
        public readonly List<string> Unparsed = new List<string>();

        /// <summary>Rules that threw. A crashing rule is a gap in coverage, never a clean result.</summary>
        public readonly List<string> RuleFailures = new List<string>();

        public ReviewVerdict Verdict = ReviewVerdict.Approve;
        public string VerdictReason = string.Empty;

        /// <summary>Human label for what was submitted ("whole solution", "uncommitted changes", a file name).</summary>
        public string ScopeLabel = string.Empty;

        /// <summary>True when the run stopped early because the caller cancelled.</summary>
        public bool Cancelled;

        /// <summary>Total risk across every finding — the number the Hotspots list partitions.</summary>
        public double TotalRisk;

        /// <summary>
        /// True only when every submitted file parsed, no rule threw, and the run was not
        /// cancelled. A caller may only say "the code is clean" when this holds AND there are no
        /// findings; anything less is "nothing was reported by the checks that ran".
        /// </summary>
        public bool IsCompleteReview
        {
            get { return !Cancelled && Unparsed.Count == 0 && RuleFailures.Count == 0; }
        }
    }

    /// <summary>Tunables a caller may want to move; the defaults are the thresholds the rules document.</summary>
    public sealed class ReviewOptions
    {
        public int MaxFunctionStatements = 50;
        public int MaxFunctionParameters = 5;
        public int MaxNestingDepth = 4;
        public int MaxClassMethods = 20;
        public int MaxClassLines = 400;
        public int MaxCyclomaticComplexity = 12;
        public int MaxReturnPoints = 6;
        public int MaxBooleanOperands = 4;

        /// <summary>Statement count a repeated block needs before it is reported as duplication.</summary>
        public int DuplicateBlockStatements = 6;

        /// <summary>
        /// When true (the default for a diff review), findings outside the changed lines are
        /// kept but demoted to Info so the review leads with what the author actually wrote.
        /// </summary>
        public bool DemoteUnchangedFindings;

        public static ReviewOptions Default { get { return new ReviewOptions(); } }
    }
}
