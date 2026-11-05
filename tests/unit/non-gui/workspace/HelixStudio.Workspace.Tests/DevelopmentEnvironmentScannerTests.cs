using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toolchains;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// Tools ▸ Development Env: what the machine scan reports, and — more importantly — what it
    /// refuses to claim. Every version banner below is real output from the tool it names, because
    /// the whole engine is a parser of other people's text.
    ///
    /// The runner is faked on purpose: a test that asserted "Rust is installed" would be asserting
    /// something about the machine running the suite, and would pass or fail for reasons that have
    /// nothing to do with this code.
    /// </summary>
    [TestClass]
    public class DevelopmentEnvironmentScannerTests
    {
        /// <summary>A machine with exactly the tools it is told about, answering exactly as asked.</summary>
        private sealed class FakeRunner : IToolProbeRunner
        {
            private readonly Dictionary<string, string> _installed =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ProbeRun> _answers =
                new Dictionary<string, ProbeRun>(StringComparer.OrdinalIgnoreCase);

            public readonly List<string> Launched = new List<string>();

            public FakeRunner With(string executable, string path, ProbeRun answer)
            {
                _installed[executable] = path;
                _answers[path] = answer;
                return this;
            }

            public FakeRunner Printing(string executable, string banner)
            {
                return With(executable, @"C:\tools\" + executable + ".exe", ProbeRun.Ran(banner));
            }

            public string Resolve(ToolProbe probe)
            {
                string path;
                return _installed.TryGetValue(probe.Executable, out path) ? path : null;
            }

            public ProbeRun Run(ToolProbe probe, string executablePath, CancellationToken token)
            {
                Launched.Add(executablePath);
                ProbeRun answer;
                return _answers.TryGetValue(executablePath, out answer) ? answer : ProbeRun.Failed("unexpected");
            }
        }

        private static ToolProbe Probe(string language, string component, string exe, string pattern,
            ProbeYield yield = ProbeYield.SingleVersion)
        {
            return new ToolProbe(language, component, exe, "--version", pattern, yield);
        }

        private static InstalledKit Find(DevelopmentEnvironmentReport report, string component)
        {
            foreach (KitGroup g in report.Groups)
                foreach (InstalledKit k in g.Kits)
                    if (k.Component == component) return k;
            return null;
        }

        private static KitGroup Group(DevelopmentEnvironmentReport report, string language)
        {
            foreach (KitGroup g in report.Groups) if (g.Language == language) return g;
            return null;
        }

        // ---------------------------------------------------------------- version parsing

        [TestMethod]
        public void EachToolsOwnBanner_YieldsItsVersion()
        {
            var runner = new FakeRunner()
                .Printing("python", "Python 3.12.1")
                // java prints its banner to STDERR; the runner hands both streams over as one blob.
                .Printing("java", "openjdk version \"21.0.2\"\nOpenJDK Runtime Environment")
                .Printing("node", "v20.11.0")
                .Printing("rustc", "rustc 1.75.0 (82e1608df)")
                .Printing("cmake", "cmake version 3.28.1\n\nCMake suite maintained and supported by Kitware")
                .Printing("go", "go version go1.22.0 windows/amd64")
                .Printing("nvcc", "nvcc: NVIDIA (R) Cuda compiler driver\nCuda compilation tools, release 12.4, V12.4.99");

            var probes = new List<ToolProbe>
            {
                Probe("Python", "CPython interpreter", "python", @"Python\s+(\d+\.\d+(?:\.\d+)?)"),
                Probe("Java", "JRE / JDK runtime", "java", "version\\s+\"?(\\d+\\.\\d+(?:\\.\\d+)?)"),
                Probe("JavaScript / TypeScript", "Node.js", "node", @"v?(\d+\.\d+(?:\.\d+)?)"),
                Probe("Rust", "rustc", "rustc", @"rustc\s+(\d+\.\d+(?:\.\d+)?)"),
                Probe("C / C++", "CMake", "cmake", @"cmake version\s+(\d+\.\d+(?:\.\d+)?)"),
                Probe("Go", "Go toolchain", "go", @"go(?:\s+version\s+go)?(\d+\.\d+(?:\.\d+)?)"),
                Probe("CUDA", "nvcc", "nvcc", @"release\s+(\d+\.\d+(?:\.\d+)?)"),
            };

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes).Scan(CancellationToken.None);

            Assert.AreEqual("3.12.1", Find(report, "CPython interpreter").Version);
            Assert.AreEqual("21.0.2", Find(report, "JRE / JDK runtime").Version, "java prints to stderr");
            Assert.AreEqual("20.11.0", Find(report, "Node.js").Version, "the leading v is not part of the version");
            Assert.AreEqual("1.75.0", Find(report, "rustc").Version);
            Assert.AreEqual("3.28.1", Find(report, "CMake").Version);
            Assert.AreEqual("1.22.0", Find(report, "Go toolchain").Version);
            Assert.AreEqual("12.4", Find(report, "nvcc").Version, "nvcc's version is on the 'release' line");
            Assert.IsTrue(report.IsCompleteScan);
            Assert.AreEqual(7, report.InstalledCount);
        }

        [TestMethod]
        public void TheCatalogsOwnPatterns_ReadTheRealBanners()
        {
            // Guards the shipped catalog, not a copy of it: a pattern edited into uselessness here
            // is the difference between "Rust 1.75.0" and a kit with no version at all.
            var expectations = new Dictionary<string, string[]>
            {
                { "CPython interpreter", new[] { "Python 3.12.1", "3.12.1" } },
                { "pip", new[] { "pip 24.0 from C:\\Python312\\Lib\\site-packages\\pip (python 3.12)", "24.0" } },
                { "GCC", new[] { "gcc (Rev3, Built by MSYS2 project) 13.2.0", "13.2.0" } },
                { "Clang", new[] { "clang version 17.0.6\nTarget: x86_64-pc-windows-msvc", "17.0.6" } },
                { "CMake", new[] { "cmake version 3.28.1", "3.28.1" } },
                { "nvcc", new[] { "Cuda compilation tools, release 12.4, V12.4.99", "12.4" } },
                { "dotnet CLI", new[] { "8.0.101", "8.0.101" } },
                { "JRE / JDK runtime", new[] { "openjdk version \"21.0.2\"", "21.0.2" } },
                { "javac compiler", new[] { "javac 21.0.2", "21.0.2" } },
                { "Maven", new[] { "Apache Maven 3.9.6 (bc0240f3c744dd6b6ec2920b3cd08dcc295161ae)", "3.9.6" } },
                { "rustc", new[] { "rustc 1.75.0 (82e1608df)", "1.75.0" } },
                { "Cargo", new[] { "cargo 1.75.0 (1d8b05cdd)", "1.75.0" } },
                { "Go toolchain", new[] { "go version go1.22.0 windows/amd64", "1.22.0" } },
                { "Node.js", new[] { "v20.11.0", "20.11.0" } },
                { "Julia", new[] { "julia version 1.10.0", "1.10.0" } },
                { "Ruby", new[] { "ruby 3.3.0 (revision 5124f9ac75) [x64-mingw-ucrt]", "3.3.0" } },
                { "PHP", new[] { "PHP 8.3.2 (cli) (ZTS Visual C++ 2019 x64)", "8.3.2" } },
                { "Git", new[] { "git version 2.43.0.windows.1", "2.43.0" } },
            };

            foreach (ToolProbe probe in ToolProbeCatalog.All())
            {
                string[] sample;
                if (!expectations.TryGetValue(probe.Component, out sample)) continue;
                string parsed = DevelopmentEnvironmentScanner.ParseVersion(probe.VersionPattern, sample[0]);
                Assert.AreEqual(sample[1], parsed,
                    probe.Component + " did not parse out of its own banner: " + sample[0]);
            }
        }

        [TestMethod]
        public void AToolThatRuns_ButPrintsSomethingUnrecognised_IsNotClaimedAsAVersion()
        {
            var runner = new FakeRunner().Printing("zig", "usage: zig [command] [options]");
            var probes = new List<ToolProbe> { Probe("Zig", "Zig", "zig", @"(\d+\.\d+\.\d+)") };

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes).Scan(CancellationToken.None);

            InstalledKit zig = Find(report, "Zig");
            Assert.AreEqual(KitOutcome.Inconclusive, zig.Outcome);
            Assert.IsNull(zig.Version, "no version was reported, so none may be shown");
            Assert.AreEqual(@"C:\tools\zig.exe", zig.Path, "it is still on disk, and the path is known");
            Assert.AreEqual(0, report.MissingGroups.Count,
                "a tool that ran is not missing — 'could not identify' is a third answer, not absence");
            Assert.IsTrue(Group(report, "Zig").IsUnverified);
        }

        // ---------------------------------------------------------------- absence

        [TestMethod]
        public void AToolThatIsNotOnPath_IsReportedMissing_AndNeverLaunched()
        {
            var runner = new FakeRunner();   // an empty machine
            var probes = new List<ToolProbe> { Probe("Rust", "rustc", "rustc", @"rustc\s+(\S+)") };

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes).Scan(CancellationToken.None);

            InstalledKit rustc = Find(report, "rustc");
            Assert.AreEqual(KitOutcome.NotFound, rustc.Outcome);
            Assert.IsNull(rustc.Path);
            Assert.AreEqual(0, runner.Launched.Count, "nothing to launch — resolution failed first");
            Assert.AreEqual(1, report.MissingGroups.Count);
            Assert.AreEqual(0, report.InstalledCount);
        }

        [TestMethod]
        public void AToolThatHangs_IsRecordedAsUnanswered_NotAsMissing()
        {
            var runner = new FakeRunner().With("mvn", @"C:\tools\mvn.cmd", ProbeRun.TimedOut());
            var probes = new List<ToolProbe> { Probe("Java", "Maven", "mvn", @"Maven\s+(\S+)") };

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes).Scan(CancellationToken.None);

            InstalledKit maven = Find(report, "Maven");
            Assert.AreEqual(KitOutcome.TimedOut, maven.Outcome);
            Assert.AreEqual(@"C:\tools\mvn.cmd", maven.Path);
            Assert.AreEqual(1, report.Unresolved.Count);
            Assert.AreEqual(0, report.MissingGroups.Count);
        }

        [TestMethod]
        public void ACancelledScan_SaysItIsIncomplete()
        {
            var runner = new FakeRunner().Printing("python", "Python 3.12.1");
            var probes = new List<ToolProbe>
            {
                Probe("Python", "CPython interpreter", "python", @"Python\s+(\S+)"),
                Probe("Rust", "rustc", "rustc", @"rustc\s+(\S+)"),
            };
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes).Scan(cancelled.Token);

            Assert.IsFalse(report.IsCompleteScan,
                "a scan that stopped early must not present its list as the machine's inventory");
        }

        // ---------------------------------------------------------------- multi-yield probes

        [TestMethod]
        public void DotnetListSdks_YieldsOneKitPerInstalledSdk()
        {
            var runner = new FakeRunner().Printing("dotnet",
                "6.0.418 [C:\\Program Files\\dotnet\\sdk]\r\n8.0.101 [C:\\Program Files\\dotnet\\sdk]");
            var probes = new List<ToolProbe>
            {
                Probe(".NET / C#", "SDK", "dotnet", null, ProbeYield.DotNetSdkList),
            };

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes).Scan(CancellationToken.None);

            Assert.AreEqual(2, report.InstalledCount);
            Assert.IsNotNull(Find(report, "SDK 6.0.418"));
            InstalledKit newest = Find(report, "SDK 8.0.101");
            Assert.AreEqual("8.0.101", newest.Version);
            Assert.AreEqual(@"C:\Program Files\dotnet\sdk\8.0.101", newest.Path,
                "the version folder is where that SDK actually lives");
        }

        [TestMethod]
        public void PythonLauncherListing_YieldsEveryRegisteredInterpreter()
        {
            var runner = new FakeRunner().Printing("py",
                " -V:3.12 *        C:\\Users\\dev\\AppData\\Local\\Programs\\Python\\Python312\\python.exe\r\n"
                + " -V:3.9           C:\\Python39\\python.exe");
            var probes = new List<ToolProbe>
            {
                Probe("Python", "Registered interpreters", "py", null, ProbeYield.PythonLauncherList),
            };

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes).Scan(CancellationToken.None);

            Assert.AreEqual(2, report.InstalledCount);
            InstalledKit primary = Find(report, "Python 3.12 (default)");
            Assert.IsNotNull(primary, "the launcher's * marks the default interpreter");
            Assert.AreEqual(@"C:\Users\dev\AppData\Local\Programs\Python\Python312\python.exe", primary.Path);
            Assert.IsNotNull(Find(report, "Python 3.9"));
        }

        // ---------------------------------------------------------------- providers

        private sealed class StubProvider : IKitProvider
        {
            private readonly IList<InstalledKit> _kits;
            public StubProvider(params InstalledKit[] kits) { _kits = new List<InstalledKit>(kits); }
            public string Language { get { return "Python"; } }
            public IList<InstalledKit> Discover(CancellationToken token) { return _kits; }
        }

        private sealed class ThrowingProvider : IKitProvider
        {
            public string Language { get { return "Python"; } }
            public IList<InstalledKit> Discover(CancellationToken token)
            {
                throw new InvalidOperationException("the registry blew up");
            }
        }

        [TestMethod]
        public void AProvidersKits_JoinTheirLanguage_WithoutRepeatingWhatThePathProbeFound()
        {
            var runner = new FakeRunner().With("python", @"C:\Python312\python.exe", ProbeRun.Ran("Python 3.12.1"));
            var probes = new List<ToolProbe> { Probe("Python", "CPython interpreter", "python", @"Python\s+(\S+)") };
            var provider = new StubProvider(
                // The same interpreter the PATH probe already found, reached another way.
                new InstalledKit("Python", "CPython 3.12.1 x86_64", "3.12.1", @"C:\Python312\python.exe",
                    KitOutcome.Installed, "found via registry HKLM"),
                // And one that is NOT on PATH at all — the case the provider exists for.
                new InstalledKit("Python", "Anaconda 3.11.7 x86_64", "3.11.7", @"C:\ProgramData\anaconda3\python.exe",
                    KitOutcome.Installed, "found via well-known"));

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes, new List<IKitProvider> { provider })
                    .Scan(CancellationToken.None);

            KitGroup python = Group(report, "Python");
            Assert.AreEqual(2, python.Kits.Count,
                "the duplicate path must be folded in, the registry-only interpreter must not be lost");
            Assert.IsNotNull(Find(report, "CPython interpreter"));
            Assert.IsNotNull(Find(report, "Anaconda 3.11.7 x86_64"));
        }

        [TestMethod]
        public void AProviderThatThrows_DoesNotTakeTheScanDownWithIt()
        {
            var runner = new FakeRunner().Printing("python", "Python 3.12.1");
            var probes = new List<ToolProbe> { Probe("Python", "CPython interpreter", "python", @"Python\s+(\S+)") };

            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(runner, probes, new List<IKitProvider> { new ThrowingProvider() })
                    .Scan(CancellationToken.None);

            Assert.AreEqual(1, report.InstalledCount, "what the probes found still stands");
            Assert.IsTrue(report.IsCompleteScan);
        }

        // ---------------------------------------------------------------- the catalog itself

        [TestMethod]
        public void EveryCatalogEntry_IsUsable()
        {
            var seen = new List<string>();
            foreach (ToolProbe probe in ToolProbeCatalog.All())
            {
                Assert.IsFalse(string.IsNullOrEmpty(probe.Language), "a probe with no language cannot be grouped");
                Assert.IsFalse(string.IsNullOrEmpty(probe.Component), "a probe with no component has no row to draw");
                Assert.IsFalse(string.IsNullOrEmpty(probe.Executable), "a probe with no executable resolves nothing");

                string key = probe.Language + "/" + probe.Component;
                Assert.IsFalse(seen.Contains(key), "two probes share the row " + key);
                seen.Add(key);

                if (probe.Yield != ProbeYield.SingleVersion) continue;
                Assert.IsFalse(string.IsNullOrEmpty(probe.VersionPattern),
                    probe.Component + " has no version pattern, so it could never report one");
                // A pattern that does not compile would silently downgrade every install to
                // "could not identify" at runtime.
                Assert.IsNull(DevelopmentEnvironmentScanner.ParseVersion(probe.VersionPattern, ""),
                    "an empty banner must not match " + probe.Component);
                new System.Text.RegularExpressions.Regex(probe.VersionPattern);
            }
            Assert.IsTrue(seen.Count > 25, "the catalog should cover the mainstream kits; it has " + seen.Count);
        }

        [TestMethod]
        public void EveryLanguageInTheCatalog_AppearsInTheReport_EvenWithNothingInstalled()
        {
            // The user asked what is installed; "Go: nothing here" is an answer, not an omission.
            DevelopmentEnvironmentReport report =
                new DevelopmentEnvironmentScanner(new FakeRunner()).Scan(CancellationToken.None);

            Assert.AreEqual(ToolProbeCatalog.Languages().Count, report.Groups.Count);
            Assert.AreEqual(report.Groups.Count, report.MissingGroups.Count);
            Assert.AreEqual(0, report.InstalledCount);
        }
    }
}
