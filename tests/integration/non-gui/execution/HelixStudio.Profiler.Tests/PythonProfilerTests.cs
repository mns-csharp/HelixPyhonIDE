namespace HelixPythonNamespace.Profiler.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Profiler;

    [TestClass]
    public class PythonProfilerTests
    {
        // ---- ParseRecords (pure) ----

        [TestMethod]
        public void ParseRecords_MapsAllFields()
        {
            string json = "{\"records\":[{\"file\":\"/p/m.py\",\"line\":12,\"name\":\"work\"," +
                          "\"callCount\":5,\"totalSeconds\":0.25,\"cumulativeSeconds\":0.5}]}";
            string error;
            List<ProfileRecord> recs = PythonProfiler.ParseRecords(json, out error);
            Assert.IsNull(error);
            Assert.AreEqual(1, recs.Count);
            ProfileRecord r = recs[0];
            Assert.AreEqual("work", r.Name);
            Assert.AreEqual("/p/m.py", r.FilePath);
            Assert.AreEqual(12, r.Line);
            Assert.AreEqual(5, r.CallCount);
            Assert.AreEqual(0.25, r.TotalSeconds, 1e-9);
            Assert.AreEqual(0.5, r.CumulativeSeconds, 1e-9);
        }

        [TestMethod]
        public void ParseRecords_EmptyArray()
        {
            string error;
            List<ProfileRecord> recs = PythonProfiler.ParseRecords("{\"records\":[]}", out error);
            Assert.IsNull(error);
            Assert.AreEqual(0, recs.Count);
        }

        [TestMethod]
        public void ParseRecords_MissingKeyYieldsEmpty()
        {
            string error;
            List<ProfileRecord> recs = PythonProfiler.ParseRecords("{\"other\":1}", out error);
            Assert.IsNull(error);
            Assert.AreEqual(0, recs.Count);
        }

        [TestMethod]
        public void ParseRecords_MalformedJsonYieldsError()
        {
            string error;
            List<ProfileRecord> recs = PythonProfiler.ParseRecords("{not json", out error);
            Assert.IsNull(recs);
            Assert.IsNotNull(error);
            Assert.IsTrue(error.Contains("malformed JSON"));
        }

        [TestMethod]
        public void ParseRecords_MissingNumericFieldsDefaultToZero()
        {
            string error;
            List<ProfileRecord> recs = PythonProfiler.ParseRecords("{\"records\":[{\"name\":\"f\"}]}", out error);
            Assert.IsNull(error);
            Assert.AreEqual(1, recs.Count);
            Assert.AreEqual("f", recs[0].Name);
            Assert.AreEqual(0, recs[0].CallCount);
            Assert.AreEqual(0.0, recs[0].TotalSeconds, 1e-9);
        }

        [TestMethod]
        public void WrapperSource_EmbedsCProfileEntryPoints()
        {
            string src = PythonProfiler.WrapperSource;
            Assert.IsTrue(src.Contains("cProfile"));
            Assert.IsTrue(src.Contains("runpy.run_path"));
            Assert.IsTrue(src.Contains("pstats.Stats"));
            Assert.IsTrue(src.Contains("PROFILER_ERROR"));
        }

        [TestMethod]
        public void WriteWrapper_WritesParseableFile()
        {
            string path = PythonProfiler.WriteWrapper();
            Assert.IsNotNull(path);
            Assert.IsTrue(File.Exists(path));
            string content = File.ReadAllText(path);
            Assert.IsTrue(content.Contains("cProfile"));
        }

        // ---- End-to-end (requires a real interpreter) ----

        [TestMethod]
        public void EndToEnd_ProfilesRealScript()
        {
            string python = ResolvePython();
            if (python == null) { Assert.Inconclusive("No Python interpreter found on PATH."); return; }

            string dir = Path.Combine(Path.GetTempPath(), "helix_prof_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "target.py");
            File.WriteAllText(script,
                "def busy(n):\n" +
                "    total = 0\n" +
                "    for i in range(n):\n" +
                "        total += i * i\n" +
                "    return total\n" +
                "\n" +
                "if __name__ == '__main__':\n" +
                "    busy(100000)\n");

            ProfileFinishedEventArgs result = null;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                PythonProfiler profiler = new PythonProfiler();
                profiler.Finished += (s, e) => { result = e; done.Set(); };
                profiler.Start(python, script, dir);
                Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "Profiler did not finish in time.");
            }

            Assert.IsNotNull(result);
            Assert.AreEqual("", result.ErrorMessage, "Unexpected profiler error: " + result.ErrorMessage);
            Assert.IsTrue(result.Records.Count > 0, "Expected at least one profile record.");
            bool sawBusy = false;
            foreach (ProfileRecord r in result.Records) if (r.Name == "busy") sawBusy = true;
            Assert.IsTrue(sawBusy, "Expected the 'busy' function in profile records.");

            try { Directory.Delete(dir, true); } catch { }
        }

        // Regression: a profiled script that writes to stdout must NOT corrupt the results. The
        // wrapper used to dump JSON to stdout, so the script's own prints interleaved with it and
        // the parse failed ("malformed JSON: Unexpected character ..."). The JSON now goes to a file.
        [TestMethod]
        public void EndToEnd_ScriptThatPrintsStillProfilesCleanly()
        {
            string python = ResolvePython();
            if (python == null) { Assert.Inconclusive("No Python interpreter found on PATH."); return; }

            string dir = Path.Combine(Path.GetTempPath(), "helix_prof_print_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "target.py");
            File.WriteAllText(script,
                "def work():\n" +
                "    print('NOISE on stdout that would corrupt JSON')\n" +
                "    return sum(range(1000))\n" +
                "\n" +
                "if __name__ == '__main__':\n" +
                "    print('RESULT', work())\n");

            ProfileFinishedEventArgs result = null;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                PythonProfiler profiler = new PythonProfiler();
                profiler.Finished += (s, e) => { result = e; done.Set(); };
                profiler.Start(python, script, dir);
                Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "Profiler did not finish in time.");
            }

            Assert.IsNotNull(result);
            Assert.AreEqual("", result.ErrorMessage, "script stdout must not corrupt the JSON results: " + result.ErrorMessage);
            Assert.IsTrue(result.Records.Count > 0, "Expected profile records despite the script printing.");
            bool sawWork = false;
            foreach (ProfileRecord r in result.Records) if (r.Name == "work") sawWork = true;
            Assert.IsTrue(sawWork, "Expected the 'work' function in profile records.");

            try { Directory.Delete(dir, true); } catch { }
        }

        [TestMethod]
        public void EndToEnd_NonzeroExitReportsError()
        {
            string python = ResolvePython();
            if (python == null) { Assert.Inconclusive("No Python interpreter found on PATH."); return; }

            string dir = Path.Combine(Path.GetTempPath(), "helix_prof_err_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "boom.py");
            File.WriteAllText(script, "raise ValueError('boom')\n");

            ProfileFinishedEventArgs result = null;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                PythonProfiler profiler = new PythonProfiler();
                profiler.Finished += (s, e) => { result = e; done.Set(); };
                profiler.Start(python, script, dir);
                Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "Profiler did not finish in time.");
            }

            Assert.IsNotNull(result);
            Assert.AreNotEqual("", result.ErrorMessage);
            Assert.IsTrue(result.ErrorMessage.Contains("PROFILER_ERROR"));
            Assert.AreEqual(0, result.Records.Count);

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
