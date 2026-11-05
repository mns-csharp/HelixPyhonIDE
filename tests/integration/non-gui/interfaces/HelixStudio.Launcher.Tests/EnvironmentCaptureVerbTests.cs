namespace HelixPythonNamespace.Launcher.Tests
{
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Launcher;
    using HelixPythonNamespace.Workspace.Toolchains;

    /// <summary>
    /// The environment lock the launcher writes beside a solution's manifest on every run.
    ///
    /// The claim it supports is provenance: pyproject.toml says what the project asked for, and
    /// this says what answered. So the assertions here are about WHEN it appears — never for a run
    /// that was blocked, never when the caller opted out — because a lock file beside a solution
    /// that did not run would attribute a result to an environment that produced nothing.
    ///
    /// Every run here is against a COPY. The tracked fixtures are load-bearing for other suites and
    /// this verb writes into the solution directory.
    /// </summary>
    [TestClass]
    public class EnvironmentCaptureVerbTests
    {
        private static string CleanFixture
        {
            get { return LauncherFixtures.CleanSolution; }
        }

        private static string SkewedFixture
        {
            get { return LauncherFixtures.SkewedSolution; }
        }

        private static string LockIn(string directory)
        {
            return Path.Combine(directory, EnvironmentCapture.FileName);
        }

        /// <summary>
        /// A copy of a fixture with no lock file in it.
        ///
        /// Needed because a run legitimately WRITES one into the solution directory, and other
        /// suites run the executing verbs against the tracked fixture itself — so a copy can inherit
        /// a lock that this test's run did not produce. Measured: NoCaptureSkipsIt and
        /// ADryRunWritesNothingEither both passed alone and failed in the full suite for exactly
        /// that reason, which is the shape of a test asserting about somebody else's side effect.
        /// </summary>
        private static LauncherFixtures.FixtureCopy CleanCopy(string fixture)
        {
            var copy = new LauncherFixtures.FixtureCopy(fixture);
            try { File.Delete(LockIn(copy.Path)); } catch (IOException) { }
            return copy;
        }

        [TestMethod]
        public void ARunWritesTheEnvironmentBesideTheManifest()
        {
            using (var copy = CleanCopy(CleanFixture))
            {
                LauncherFixtures.Result r = LauncherFixtures.RunCapturing("--solution", copy.Path, "--run");

                Assert.AreEqual(0, r.Code, r.Err);
                Assert.IsTrue(File.Exists(LockIn(copy.Path)),
                    "environment.lock.toml should sit beside pyproject.toml");

                string toml = File.ReadAllText(LockIn(copy.Path));
                StringAssert.Contains(toml, "[capture]");
                StringAssert.Contains(toml, "[interpreter]");
                StringAssert.Contains(toml, "[toolchains]");
                StringAssert.Contains(toml, "product = \"HelixPython\"");
            }
        }

        [TestMethod]
        public void TheCaptureRecordsTheInterpreterTheRunResolved()
        {
            // A lock file naming a different Python from the one the run resolved would be
            // provenance in name only, so this asserts on the FILE rather than on stdout: these
            // tests drive a recording launcher, which never starts the program.
            using (var copy = CleanCopy(CleanFixture))
            {
                LauncherFixtures.Result r = LauncherFixtures.RunCapturing("--solution", copy.Path, "--run");
                Assert.AreEqual(0, r.Code, r.Err);

                string toml = File.ReadAllText(LockIn(copy.Path));
                StringAssert.Contains(toml, "outcome = \"identified\"",
                    "the interpreter on this machine should have identified itself");
                StringAssert.Contains(toml, "python", "the interpreter path is recorded");
            }
        }

        [TestMethod]
        public void NoCaptureSkipsIt()
        {
            // For a read-only checkout, and for a loop that runs the same solution hundreds of times.
            using (var copy = CleanCopy(CleanFixture))
            {
                LauncherFixtures.Result r = LauncherFixtures.RunCapturing("--solution", copy.Path, "--run", "--no-capture");

                Assert.AreEqual(0, r.Code, r.Err, "the run itself is unaffected");
                Assert.IsFalse(File.Exists(LockIn(copy.Path)));
            }
        }

        [TestMethod]
        public void ABlockedRunWritesNothing()
        {
            // Nothing executed, so there is nothing to attribute. A lock file here would say a
            // result came from this environment when no result came from anywhere.
            using (var copy = CleanCopy(SkewedFixture))
            {
                LauncherFixtures.Result r = LauncherFixtures.RunCapturing("--solution", copy.Path, "--run");

                Assert.AreEqual(SolutionLauncher.ExitBlocked, r.Code);
                Assert.IsFalse(File.Exists(LockIn(copy.Path)),
                    "a blocked run must not leave provenance for a run that did not happen");
            }
        }

        [TestMethod]
        public void ADryRunWritesNothingEither()
        {
            using (var copy = CleanCopy(CleanFixture))
            {
                LauncherFixtures.Result r = LauncherFixtures.RunCapturing("--solution", copy.Path, "--run", "--dry-run");

                Assert.AreEqual(0, r.Code, r.Err);
                Assert.IsFalse(File.Exists(LockIn(copy.Path)),
                    "--dry-run reports what WOULD happen; nothing ran, so nothing is attributed");
            }
        }

        [TestMethod]
        public void TheCaptureIsRewrittenOnEveryRun()
        {
            // "On every run" is the claim: a stale lock beside a fresh result is the failure mode
            // this whole file exists to avoid.
            using (var copy = CleanCopy(CleanFixture))
            {
                LauncherFixtures.RunCapturing("--solution", copy.Path, "--run");
                File.WriteAllText(LockIn(copy.Path), "# stale\n");

                LauncherFixtures.RunCapturing("--solution", copy.Path, "--run");

                string toml = File.ReadAllText(LockIn(copy.Path));
                Assert.IsFalse(toml.StartsWith("# stale"), "the second run should have replaced it");
                StringAssert.Contains(toml, "[capture]");
            }
        }
    }
}
