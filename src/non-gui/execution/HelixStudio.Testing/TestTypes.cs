namespace HelixPythonNamespace.Testing
{
    using System.Collections.Generic;

    public enum TestFramework
    {
        Pytest,
        Unittest,
    }

    public enum TestStatus
    {
        Pending,
        Running,
        Passed,
        Failed,
        Error,
        Skipped,
    }

    /// <summary>One test result row. Port of the C++ <c>TestNode</c>.</summary>
    public sealed class TestNode
    {
        public string Id = "";             // Stable identifier passed to the runner.
        public string Display = "";        // Short name shown in the tree.
        public string FilePath = "";       // Test source file when known.
        public int Line;
        public TestStatus Status = TestStatus.Pending;
        public double DurationSeconds;
        public string FailureMessage = "";

        public TestNode Clone()
        {
            return new TestNode
            {
                Id = Id,
                Display = Display,
                FilePath = FilePath,
                Line = Line,
                Status = Status,
                DurationSeconds = DurationSeconds,
                FailureMessage = FailureMessage,
            };
        }
    }

    /// <summary>Request describing a single test run. Port of the C++ <c>TestRunRequest</c>.</summary>
    public sealed class TestRunRequest
    {
        public TestFramework Framework = TestFramework.Pytest;
        public string InterpreterPath = "";        // Required.
        public string WorkingDirectory = "";
        public List<string> Targets = new List<string>();   // Files or node ids; empty = entire dir.
        public List<string> ExtraArgs = new List<string>();
    }
}
