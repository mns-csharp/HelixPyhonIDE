namespace HelixPythonNamespace.CodeReview
{
    using System.Collections.Generic;

    /// <summary>
    /// A rule that judges one file on its own. Rules append to <paramref name="output"/> rather
    /// than returning, so a rule that finds five things costs one call, and they never see each
    /// other's findings — a rule that needs the whole submission is an
    /// <see cref="ICrossFileReviewRule"/> instead.
    /// </summary>
    public interface IReviewRule
    {
        /// <summary>Stable identifier, <c>CR-&lt;CAT&gt;-&lt;NNN&gt;</c>. Appears in the panel and the exported report.</summary>
        string RuleId { get; }

        /// <summary>Short human name — the heading of the review comment.</summary>
        string Title { get; }

        ReviewCategory Category { get; }

        void Review(ReviewContext context, List<ReviewFinding> output);
    }

    /// <summary>
    /// Everything the per-file rules saw, for the judgements that only exist across files:
    /// import cycles, code duplicated between modules, a public class nothing tests.
    /// </summary>
    public sealed class ReviewSolutionContext
    {
        public readonly IReadOnlyList<ReviewContext> Files;
        public readonly ReviewOptions Options;

        public ReviewSolutionContext(IReadOnlyList<ReviewContext> files, ReviewOptions options)
        {
            Files = files ?? new List<ReviewContext>();
            Options = options ?? ReviewOptions.Default;
        }
    }

    /// <summary>A rule that needs every submitted file at once.</summary>
    public interface ICrossFileReviewRule
    {
        string RuleId { get; }
        string Title { get; }
        ReviewCategory Category { get; }
        void Review(ReviewSolutionContext context, List<ReviewFinding> output);
    }
}
