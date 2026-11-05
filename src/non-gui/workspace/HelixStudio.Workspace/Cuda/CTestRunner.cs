using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Workspace.Testing;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// FR-TEST-03 (CMake) / FR-TEST-02 (CUDA): discover + run a configured build's tests with the REAL
    /// <c>ctest</c> (NFR-02), mapped to the shared <see cref="WorkspaceTestRun"/>. Serves both the CMake and the
    /// CUDA/CMake test rows since CUDA kernel tests are registered with CMake's <c>add_test</c> and run by ctest.
    /// </summary>
    public static class CTestRunner
    {
        public static bool IsAvailable { get { return FindCTest() != null; } }

        // "1/3 Test #1: adds_two ...............   Passed    0.01 sec"  / "...***Failed" / "...***Not Run"
        // The name is captured lazily up to the dot leader, NOT as \S+: Catch2 test names
        // routinely contain spaces ("BoundingBox dimensions"), and \S+ silently dropped every
        // such test from the panel — a 60-test Catch2 suite showed only its two
        // single-word-named tests.
        private static readonly Regex TestLine = new Regex(
            @"Test\s+#\d+:\s+(?<name>.+?)\s*\.{2,}\s*(?<res>\*\*\*Failed|\*\*\*Not Run|Passed|Failed)\b(?:.*?(?<sec>[\d.]+)\s*sec)?",
            RegexOptions.Compiled);

        /// <summary>
        /// Run the configured build's tests. <paramref name="onTest"/> is raised as each case is
        /// reported by ctest — ON THE READER THREAD, so callers must marshal to the UI themselves —
        /// which is what lets the Test Runner panel fill in one row at a time instead of sitting
        /// empty until the whole suite finishes. <paramref name="cancel"/> kills the ctest process.
        /// </summary>
        public static WorkspaceTestRun Run(string buildDir, int timeoutMs = 180000, string testNameRegex = null,
            Action<TestCaseResult> onTest = null, System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            var r = new WorkspaceTestRun { Tool = "ctest" };
            string ctest = FindCTest();
            if (ctest == null) { r.Reason = "ctest not found (install CMake)"; return r; }
            if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir)) { r.Reason = "build dir not found: " + buildDir; return r; }

            var runDirs = ResolveTestDirectories(Path.GetFullPath(buildDir));
            if (runDirs.Count == 0)
            {
                r.Reason = "no CTestTestfile.cmake under " + buildDir +
                    " — no add_test/gtest_discover_tests registered (or the project is not built)";
                return r;
            }

            var rawAll = new System.Text.StringBuilder();
            foreach (string dir in runDirs)
            {
                if (!RunOnce(ctest, dir, timeoutMs, testNameRegex, r, rawAll, onTest, cancel)) return r;
            }
            string combined = rawAll.ToString().Trim();
            r.RawOutput = combined.Length > 8000 ? combined.Substring(combined.Length - 8000) : combined;
            r.Available = true;
            return r;
        }

        // One real ctest invocation in one directory; false = fatal (Reason already set).
        //
        // Output is consumed line-by-line through the async Output/ErrorDataReceived events rather
        // than ReadToEnd(). Two reasons: a result is published the moment ctest prints it (the panel
        // used to stay empty for the whole run and then fill in one jump), and reading one pipe to
        // completion before touching the other deadlocks whenever the unread pipe fills — which a
        // large --output-on-failure suite can do.
        private static bool RunOnce(string ctest, string workingDir, int timeoutMs, string testNameRegex,
            WorkspaceTestRun r, System.Text.StringBuilder rawAll,
            Action<TestCaseResult> onTest, System.Threading.CancellationToken cancel)
        {
            try
            {
                string args = "--output-on-failure --no-compress-output";
                if (!string.IsNullOrEmpty(testNameRegex)) args += " -R \"" + testNameRegex + "\"";
                var psi = new ProcessStartInfo
                {
                    FileName = ctest, Arguments = args,
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true, WorkingDirectory = workingDir,
                };
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler onLine = delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;              // stream closed
                        lock (rawAll) rawAll.Append(e.Data).Append('\n');
                        TestCaseResult tc = ParseLine(e.Data);
                        if (tc == null) return;
                        lock (r) r.Tests.Add(tc);
                        if (onTest != null) { try { onTest(tc); } catch { /* a UI hiccup must not kill the run */ } }
                    };
                    p.OutputDataReceived += onLine;
                    p.ErrorDataReceived += onLine;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    // Poll rather than one long WaitForExit so Cancel stops the run promptly.
                    int waited = 0;
                    while (!p.WaitForExit(200))
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            try { p.Kill(); } catch { }
                            r.Reason = "ctest cancelled";
                            return false;
                        }
                        waited += 200;
                        if (waited >= timeoutMs)
                        {
                            try { p.Kill(); } catch { }
                            r.Reason = "ctest timed out";
                            return false;
                        }
                    }
                    p.WaitForExit();   // parameterless: also waits for the async readers to drain
                    if (p.ExitCode != 0) r.ExitCode = p.ExitCode;
                    return true;
                }
            }
            catch (Exception ex) { r.Reason = "ctest failed: " + ex.Message; r.Available = false; return false; }
        }

        /// <summary>
        /// The directories a run must invoke ctest in. Normally just the build root — but a root
        /// CMakeLists that never called <c>enable_testing()</c> gets no root CTestTestfile.cmake even
        /// when a test SUBPROJECT registered tests (the template's GTest project enables testing in
        /// its own directory only), and root ctest then reports "no test configuration file". This
        /// walks down to the TOPMOST directories that do have a CTestTestfile.cmake (skipping
        /// CMakeFiles/_deps, where FetchContent'd googletest keeps its own), so those solutions'
        /// tests still run. Public for deterministic testing.
        /// </summary>
        public static System.Collections.Generic.List<string> ResolveTestDirectories(string buildDir)
        {
            var found = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(buildDir) || !Directory.Exists(buildDir)) return found;
            Descend(buildDir, found);
            return found;
        }

        private static void Descend(string dir, System.Collections.Generic.List<string> found)
        {
            if (File.Exists(Path.Combine(dir, "CTestTestfile.cmake"))) { found.Add(dir); return; }
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (string sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (string.Equals(name, "CMakeFiles", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "_deps", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ".cmake", StringComparison.OrdinalIgnoreCase)) continue;
                Descend(sub, found);
            }
        }

        /// <summary>Parse <c>ctest</c> output into cases. Exposed for deterministic testing.</summary>
        public static WorkspaceTestRun ParseOutput(string output)
        {
            var r = new WorkspaceTestRun { Tool = "ctest", Available = true };
            ParseTests(output, r);
            return r;
        }

        private static void ParseTests(string text, WorkspaceTestRun r)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                TestCaseResult tc = ParseLine(raw);
                if (tc != null) r.Tests.Add(tc);
            }
        }

        /// <summary>
        /// One ctest output line to a case result, or null when the line is not a result line.
        /// Shared by the streaming reader and the whole-text <see cref="ParseOutput"/> so both
        /// recognise exactly the same lines.
        /// </summary>
        private static TestCaseResult ParseLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            Match m = TestLine.Match(raw);
            if (!m.Success) return null;
            string res = m.Groups["res"].Value;
            double sec = 0; if (m.Groups["sec"].Success) double.TryParse(m.Groups["sec"].Value, out sec);
            return new TestCaseResult
            {
                Name = m.Groups["name"].Value,
                Outcome = res == "Passed" ? "passed" : res.Contains("Not Run") ? "ignored" : "failed",
                DurationSeconds = sec,
            };
        }

        private static string FindCTest()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string c; try { c = Path.Combine(dir.Trim(), "ctest.exe"); } catch { continue; }
                if (File.Exists(c)) return c;
            }
            foreach (string root in new[] { @"C:\Program Files\CMake\bin", @"C:\Program Files (x86)\CMake\bin" })
            {
                string c = Path.Combine(root, "ctest.exe"); if (File.Exists(c)) return c;
            }
            return null;
        }
    }
}
