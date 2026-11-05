namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// SRS solution_explorer_rules §8.2: normal editing must never invoke the
    /// virtual compiler — heavy background analysis (the CUDA virtual compiler and
    /// the per-language linter/analyzer passes that run on typing pauses) is
    /// allowed only when the user explicitly enables it. The host sets this from
    /// the persisted setting at startup and from the Analysis menu toggle; the
    /// editor's diagnostics timer and the CUDA completion engine consult it.
    /// Plain parser-only re-parses (Python squiggles) are not gated — a parse is
    /// not a compile.
    /// </summary>
    public static class AnalysisGate
    {
        /// <summary>Off by default per §8.2 ("explicitly enabled by the user").</summary>
        public static bool BackgroundAnalysisEnabled;
    }
}
