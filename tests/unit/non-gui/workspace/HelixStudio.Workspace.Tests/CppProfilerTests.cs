using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cpp;
using HelixPythonNamespace.Workspace.Profiling;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// FR-PROF (C++): C++ was the last shipped language with no profiler. The sampling itself needs a
    /// real process and a PDB, so it is proven by running against a compiled binary (see the session
    /// evidence: hot_spin 99.7% / light_touch 0.3% on an MSVC-built workload); what is pinned here is
    /// everything decidable without spawning one — the ranking maths and the refusal paths, which are
    /// what a caller sees when the environment is not ready.
    /// </summary>
    [TestClass]
    public class CppProfilerTests
    {
        [TestMethod]
        public void MissingExecutable_IsRefusedWithAReason_NotACrash()
        {
            WorkspaceProfile r = CppProfiler.Run(@"C:\definitely\not\here.exe");
            Assert.IsFalse(r.Succeeded);
            StringAssert.Contains(r.Reason, "not found");
        }

        [TestMethod]
        public void NullOrEmptyPath_IsRefusedWithAReason()
        {
            foreach (string p in new[] { null, "", "   " })
            {
                WorkspaceProfile r = CppProfiler.Run(p);
                Assert.IsFalse(r.Succeeded, "path: " + (p ?? "<null>"));
                Assert.IsFalse(string.IsNullOrEmpty(r.Reason), "a refusal must say why");
            }
        }

        /// <summary>
        /// The shared sampler ranks hottest-first and computes each function's share — the same
        /// maths Rust's profiler uses, now that both drive NativeSampler.
        /// </summary>
        [TestMethod]
        public void BuildProfile_RanksHottestFirstAndComputesPercentages()
        {
            var counts = new Dictionary<string, int> { { "light_touch", 4 }, { "hot_spin", 1426 } };
            WorkspaceProfile r = NativeSampler.BuildProfile(counts, 4.0);

            Assert.AreEqual(2, r.Samples.Count);
            Assert.AreEqual("hot_spin", r.Samples[0].Function, "hottest first");
            Assert.AreEqual(1426, r.Samples[0].Samples);
            Assert.AreEqual(99.7, r.Samples[0].Percent, 0.1);
            Assert.AreEqual("light_touch", r.Samples[1].Function);
            Assert.AreEqual(4.0, r.WallSeconds, 0.001);
            Assert.IsTrue(r.Available);
        }

        [TestMethod]
        public void BuildProfile_OnNoSamples_IsEmptyRatherThanDividingByZero()
        {
            WorkspaceProfile r = NativeSampler.BuildProfile(new Dictionary<string, int>(), 0.0);
            Assert.AreEqual(0, r.Samples.Count);
            Assert.IsFalse(r.Succeeded, "no samples is not a successful profile");
        }

        /// <summary>
        /// The frame filter decides what reaches the panel. CRT/OS scaffolding and the standard
        /// library are noise; the user's own functions are the point.
        /// </summary>
        [TestMethod]
        public void RuntimeFrames_AreRecognisedSoUserCodeIsWhatSurfaces()
        {
            foreach (string f in new[] { "[external]", "__scrt_common_main", "BaseThreadInitThunk", "RtlUserThreadStart" })
                Assert.IsTrue(NativeSampler.IsCommonRuntimeFrame(f), f + " is scaffolding");

            foreach (string f in new[] { "hot_spin", "MyApp::compute", "solve_system" })
                Assert.IsFalse(NativeSampler.IsCommonRuntimeFrame(f), f + " is user code");
        }

        [TestMethod]
        public void NewestExecutable_PicksTheMostRecentlyBuiltBinary()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix_prof_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string older = System.IO.Path.Combine(dir, "older.exe");
                string newer = System.IO.Path.Combine(dir, "newer.exe");
                System.IO.File.WriteAllText(older, "x");
                System.IO.File.WriteAllText(newer, "x");
                System.IO.File.SetLastWriteTimeUtc(older, System.DateTime.UtcNow.AddMinutes(-10));
                System.IO.File.SetLastWriteTimeUtc(newer, System.DateTime.UtcNow);

                Assert.AreEqual(newer, NativeSampler.NewestExecutable(dir));
                Assert.IsNull(NativeSampler.NewestExecutable(@"C:\no\such\dir"));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }
    }
}
