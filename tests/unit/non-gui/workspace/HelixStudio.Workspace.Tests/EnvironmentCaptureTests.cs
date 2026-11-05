using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toolchains;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// The environment lock written beside pyproject.toml on every run.
    ///
    /// pyproject.toml says what a project ASKED for; this says what answered. The difference is the
    /// whole reproducibility claim, and it only holds if the record is honest when it cannot answer
    /// — so most of what is asserted here is the failure branches: an interpreter that did not
    /// identify itself, a Python with no pip, a toolchain scan that was cancelled.
    ///
    /// The interpreter is faked throughout. A test that asserted "Python 3.11 is installed" would be
    /// asserting something about the machine running the suite.
    /// </summary>
    [TestClass]
    public class EnvironmentCaptureTests
    {
        private readonly List<string> _temporary = new List<string>();

        [TestCleanup]
        public void TearDown()
        {
            foreach (string dir in _temporary)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        /// <summary>An interpreter that answers exactly what it is told to, or fails as instructed.</summary>
        private sealed class FakeInterpreter : IInterpreterProbe
        {
            private readonly Dictionary<string, ProbeRun> _answers =
                new Dictionary<string, ProbeRun>(StringComparer.Ordinal);

            public readonly List<string> Asked = new List<string>();
            public Exception Throw;

            public FakeInterpreter Answering(string arguments, ProbeRun run)
            {
                _answers[arguments] = run;
                return this;
            }

            public ProbeRun Run(string executable, string arguments, CancellationToken cancellationToken)
            {
                Asked.Add(arguments);
                if (Throw != null) throw Throw;
                ProbeRun run;
                return _answers.TryGetValue(arguments, out run) ? run : ProbeRun.Failed("not stubbed");
            }
        }

        private static FakeInterpreter HealthyPython()
        {
            return new FakeInterpreter()
                .Answering("-VV", ProbeRun.Ran(
                    "Python 3.12.1 (tags/v3.12.1:2305ca5) [MSC v.1937 64 bit (AMD64)]"))
                .Answering("-m pip freeze --all", ProbeRun.Ran("numpy==1.26.4\nmdtraj==1.11.1\npip==24.0\n"));
        }

        private static DevelopmentEnvironmentReport Toolchains(bool complete, params InstalledKit[] kits)
        {
            var groups = new List<KitGroup> { new KitGroup("Python", kits.ToList()) };
            return new DevelopmentEnvironmentReport(groups, complete);
        }

        private string Sandbox()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-env-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _temporary.Add(dir);
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"), "[project]\nname = \"demo\"\n");
            return dir;
        }

        // ---- the good case ---------------------------------------------------------------------

        [TestMethod]
        public void ACompleteCaptureRecordsWhatAnswered()
        {
            var toolchains = Toolchains(true,
                new InstalledKit("Python", "CPython interpreter", "3.12.1", @"C:\Python312\python.exe",
                                 KitOutcome.Installed, string.Empty));

            EnvironmentRecord record = EnvironmentCapture.Capture(
                @"C:\Python312\python.exe", toolchains, HealthyPython(), CancellationToken.None);

            Assert.IsTrue(record.IsComplete);
            Assert.IsNull(record.Incompleteness);
            Assert.AreEqual(KitOutcome.Installed, record.InterpreterOutcome);
            StringAssert.Contains(record.InterpreterVersion, "3.12.1");
            StringAssert.Contains(record.InterpreterVersion, "MSC v.1937",
                "-VV is asked for, not -V: the build is what tells two 3.12.1 installs apart");
            Assert.AreEqual(3, record.Packages.Count);
        }

        [TestMethod]
        public void TheRecordIsWrittenBesideTheManifest()
        {
            string dir = Sandbox();
            EnvironmentRecord record = EnvironmentCapture.Capture(
                @"C:\Python312\python.exe", Toolchains(true), HealthyPython(), CancellationToken.None);

            string written = EnvironmentCapture.WriteBeside(Path.Combine(dir, "pyproject.toml"), record);

            Assert.AreEqual(Path.Combine(dir, EnvironmentCapture.FileName), written);
            string toml = File.ReadAllText(written);
            StringAssert.Contains(toml, "complete = true");
            StringAssert.Contains(toml, "[[package]]");
            StringAssert.Contains(toml, "name = \"numpy\"");
            StringAssert.Contains(toml, "version = \"1.26.4\"");
        }

        // ---- the honesty rules, carried from the Development Environment tab ----------------------

        [TestMethod]
        public void AnInterpreterThatDidNotIdentifyItselfGetsNoVersion()
        {
            // The failure this guards: printing a version we did not obtain, in a file whose entire
            // purpose is to be believed later.
            var probe = new FakeInterpreter()
                .Answering("-VV", ProbeRun.Failed("Fatal Python error: init_fs_encoding"))
                .Answering("-m pip freeze --all", ProbeRun.Ran("numpy==1.26.4\n"));

            EnvironmentRecord record = EnvironmentCapture.Capture(
                @"C:\broken\python.exe", Toolchains(true), probe, CancellationToken.None);

            Assert.AreEqual(KitOutcome.Inconclusive, record.InterpreterOutcome);
            Assert.AreEqual(string.Empty, record.InterpreterVersion, "no version was obtained, so none is recorded");
            StringAssert.Contains(record.InterpreterDetail, "init_fs_encoding");
            Assert.IsFalse(record.IsComplete);
            StringAssert.Contains(record.Incompleteness, "interpreter was not identified");

            string toml = record.ToToml();
            StringAssert.Contains(toml, "outcome = \"unidentified\"");
            string interpreterSection = toml.Substring(toml.IndexOf("[interpreter]", StringComparison.Ordinal));
            interpreterSection = interpreterSection.Substring(0,
                interpreterSection.IndexOf("[packages]", StringComparison.Ordinal));
            Assert.IsFalse(interpreterSection.Contains("version ="),
                "no version line at all - an absent field cannot be misread, an invented one can");
            StringAssert.Contains(toml, "complete = false");
        }

        [TestMethod]
        public void APythonWithoutPipSaysSoRatherThanReportingNoPackages()
        {
            // "No packages" and "we could not list the packages" are different facts, and an empty
            // package list read as the first would be a false provenance.
            var probe = new FakeInterpreter()
                .Answering("-VV", ProbeRun.Ran("Python 3.12.1"))
                .Answering("-m pip freeze --all", ProbeRun.Failed("No module named pip"));

            EnvironmentRecord record = EnvironmentCapture.Capture(
                @"C:\Python312\python.exe", Toolchains(true), probe, CancellationToken.None);

            Assert.IsFalse(record.PackagesResolved);
            Assert.AreEqual(0, record.Packages.Count);
            StringAssert.Contains(record.PackagesDetail, "No module named pip");
            Assert.IsFalse(record.IsComplete);

            string toml = record.ToToml();
            StringAssert.Contains(toml, "resolved = false");
            StringAssert.Contains(toml, "count = 0");
            StringAssert.Contains(toml, "detail = \"No module named pip\"");
        }

        [TestMethod]
        public void ACancelledToolchainScanIsNotPresentedAsAnInventory()
        {
            EnvironmentRecord record = EnvironmentCapture.Capture(
                @"C:\Python312\python.exe", Toolchains(false), HealthyPython(), CancellationToken.None);

            Assert.IsFalse(record.ToolchainScanComplete);
            Assert.IsFalse(record.IsComplete);
            StringAssert.Contains(record.Incompleteness, "toolchain scan did not finish");
            StringAssert.Contains(record.ToToml(), "scan_complete = false");
        }

        [TestMethod]
        public void AnUnidentifiedToolIsRecordedAsUnidentified_NotAsMissing()
        {
            // The tab's first rule: an unidentifiable tool gets its own branch. A file that flattened
            // "we could not tell" into "not installed" would describe a different machine.
            var toolchains = Toolchains(true,
                new InstalledKit("Python", "Poetry", string.Empty, @"C:\tools\poetry.exe",
                                 KitOutcome.Inconclusive, "the version banner did not parse"),
                new InstalledKit("Python", "uv", string.Empty, string.Empty,
                                 KitOutcome.NotFound, "uv was not found on PATH"));

            string toml = EnvironmentCapture.Capture(
                @"C:\Python312\python.exe", toolchains, HealthyPython(), CancellationToken.None).ToToml();

            StringAssert.Contains(toml, "outcome = \"unidentified\"");
            StringAssert.Contains(toml, "detail = \"the version banner did not parse\"");
            StringAssert.Contains(toml, "outcome = \"not found\"");
            StringAssert.Contains(toml, "detail = \"uv was not found on PATH\"");
        }

        [TestMethod]
        public void WithNoInterpreterTheRecordSaysThatRatherThanGuessing()
        {
            EnvironmentRecord record = EnvironmentCapture.Capture(
                null, Toolchains(true), HealthyPython(), CancellationToken.None);

            Assert.AreEqual(KitOutcome.NotFound, record.InterpreterOutcome);
            Assert.IsFalse(record.IsComplete);
            StringAssert.Contains(record.InterpreterDetail, "no interpreter");
        }

        // ---- it must never break a run --------------------------------------------------------------

        [TestMethod]
        public void AProbeThatThrowsDoesNotTakeTheRunWithIt()
        {
            var probe = new FakeInterpreter { Throw = new InvalidOperationException("the pipe broke") };

            EnvironmentRecord record = EnvironmentCapture.Capture(
                @"C:\Python312\python.exe", Toolchains(true), probe, CancellationToken.None);

            Assert.AreEqual(KitOutcome.Inconclusive, record.InterpreterOutcome);
            StringAssert.Contains(record.InterpreterDetail, "the pipe broke");
            Assert.IsFalse(record.IsComplete);
        }

        [TestMethod]
        public void AnUnwritableLocationReturnsNullRatherThanThrowing()
        {
            EnvironmentRecord record = EnvironmentCapture.Capture(
                @"C:\Python312\python.exe", Toolchains(true), HealthyPython(), CancellationToken.None);

            string missing = Path.Combine(Path.GetTempPath(),
                "helix-no-such-" + Guid.NewGuid().ToString("N"), "pyproject.toml");
            Assert.IsNull(EnvironmentCapture.WriteBeside(missing, record),
                "a read-only or absent directory must not fail the run");
            Assert.IsNull(EnvironmentCapture.WriteBeside(null, record));
        }

        // ---- reading pip ------------------------------------------------------------------------------

        [TestMethod]
        public void PipFreezeIsParsedIntoNamesAndVersions()
        {
            IList<PackageRecord> packages = EnvironmentCapture.ParseFreeze(
                "numpy==1.26.4\nmdtraj==1.11.1\n\n# a comment\nscipy==1.11.4\n");

            Assert.AreEqual(3, packages.Count);
            Assert.AreEqual("numpy", packages[0].Name);
            Assert.AreEqual("1.26.4", packages[0].Version);
            Assert.AreEqual("scipy", packages[2].Name);
        }

        [TestMethod]
        public void AnEditableInstallIsKeptWithoutInventingAVersion()
        {
            // "-e git+https://..." has no version. Recording the left-hand side as one would put a
            // number in a lock file that nothing on disk agrees with.
            IList<PackageRecord> packages = EnvironmentCapture.ParseFreeze(
                "numpy==1.26.4\n-e git+https://github.com/x/y.git@abc#egg=y\n");

            Assert.AreEqual(2, packages.Count);
            Assert.AreEqual(string.Empty, packages[1].Version, "no version was stated, so none is claimed");
            StringAssert.Contains(packages[1].Name, "git+https");
        }

        [TestMethod]
        public void AWindowsPathSurvivesTheTomlQuoting()
        {
            // A path is full of backslashes and TOML basic strings escape them. Getting this wrong
            // makes the file unparseable, which is a silent failure the day someone tries to read it.
            Assert.AreEqual("\"C:\\\\Python312\\\\python.exe\"",
                EnvironmentRecord.Quote(@"C:\Python312\python.exe"));
            Assert.AreEqual("\"say \\\"hi\\\"\"", EnvironmentRecord.Quote("say \"hi\""));
            Assert.AreEqual("\"\"", EnvironmentRecord.Quote(null));
        }
    }
}
