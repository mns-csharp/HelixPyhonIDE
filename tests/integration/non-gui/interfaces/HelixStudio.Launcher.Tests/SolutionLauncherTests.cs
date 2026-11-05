namespace HelixPythonNamespace.Launcher.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Launcher;

    /// <summary>
    /// The headless launcher, driven through <see cref="SolutionLauncher.Run"/> against REAL
    /// fixture solutions — the acceptance oracle this project uses, rather than source snippets a
    /// unit test injects.
    ///
    /// The one thing stubbed out is starting Python: <see cref="RecordingLauncher"/> captures the
    /// executable, argv, working directory and environment the launcher decided on, which is the
    /// whole of what this component contributes. That a real interpreter then runs the captured
    /// command is verified separately by <see cref="ItActuallyRunsTheProgramAndReturnsItsExitCode"/>,
    /// which uses the real <see cref="ProcessProgramLauncher"/>.
    /// </summary>
    [TestClass]
    public class SolutionLauncherTests
    {
        /// <summary>The parity-CLEAN half of the structure-parity pair: the one fixture that passes PY8004.</summary>
        private static string CleanFixture
        {
            get { return LauncherFixtures.CleanSolution; }
        }

        /// <summary>The SKEWED half: an <c>src/</c> grouping directory, so PY8004 must block it.</summary>
        private static string SkewedFixture
        {
            get { return LauncherFixtures.SkewedSolution; }
        }

        private static string FixtureRoot { get { return LauncherFixtures.Root; } }

        private static LauncherFixtures.Result Run(params string[] args)
        {
            return LauncherFixtures.Run(args);
        }

        // ---- the shape the feature request asked for ------------------------------------

        [TestMethod]
        public void TheRequestedCommandLineLaunchesTheNamedPackage()
        {
            // `helixstudio --solution <root pyproject.toml> --execute_package <name>` — the exact
            // spelling from the request, underscore and all.
            LauncherFixtures.Result r = Run("--solution", Path.Combine(CleanFixture, "pyproject.toml"),
                           "--execute_package", "calc_app");

            Assert.AreEqual(0, r.Code, r.Err);
            Assert.IsTrue(r.Launcher.Launched, "the launcher never started anything: " + r.Err);
            CollectionAssert.Contains(r.Launcher.Arguments, "-m",
                "an entry module inside a package must be launched with -m, never by path");
            CollectionAssert.Contains(r.Launcher.Arguments, "calc_app.main");
            Assert.AreEqual("-u", r.Launcher.Arguments[0], "output must be unbuffered");
        }

        [TestMethod]
        public void TheHyphenSpellingOfExecutePackageMeansTheSameThing()
        {
            LauncherFixtures.Result underscore = Run("--solution", CleanFixture, "--execute_package", "calc_app");
            LauncherFixtures.Result hyphen = Run("--solution", CleanFixture, "--execute-package", "calc_app");

            Assert.AreEqual(0, hyphen.Code, hyphen.Err);
            CollectionAssert.AreEqual(underscore.Launcher.Arguments, hyphen.Launcher.Arguments);
        }

        [TestMethod]
        public void TheSolutionMayBeGivenAsTheFolderRatherThanTheManifest()
        {
            LauncherFixtures.Result byFolder = Run("--solution", CleanFixture);
            LauncherFixtures.Result byManifest = Run("--solution", Path.Combine(CleanFixture, "pyproject.toml"));

            Assert.AreEqual(0, byFolder.Code, byFolder.Err);
            CollectionAssert.AreEqual(byManifest.Launcher.Arguments, byFolder.Launcher.Arguments);
        }

        [TestMethod]
        public void OmittingThePackageRunsTheStartupPackage()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture);

            Assert.AreEqual(0, r.Code, r.Err);
            // calc_app carries `startup = true`; calc_engine and calc_tests are not runnable.
            CollectionAssert.Contains(r.Launcher.Arguments, "calc_app.main");
        }

        [TestMethod]
        public void ItRunsInTheProjectFolderAndPutsEveryMemberOnPythonPath()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture);

            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(CleanFixture, "calc_app")).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(r.Launcher.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar),
                "VS parity: the program runs in its own project folder, not the solution root");

            string pythonPath;
            Assert.IsTrue(r.Launcher.Environment.TryGetValue("PYTHONPATH", out pythonPath),
                "PYTHONPATH must be set or the members are not importable");
            StringAssert.Contains(pythonPath, "calc_engine",
                "the library member has to be importable from the app");
        }

        // ---- the four hard blockers apply here too --------------------------------------

        [TestMethod]
        public void APY8004MismatchBlocksTheRunAndIsNotSilentlyRepaired()
        {
            // The skewed fixture groups its members under src/, which is the Depth defect.
            LauncherFixtures.Result r = Run("--solution", SkewedFixture);

            Assert.AreEqual(SolutionLauncher.ExitBlocked, r.Code);
            Assert.IsFalse(r.Launcher.Launched, "a PY8004-blocked solution must not run");
            StringAssert.Contains(r.Err, "do not match the Solution Explorer");
            // The remedy has to name a host that can actually act. A console cannot restructure the
            // user's directories, and telling them to accept a prompt that never appears here was
            // the dead end this wording exists to avoid.
            StringAssert.Contains(r.Err, "HelixStudio");
        }

        [TestMethod]
        public void APY8002ViolationBlocksTheRunAndNamesTheFileAndLine()
        {
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                // A module-level helper that is not main() — the fault, introduced in a COPY so the
                // tracked fixture keeps testing what it was written to test.
                File.WriteAllText(Path.Combine(copy.Path, "calc_engine", "stray.py"),
                    "def helper(value):\n    return value * 2\n");

                LauncherFixtures.Result r = Run("--solution", copy.Path);

                Assert.AreEqual(SolutionLauncher.ExitBlocked, r.Code);
                Assert.IsFalse(r.Launcher.Launched);
                StringAssert.Contains(r.Err, "PY8002");
                StringAssert.Contains(r.Err, "stray.py", "the user has to be told WHICH file");
            }
        }

        [TestMethod]
        public void AllowNonOopRelaxesRule2AndOnlyRule2()
        {
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                File.WriteAllText(Path.Combine(copy.Path, "calc_engine", "stray.py"),
                    "def helper(value):\n    return value * 2\n");

                LauncherFixtures.Result relaxed = Run("--solution", copy.Path, "--allow-non-oop");
                Assert.AreEqual(0, relaxed.Code, relaxed.Err);
                Assert.IsTrue(relaxed.Launcher.Launched);
            }

            // …and it does NOT relax PY8004, which has no opt-out in either host.
            LauncherFixtures.Result stillBlocked = Run("--solution", SkewedFixture, "--allow-non-oop");
            Assert.AreEqual(SolutionLauncher.ExitBlocked, stillBlocked.Code);
        }

        [TestMethod]
        public void ALibraryPackageIsRefusedRatherThanLaunched()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--execute-package", "calc_engine");

            Assert.AreEqual(SolutionLauncher.ExitBlocked, r.Code);
            Assert.IsFalse(r.Launcher.Launched);
            StringAssert.Contains(r.Err, "library rather than an application");
        }

        // ---- command line --------------------------------------------------------------

        [TestMethod]
        public void AnUnknownPackageNameListsTheRealOnes()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--execute-package", "no_such_package");

            Assert.AreEqual(SolutionLauncher.ExitUsage, r.Code);
            StringAssert.Contains(r.Err, "no_such_package");
            StringAssert.Contains(r.Err, "calc_app", "a wrong name is only useful next to the right ones");
        }

        [TestMethod]
        public void AMissingSolutionIsAUsageErrorNotACrash()
        {
            LauncherFixtures.Result r = Run("--solution", Path.Combine(Path.GetTempPath(), "helixstudio-no-such-solution"));

            Assert.AreEqual(SolutionLauncher.ExitUsage, r.Code);
            StringAssert.Contains(r.Err, "solution not found");
        }

        [TestMethod]
        public void NoArgumentsPrintsUsageAndSucceeds()
        {
            LauncherFixtures.Result r = Run();

            Assert.AreEqual(0, r.Code);
            StringAssert.Contains(r.Out, "--execute-package");
        }

        [TestMethod]
        public void ArgumentsAfterADoubleDashReplaceTheStoredRunArgs()
        {
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                string manifest = Path.Combine(copy.Path, "calc_app", "pyproject.toml");
                File.WriteAllText(manifest,
                    File.ReadAllText(manifest).Replace("startup = true",
                        "startup = true\nrun-args = \"--from-manifest\""));

                LauncherFixtures.Result stored = Run("--solution", copy.Path);
                CollectionAssert.Contains(stored.Launcher.Arguments, "--from-manifest");

                LauncherFixtures.Result overridden = Run("--solution", copy.Path, "--", "--from-command-line", "7");
                CollectionAssert.DoesNotContain(overridden.Launcher.Arguments, "--from-manifest",
                    "argv spelled out on the command line replaces run-args, it does not append to it");
                CollectionAssert.Contains(overridden.Launcher.Arguments, "--from-command-line");
                CollectionAssert.Contains(overridden.Launcher.Arguments, "7");
            }
        }

        [TestMethod]
        public void DryRunPrintsTheLaunchAndStartsNothing()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--dry-run");

            Assert.AreEqual(0, r.Code, r.Err);
            Assert.IsFalse(r.Launcher.Launched, "--dry-run must not start the program");
            StringAssert.Contains(r.Out, "calc_app.main");
            StringAssert.Contains(r.Out, "PYTHONPATH");
        }

        [TestMethod]
        public void ListPackagesNamesEveryMemberAndMarksTheStartup()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--list-packages");

            Assert.AreEqual(0, r.Code, r.Err);
            Assert.IsFalse(r.Launcher.Launched);
            StringAssert.Contains(r.Out, "calc_engine");
            StringAssert.Contains(r.Out, "calc_app");
            StringAssert.Contains(r.Out, "calc_tests");
            StringAssert.Contains(r.Out, "(startup)");
        }

        // ---- the one thing only a real process can prove --------------------------------

        [TestMethod]
        public void ItActuallyRunsTheProgramAndReturnsItsExitCode()
        {
            // Everything above stubs the process out, so nothing above would notice if the composed
            // command did not actually run. This one uses the real launcher on a copy whose entry
            // module exits with a code no default could produce.
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                File.WriteAllText(Path.Combine(copy.Path, "calc_app", "main.py"),
                    "import sys\n"
                  + "\n"
                  + "\n"
                  + "class Cli:\n"
                  + "    @staticmethod\n"
                  + "    def run():\n"
                  + "        print(\"hello from the launcher\")\n"
                  + "        sys.exit(23)\n"
                  + "\n"
                  + "\n"
                  + "def main():\n"
                  + "    Cli.run()\n"
                  + "\n"
                  + "\n"
                  + "if __name__ == \"__main__\":\n"
                  + "    main()\n");

                var outw = new StringWriter();
                var errw = new StringWriter();
                int code = SolutionLauncher.Run(
                    new[] { "--solution", copy.Path }, outw, errw, new ProcessProgramLauncher());

                if (code == SolutionLauncher.ExitNoInterpreter)
                    Assert.Inconclusive("no Python interpreter on this machine: " + errw);

                Assert.AreEqual(23, code,
                    "the program's own exit code must pass through unchanged. stderr: " + errw);
            }
        }

    }
}
