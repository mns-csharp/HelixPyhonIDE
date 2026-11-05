namespace HelixPythonNamespace.Testing.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Testing;

    [TestClass]
    public class TestRunnerTests
    {
        // ---- BuildArguments (pure) ----

        [TestMethod]
        public void BuildArguments_QuotesWhitespaceArgs()
        {
            string s = TestRunner.BuildArguments(new List<string> { "-m", "pytest", @"C:\my tests\a.py" });
            Assert.AreEqual("-m pytest \"C:\\my tests\\a.py\"", s);
        }

        [TestMethod]
        public void BuildArguments_LeavesSimpleArgsUnquoted()
        {
            Assert.AreEqual("-m unittest -v", TestRunner.BuildArguments(new List<string> { "-m", "unittest", "-v" }));
        }

        [TestMethod]
        public void Start_EmptyInterpreterRaisesStartFailed()
        {
            TestRunner runner = new TestRunner();
            string reason = null;
            runner.StartFailed += r => reason = r;
            runner.Start(new TestRunRequest { Framework = TestFramework.Unittest, InterpreterPath = "" });
            Assert.IsNotNull(reason);
            Assert.IsFalse(runner.IsRunning);
        }

        // ---- End-to-end against the real unittest module ----

        [TestMethod]
        public void EndToEnd_RunsUnittestAndParsesResults()
        {
            string python = ResolvePython();
            if (python == null) { Assert.Inconclusive("No Python interpreter found on PATH."); return; }

            string dir = Path.Combine(Path.GetTempPath(), "helix_runner_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test_sample.py"),
                "import unittest\n" +
                "\n" +
                "class TestMath(unittest.TestCase):\n" +
                "    def test_passes(self):\n" +
                "        self.assertEqual(1 + 1, 2)\n" +
                "    def test_fails(self):\n" +
                "        self.assertEqual(1 + 1, 3)\n");

            int exitCode = -999;
            List<TestNode> allBatched = new List<TestNode>();
            using (ManualResetEventSlim finished = new ManualResetEventSlim(false))
            {
                TestRunner runner = new TestRunner();
                runner.TestsBatch += batch => { lock (allBatched) allBatched.AddRange(batch); };
                runner.Finished += code => { exitCode = code; finished.Set(); };
                runner.Start(new TestRunRequest
                {
                    Framework = TestFramework.Unittest,
                    InterpreterPath = python,
                    WorkingDirectory = dir,
                    Targets = new List<string> { "test_sample" },
                });
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(30)), "Runner did not finish in time.");
                // Give the final flush a moment (timer-driven batch).
                Thread.Sleep(150);
                runner.Stop();

                Assert.AreNotEqual(0, exitCode, "Expected a non-zero exit code from the failing test.");
                IReadOnlyList<TestNode> results = runner.Results;
                int passed = 0, failed = 0;
                foreach (TestNode n in results)
                {
                    if (n.Status == TestStatus.Passed) passed++;
                    if (n.Status == TestStatus.Failed) failed++;
                }
                Assert.IsTrue(passed >= 1, "Expected at least one passing test.");
                Assert.IsTrue(failed >= 1, "Expected at least one failing test.");
                Assert.IsTrue(runner.FailedIds().Count >= 1);
            }

            try { Directory.Delete(dir, true); } catch { }
        }

        private static string ResolvePython()
        {
            foreach (string name in new[] { "python", "python3" })
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit(5000);
                        if (p.HasExited && p.ExitCode == 0) return name;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
