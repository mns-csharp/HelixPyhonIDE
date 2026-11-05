using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Testing
{
    /// <summary>Outcome of one test case from any language's real test runner.</summary>
    public sealed class TestCaseResult
    {
        public string Name = "";
        public string Outcome = "passed";   // "passed" | "failed" | "ignored"
        public double DurationSeconds;
        public string Message = "";
        public override string ToString() { return Name + " -> " + Outcome; }
    }

    /// <summary>
    /// Result of a real per-language test run (cargo test / dotnet test / Pkg.test / ctest / mvn|gradle test).
    /// The shared shape the App maps to the language-agnostic <c>TestNode</c> tree in the Tests panel, so every
    /// language's tests surface in the SAME GUI Test Runner as Python's pytest (FR-TEST-01).
    /// </summary>
    public sealed class WorkspaceTestRun
    {
        public bool Available;              // false when the tool couldn't be located/invoked
        public string Reason;
        public string Tool;                 // "cargo" | "dotnet" | "julia" | "ctest" | "maven" | "gradle"
        public string ToolVersion;
        public int ExitCode;
        public string RawOutput;
        public readonly List<TestCaseResult> Tests = new List<TestCaseResult>();

        public int Passed { get { int n = 0; foreach (var t in Tests) if (t.Outcome == "passed") n++; return n; } }
        public int Failed { get { int n = 0; foreach (var t in Tests) if (t.Outcome == "failed") n++; return n; } }
        public int Ignored { get { int n = 0; foreach (var t in Tests) if (t.Outcome == "ignored") n++; return n; } }
        public bool Succeeded { get { return Available && Failed == 0 && Tests.Count > 0; } }
    }
}
