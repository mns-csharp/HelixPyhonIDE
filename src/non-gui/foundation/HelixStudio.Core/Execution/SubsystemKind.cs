namespace HelixPythonNamespace.Core.Execution
{
    /// <summary>
    /// Identifies one of the IDE's six long-running subsystems. Used by
    /// <see cref="SubsystemController"/> to track and stop running work uniformly.
    /// </summary>
    public enum SubsystemKind
    {
        VirtualCompiler,
        Linter,
        Profiler,
        Linker,
        Analyzer,
        TestRunner,
    }
}
