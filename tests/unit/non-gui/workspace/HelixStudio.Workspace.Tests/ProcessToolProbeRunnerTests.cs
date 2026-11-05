using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toolchains;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// Finding a toolchain that is NOT on PATH.
    ///
    /// This is the failure these tests exist for: on the machine this was written on, rustup had
    /// installed the whole Rust kit into <c>%USERPROFILE%\.cargo\bin</c> and Zig was an unzipped
    /// folder in the profile — both were installed, and the Development Environment view reported
    /// both as "not found on this machine". A scan whose whole job is to answer "is it here?" had
    /// been asking the narrower question "is it on PATH?".
    ///
    /// Every test below resolves against a REAL directory tree in the temp folder rather than a
    /// stub filesystem: the thing that broke was the interaction between environment expansion,
    /// wildcard install roots and PATHEXT, and none of those are exercised by a fake.
    /// </summary>
    [TestClass]
    public class ProcessToolProbeRunnerTests
    {
        private string _root;

        [TestInitialize]
        public void CreateSandbox()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            // HELIX_PROBE_TEST_ROOT stands in for %USERPROFILE% / %ProgramFiles%: the catalog names
            // install roots through environment variables, so the expansion is what is under test.
            Environment.SetEnvironmentVariable("HELIX_PROBE_TEST_ROOT", _root);
        }

        [TestCleanup]
        public void RemoveSandbox()
        {
            Environment.SetEnvironmentVariable("HELIX_PROBE_TEST_ROOT", null);
            try { if (_root != null) Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string Tool(string relativeDirectory, string fileName)
        {
            string dir = Path.Combine(_root, relativeDirectory);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, fileName);
            File.WriteAllText(file, "not a real executable");
            return file;
        }

        private static ToolProbe ProbeIn(string executable, params string[] directories)
        {
            return new ToolProbe("Test", "tool", executable, "--version", null,
                ProbeYield.SingleVersion, null, directories);
        }

        [TestMethod]
        public void AToolInAWellKnownDirectory_IsFoundEvenThoughItIsNotOnPath()
        {
            // Shaped exactly like the Rust kit — a dot-prefixed directory in the profile — but
            // named after nothing real: PATH is searched first, so a probe for a tool this machine
            // actually has would resolve the machine's copy and assert nothing about the hint.
            string expected = Tool(@".cargo\bin", "helixrustc.exe");

            string resolved = new ProcessToolProbeRunner()
                .Resolve(ProbeIn("helixrustc", @"%HELIX_PROBE_TEST_ROOT%\.cargo\bin"));

            Assert.AreEqual(expected, resolved,
                "rustup installs outside PATH — a scan that only reads PATH calls Rust missing");
        }

        [TestMethod]
        public void AVersionedInstallRoot_IsMatchedWithAWildcard()
        {
            // The normal shape of a JDK, a CUDA toolkit, a PostgreSQL install: one directory per
            // version under a fixed parent, and no environment variable pointing at the one you want.
            // A name no real machine has: PATH is searched first, so probing for something this
            // machine genuinely installs would assert against the wrong copy of it.
            Tool(@"kits\9.0\bin", "other.exe");
            string expected = Tool(@"kits\21.0.2\bin", "helixc.exe");

            string resolved = new ProcessToolProbeRunner()
                .Resolve(ProbeIn("helixc", @"%HELIX_PROBE_TEST_ROOT%\kits\*\bin"));

            Assert.AreEqual(expected, resolved);
        }

        [TestMethod]
        public void ADirectoryNamingAVariableThisMachineDoesNotSet_IsDroppedRatherThanSearchedLiterally()
        {
            string resolved = new ProcessToolProbeRunner()
                .Resolve(ProbeIn("nothing-here", @"%HELIX_PROBE_NO_SUCH_VARIABLE%\bin"));

            Assert.IsNull(resolved, "an unset variable is not a directory, and must not become one");
        }

        [TestMethod]
        public void AToolThatIsOnNoneOfThem_IsStillReportedAsNotFound()
        {
            Tool(@"bin", "present.exe");

            Assert.IsNull(new ProcessToolProbeRunner()
                .Resolve(ProbeIn("absent", @"%HELIX_PROBE_TEST_ROOT%\bin")),
                "widening the search must not turn a miss into a hit");
        }

        [TestMethod]
        public void PathExtIsHonouredInAWellKnownDirectory_NotJustOnPath()
        {
            // kotlinc, lein, gradle and npm are .bat/.cmd shims; resolving only ".exe" would report
            // half the JVM world missing.
            string expected = Tool(@"tools", "helixkotlinc.bat");

            string resolved = new ProcessToolProbeRunner()
                .Resolve(ProbeIn("helixkotlinc", @"%HELIX_PROBE_TEST_ROOT%\tools"));

            Assert.AreEqual(expected, resolved,
                "and the path is the FILE's name: PATHEXT is upper-case by convention, so building "
                + "the answer out of it would hand the user a 'helixkotlinc.BAT' that is not what "
                + "Explorer shows");
        }

        [TestMethod]
        public void AnImpostorDirectoryIsSkipped_AndTheRealToolFurtherAlongIsFound()
        {
            // %SystemRoot%\System32\bash.exe is the WSL launcher, not Bash. It sits ahead of Git's
            // real bash and, with no distro installed, never answers — so the scan's honest report
            // was "present, but did not answer in time" for a kit that was installed and working.
            Tool(@"impostor", "helixbash.exe");
            string real = Tool(@"real", "helixbash.exe");

            var probe = new ToolProbe("Bash", "Bash", "helixbash", "--version", null,
                ProbeYield.SingleVersion, null,
                new[] { @"%HELIX_PROBE_TEST_ROOT%\impostor", @"%HELIX_PROBE_TEST_ROOT%\real" },
                null, new[] { @"%HELIX_PROBE_TEST_ROOT%\impostor" });

            Assert.AreEqual(real, new ProcessToolProbeRunner().Resolve(probe),
                "an excluded directory must be stepped over, not stopped at");
        }

        [TestMethod]
        public void AnInterpreterWithNoVersionFlag_IsAskedInItsOwnLanguage()
        {
            // tclsh has no --version: it reads a script from stdin. The probe carries the script,
            // and the runner must write it and then CLOSE the pipe — a REPL holding an open stdin
            // sits there until the timeout and gets reported as "did not answer in time".
            string cmd = Path.Combine(_root, "echoer.cmd");
            File.WriteAllText(cmd, "@echo off" + Environment.NewLine + "sort" + Environment.NewLine);

            var probe = new ToolProbe("Tcl", "tclsh", "echoer", "", @"(\d+\.\d+)",
                ProbeYield.SingleVersion, null, new[] { "%HELIX_PROBE_TEST_ROOT%" },
                "8.6" + "\r\n");   // sort echoes it back, proving the pipe was written and closed

            var runner = new ProcessToolProbeRunner(10000);
            string resolved = runner.Resolve(probe);
            Assert.IsNotNull(resolved, "the sandbox shim should have resolved");

            ProbeRun run = runner.Run(probe, resolved, CancellationToken.None);

            Assert.AreEqual(KitOutcome.Installed, run.Outcome,
                "it exited, so it ran — the version still has to parse, which is the scanner's job");
            StringAssert.Contains(run.Output, "8.6",
                "what the probe wrote to stdin came back out, so the pipe was written AND closed");
        }

        [TestMethod]
        public void EveryCatalogProbeCanBeResolvedWithoutThrowing()
        {
            // A malformed directory template would take down the whole scan on the machine that
            // happens to have that variable set. Cheap sweep, and it exercises every wildcard in
            // the catalog against this machine's real filesystem.
            var runner = new ProcessToolProbeRunner();
            var found = new List<string>();
            foreach (ToolProbe probe in ToolProbeCatalog.All())
            {
                string path = runner.Resolve(probe);
                if (path != null) found.Add(probe.Component + " -> " + path);
            }
            Assert.IsTrue(found.Count > 0,
                "a development machine running this suite has at least one toolchain: " + found.Count);
        }
    }
}
