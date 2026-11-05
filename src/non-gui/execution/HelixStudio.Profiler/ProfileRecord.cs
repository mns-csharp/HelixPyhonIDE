namespace HelixPythonNamespace.Profiler
{
    /// <summary>One per-function timing row captured from a cProfile run. Port of the C++ <c>ProfileRecord</c>.</summary>
    public sealed class ProfileRecord
    {
        public string Name = "";
        public string FilePath = "";
        public int Line;
        public int CallCount;
        public double TotalSeconds;
        public double CumulativeSeconds;
    }
}
