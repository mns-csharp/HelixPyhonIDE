namespace HelixPythonNamespace.Complexity
{
    using System.Collections.Generic;

    /// <summary>An input source file: path + full text.</summary>
    public struct ComplexitySource
    {
        public string FilePath;
        public string Text;

        public ComplexitySource(string filePath, string text)
        {
            FilePath = filePath;
            Text = text;
        }
    }

    /// <summary>
    /// A point on the fixed growth-rate ladder this subsystem models:
    /// O(1) &lt; O(log n) &lt; O(n) &lt; O(n log n) &lt; O(n^k) &lt; O(2^n). Kept as a structured
    /// value (rather than re-parsing the formatted "O(n^2)" strings) so callers — chiefly the
    /// Complexity panel's "Show Asymptotic Plot" context-menu action — can evaluate the curve
    /// directly instead of guessing its shape back out of display text.
    /// </summary>
    public enum GrowthOrderKind { Constant, Log, Linear, LinearLog, Poly, Exponential }

    /// <summary>
    /// One function/method's heuristic asymptotic classification (MIT 6.042J §13.7): best case
    /// (Ω, a lower bound achieved by some input), worst case (O, an upper bound achieved by some
    /// input), average case (Θ, tight bound — reported as the worst-case order when no conditional
    /// early exit was found to separate best from worst), and little-o (a strict comparison of the
    /// worst-case order against the next rung up a fixed growth-rate ladder).
    /// </summary>
    public sealed class ComplexityFinding
    {
        public string MemberName;
        public string FilePath;
        public int Line;
        public string BestCase;
        public string WorstCase;
        public string AverageCase;
        public string LittleO;

        /// <summary>Structured form of <see cref="WorstCase"/>, for plotting; <c>Degree</c> is only meaningful when <c>Kind == Poly</c>.</summary>
        public GrowthOrderKind WorstCaseKind;
        public int WorstCaseDegree;

        /// <summary>Structured form of <see cref="BestCase"/> — <c>Constant</c> when a conditional early exit was found, otherwise identical to the worst-case shape.</summary>
        public GrowthOrderKind BestCaseKind;
        public int BestCaseDegree;

        /// <summary>The AST evidence (loops/recursion/calls) that drove the classification.</summary>
        public string Evidence;

        /// <summary>A plain-English one-liner explaining the finding.</summary>
        public string Explanation;
    }

    public sealed class ComplexityReport
    {
        public List<ComplexityFinding> Findings = new List<ComplexityFinding>();
    }
}
