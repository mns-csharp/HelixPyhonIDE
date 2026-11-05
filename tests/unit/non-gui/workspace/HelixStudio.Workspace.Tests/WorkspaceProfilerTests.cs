using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// FR-PROF-05/06: verifies the sampling-profiler output parser HelixPython ships — the native
    /// C/C++ sampler that profiles the output of the Convert-to-C/C++ commands — against verbatim
    /// real output, mapping hot functions to <c>WorkspaceProfile</c>. Python itself profiles through
    /// HelixPythonNamespace.Profiler (cProfile), covered by that project's own suite. The JFR / Julia /
    /// dotnet-trace / cargo parsers moved to HelixJava, HelixJulia, HelixCSharp and HelixRust.
    /// </summary>
    [TestClass]
    public class WorkspaceProfilerTests
    {
        [TestMethod]
        public void MissingExecutable_IsReportedUnavailable_NotSilentlyEmpty()
        {
            var profile = HelixPythonNamespace.Workspace.Cpp.CppProfiler.Run(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "helixpython_no_such_binary.exe"));

            Assert.IsFalse(profile.Available, "a missing target executable must not report as a clean run");
            Assert.AreEqual(0, profile.Samples.Count);
            Assert.IsFalse(string.IsNullOrEmpty(profile.Reason), "the unavailability reason must be stated");
        }
    }
}
