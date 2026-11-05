using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Profiling
{
    /// <summary>One hot function/method from a real sampling profiler.</summary>
    public sealed class ProfileSample
    {
        public string Function = "";
        public int Samples;
        public double Percent;
        public string File = "";
        public int Line;
        public override string ToString() { return Function + "  " + Samples + " (" + Percent.ToString("0.0") + "%)"; }
    }

    /// <summary>
    /// Result of a real per-language sampling profiler. The shared shape the
    /// App maps to <c>ProfileRecord</c> so every language's hot functions surface in the same Profiler panel as
    /// Python's cProfile (FR-PROF-01). Samples rank the hot functions; wall time is estimated from the run.
    /// </summary>
    public sealed class WorkspaceProfile
    {
        public bool Available;
        public string Reason;
        public string Tool;                 // "jfr" | "julia" | "dotnet-trace"
        public double WallSeconds;          // total profiled wall time (for time estimates)
        public string RawOutput;
        public readonly List<ProfileSample> Samples = new List<ProfileSample>();

        public int TotalSamples { get { int n = 0; foreach (var s in Samples) n += s.Samples; return n; } }
        public bool Succeeded { get { return Available && Samples.Count > 0; } }
    }
}
