namespace HelixPythonNamespace.VirtualCompiler
{
    /// <summary>
    /// Per-call feature gates. Each pass can be disabled independently.
    /// Defaults match what ships in the most-recent phase.
    /// </summary>
    public sealed class CompileOptions
    {
        public bool EnableSyntaxPass { get; set; } = true;
        public bool EnableAstValidator { get; set; } = true;
        public bool EnableSymtablePass { get; set; } = true;
        public bool EnableCompileValidator { get; set; } = true;
        public bool EnableTypeChecker { get; set; } = true;

        /// <summary>
        /// Resolve every import against <see cref="SearchPath"/> and report what the interpreter
        /// would raise. Inert unless <see cref="SearchPath"/> is set — without a path model there
        /// is no ground truth to check against, so the pass would only be able to guess.
        /// </summary>
        public bool EnableImportResolution { get; set; } = true;

        /// <summary>
        /// The <c>sys.path</c> the compiled code will actually run under. Supplied by the caller
        /// that also builds the launcher's <c>PYTHONPATH</c>, so compile-time resolution and
        /// run-time resolution cannot disagree.
        /// </summary>
        public Imports.PythonSearchPath SearchPath { get; set; }

        /// <summary>
        /// Run the solution-policy pass (workspace membership + object orientation). Inert unless
        /// <see cref="Policy"/> is set, since both rules need the solution's member list.
        /// </summary>
        public bool EnableCompliancePass { get; set; } = true;

        /// <summary>The solution-wide rules to enforce as hard blockers.</summary>
        public Compliance.SolutionPolicy Policy { get; set; }

        public static CompileOptions Default => new CompileOptions();
    }
}
