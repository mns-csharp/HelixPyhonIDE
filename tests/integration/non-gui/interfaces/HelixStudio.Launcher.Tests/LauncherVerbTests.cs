namespace HelixPythonNamespace.Launcher.Tests
{
    using System;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Launcher;

    /// <summary>
    /// The five verbs beside <c>run</c>: <c>--compile</c>, <c>--lint</c>, <c>--code-review</c>,
    /// <c>--profile</c>, <c>--run-test</c> — each with and without a package name, which is what
    /// the feature request asked for.
    ///
    /// These drive the REAL engines against real fixture solutions. Nothing is stubbed except the
    /// process launcher for <c>run</c>, which these tests do not use: the point of a verb test is
    /// that the engine behind it actually ran.
    /// </summary>
    [TestClass]
    public class LauncherVerbTests
    {
        private static string CleanFixture
        {
            get { return LauncherFixtures.CleanSolution; }
        }

        private static string SkewedFixture
        {
            get { return LauncherFixtures.SkewedSolution; }
        }

        private static LauncherFixtures.Result Run(params string[] args)
        {
            return LauncherFixtures.Run(args);
        }

        // ---- --compile -----------------------------------------------------------------

        [TestMethod]
        public void CompileReadsEveryFileInTheSolutionAndReportsClean()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--compile");

            Assert.AreEqual(0, r.Code, r.Err + r.Out);
            StringAssert.Contains(r.Out, "0 error(s)");
            StringAssert.Contains(r.Out, "6 file(s)", "the clean fixture has six .py files");
        }

        [TestMethod]
        public void CompileScopedToOnePackageReadsOnlyThatPackage()
        {
            LauncherFixtures.Result whole = Run("--solution", CleanFixture, "--compile");
            LauncherFixtures.Result scoped = Run("--solution", CleanFixture, "--compile", "calc_engine");

            Assert.AreEqual(0, scoped.Code, scoped.Err + scoped.Out);
            StringAssert.Contains(scoped.Out, "Compile (calc_engine)");
            StringAssert.Contains(scoped.Out, "2 file(s)");
            Assert.AreNotEqual(whole.Out, scoped.Out, "a scoped compile must not read the whole solution");
            // The narrowing has to be stated, or a clean scoped compile reads as a clean solution.
            StringAssert.Contains(scoped.Out, "Scoped to one package");
        }

        [TestMethod]
        public void CompileReportsPY8002AndExitsNonZero()
        {
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                File.WriteAllText(Path.Combine(copy.Path, "calc_engine", "stray.py"),
                    "def helper(value):\n    return value * 2\n");

                LauncherFixtures.Result r = Run("--solution", copy.Path, "--compile");

                Assert.AreEqual(SolutionLauncher.ExitFindings, r.Code);
                StringAssert.Contains(r.Out, "PY8002");
                StringAssert.Contains(r.Out, "stray.py");
            }
        }

        // ---- the gate applies to the EXECUTING verbs only -------------------------------

        [TestMethod]
        public void APY8004SolutionBlocksProfileAndRunTestButNotTheReadingVerbs()
        {
            // Blocked: these two execute the user's code, exactly as run does.
            foreach (string verb in new[] { "--profile", "--run-test" })
            {
                LauncherFixtures.Result blocked = Run("--solution", SkewedFixture, verb);
                Assert.AreEqual(SolutionLauncher.ExitBlocked, blocked.Code, verb + " should be blocked");
                StringAssert.Contains(blocked.Err, "do not match the Solution Explorer");
            }

            // Not blocked: refusing to compile-check source because it failed a compile-check
            // leaves the user no way to find out what is wrong, so these REPORT instead.
            foreach (string verb in new[] { "--compile", "--lint", "--code-review" })
            {
                LauncherFixtures.Result read = Run("--solution", SkewedFixture, verb);
                Assert.AreNotEqual(SolutionLauncher.ExitBlocked, read.Code,
                    verb + " must read the solution and report, not refuse to look");
            }
        }

        [TestMethod]
        public void CompileStillReportsThePY8004StateRatherThanClaimingClean()
        {
            LauncherFixtures.Result r = Run("--solution", SkewedFixture, "--compile");

            // No file-level error, but the structure is unverified — so this is not a clean compile
            // and must not report as one.
            StringAssert.Contains(r.Err, "PY8004");
            Assert.AreEqual(SolutionLauncher.ExitFindings, r.Code,
                "an unverified structure is not a clean compile whatever the file diagnostics said");
        }

        // ---- --lint --------------------------------------------------------------------

        [TestMethod]
        public void LintRunsOverTheSolutionAndOverOnePackage()
        {
            LauncherFixtures.Result whole = Run("--solution", CleanFixture, "--lint");
            Assert.AreEqual(0, whole.Code, whole.Err);
            StringAssert.Contains(whole.Out, "Lint (whole solution)");
            StringAssert.Contains(whole.Out, "6 file(s)");

            LauncherFixtures.Result scoped = Run("--solution", CleanFixture, "--lint", "calc_engine");
            Assert.AreEqual(0, scoped.Code, scoped.Err);
            StringAssert.Contains(scoped.Out, "Lint (calc_engine)");
            StringAssert.Contains(scoped.Out, "2 file(s)");
        }

        [TestMethod]
        public void LintActuallyReportsWhatItFinds()
        {
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                // A line the L001 length rule must object to — planted, so a silent linter fails
                // this test instead of passing it.
                File.WriteAllText(Path.Combine(copy.Path, "calc_engine", "wide.py"),
                    "class Wide:\n    VALUE = \"" + new string('x', 140) + "\"\n");

                LauncherFixtures.Result r = Run("--solution", copy.Path, "--lint");

                StringAssert.Contains(r.Out, "wide.py");
                StringAssert.Contains(r.Out, "L001");
            }
        }

        // ---- --code-review -------------------------------------------------------------

        [TestMethod]
        public void CodeReviewApprovesTheCleanFixtureAndSaysTheReviewWasComplete()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--code-review");

            Assert.AreEqual(0, r.Code, r.Err + r.Out);
            StringAssert.Contains(r.Out, "Approve");
            StringAssert.Contains(r.Out, "6 of 6 file(s) submitted");
            Assert.IsFalse(r.Err.Contains("INCOMPLETE"), "a complete review must not report itself incomplete");
        }

        [TestMethod]
        public void CodeReviewPrintsAllThreePartsOfEveryComment()
        {
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                // A bare `except Exception: pass` — CR-ERR-001, a Critical the engine is documented
                // to report. Planting it makes this a test of the engine, not of an empty report.
                File.WriteAllText(Path.Combine(copy.Path, "calc_engine", "swallow.py"),
                    "class Swallow:\n"
                  + "    @staticmethod\n"
                  + "    def go(action):\n"
                  + "        try:\n"
                  + "            return action()\n"
                  + "        except Exception:\n"
                  + "            pass\n");

                LauncherFixtures.Result r = Run("--solution", copy.Path, "--code-review");

                StringAssert.Contains(r.Out, "swallow.py");
                // A review comment is message + rationale + suggestion. A surface that shows only
                // the first has turned the review back into a lint pass.
                StringAssert.Contains(r.Out, "      why: ");
                StringAssert.Contains(r.Out, "      fix: ");
                Assert.AreEqual(SolutionLauncher.ExitFindings, r.Code,
                    "a Critical finding is Request changes, which a CI gate reads as non-zero");
            }
        }

        [TestMethod]
        public void CodeReviewScopesToOnePackage()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--code-review", "calc_engine");

            Assert.AreEqual(0, r.Code, r.Err + r.Out);
            StringAssert.Contains(r.Out, "package calc_engine");
            StringAssert.Contains(r.Out, "2 of 2 file(s) submitted");
        }

        // ---- --run-test ----------------------------------------------------------------

        [TestMethod]
        public void RunTestRunsPytestOverTheSolutionAndOverOnePackage()
        {
            LauncherFixtures.Result whole = Run("--solution", CleanFixture, "--run-test");
            if (whole.Code == SolutionLauncher.ExitNoInterpreter)
                Assert.Inconclusive("no Python interpreter on this machine");

            Assert.AreEqual(0, whole.Code, whole.Err + whole.Out);
            StringAssert.Contains(whole.Out, "2 passed");

            LauncherFixtures.Result scoped = Run("--solution", CleanFixture, "--run-test", "calc_tests");
            Assert.AreEqual(0, scoped.Code, scoped.Err + scoped.Out);
            StringAssert.Contains(scoped.Out, "Tests (calc_tests)");
            StringAssert.Contains(scoped.Out, "2 passed");
        }

        [TestMethod]
        public void RunTestReturnsPytestsOwnExitCodeWhenATestFails()
        {
            using (var copy = new LauncherFixtures.FixtureCopy(CleanFixture))
            {
                File.WriteAllText(Path.Combine(copy.Path, "calc_tests", "test_planted.py"),
                    "def test_planted_failure():\n    assert 1 == 2\n");

                LauncherFixtures.Result r = Run("--solution", copy.Path, "--run-test");
                if (r.Code == SolutionLauncher.ExitNoInterpreter)
                    Assert.Inconclusive("no Python interpreter on this machine");

                // pytest exits 1 when tests failed. Passing that through is what lets a CI script
                // tell "tests failed" from "pytest could not collect anything" (exit 5).
                Assert.AreEqual(1, r.Code, r.Out + r.Err);
                StringAssert.Contains(r.Out, "1 failed");
            }
        }

        // ---- --profile -----------------------------------------------------------------

        [TestMethod]
        public void ProfileRunsTheEntryPointAndReportsFunctions()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--profile");
            if (r.Code == SolutionLauncher.ExitNoInterpreter)
                Assert.Inconclusive("no Python interpreter on this machine");

            Assert.AreEqual(0, r.Code, r.Err + r.Out);
            StringAssert.Contains(r.Out, "cumtime");
            StringAssert.Contains(r.Out, "function(s).");
        }

        [TestMethod]
        public void ProfileRefusesProgramArgumentsRatherThanDroppingThem()
        {
            // The profiler wrapper takes the target and nothing else, so argv would be accepted and
            // silently discarded — running a different program than the one asked for.
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--profile", "--", "--flag");

            Assert.AreEqual(SolutionLauncher.ExitUsage, r.Code);
            StringAssert.Contains(r.Err, "cannot pass arguments");
        }

        // ---- command line ---------------------------------------------------------------

        [TestMethod]
        public void TwoActionsAreRefusedRatherThanSilentlyPickingOne()
        {
            LauncherFixtures.Result r = Run("--solution", CleanFixture, "--compile", "--lint");

            Assert.AreEqual(SolutionLauncher.ExitUsage, r.Code);
            StringAssert.Contains(r.Err, "only one action at a time");
        }

        [TestMethod]
        public void AVerbsInlineValueIsAPackageNameNeverASolutionPath()
        {
            // `helixstudio --compile <path>` must still mean "compile that solution", or the
            // shortest useful form of the command would be a usage error.
            LauncherFixtures.Result byPath = Run("--compile", CleanFixture);
            Assert.AreEqual(0, byPath.Code, byPath.Err + byPath.Out);
            StringAssert.Contains(byPath.Out, "Compile (whole solution)");

            // …while a bare name that is not a path is the package.
            LauncherFixtures.Result byName = Run("--solution", CleanFixture, "--compile", "calc_app");
            StringAssert.Contains(byName.Out, "Compile (calc_app)");
        }

        [TestMethod]
        public void EveryVerbAcceptsThePackageThroughTheSharedSelectorToo()
        {
            // --package, --execute-package and --execute_package are one slot, so a caller who
            // learned the spelling from the run examples can use it with any action.
            foreach (string selector in new[] { "--package", "--execute-package", "--execute_package" })
            {
                LauncherFixtures.Result r = Run("--solution", CleanFixture, "--lint", selector, "calc_engine");
                Assert.AreEqual(0, r.Code, selector + ": " + r.Err);
                StringAssert.Contains(r.Out, "Lint (calc_engine)");
            }
        }

        [TestMethod]
        public void AnUnknownPackageIsRefusedForEveryVerbNotJustRun()
        {
            foreach (string verb in new[] { "--compile", "--lint", "--code-review", "--run-test", "--profile" })
            {
                LauncherFixtures.Result r = Run("--solution", CleanFixture, verb, "--package", "no_such_package");
                Assert.AreEqual(SolutionLauncher.ExitUsage, r.Code, verb);
                StringAssert.Contains(r.Err, "no_such_package");
            }
        }

        [TestMethod]
        public void DryRunDescribesEachVerbWithoutDoingIt()
        {
            LauncherFixtures.Result compile = Run("--solution", CleanFixture, "--compile", "--dry-run");
            Assert.AreEqual(0, compile.Code, compile.Err);
            StringAssert.Contains(compile.Out, "file(s) would be read");
            Assert.IsFalse(compile.Out.Contains("error(s)"), "--dry-run must not actually compile");

            LauncherFixtures.Result tests = Run("--solution", CleanFixture, "--run-test", "--dry-run");
            Assert.AreEqual(0, tests.Code, tests.Err);
            StringAssert.Contains(tests.Out, "pytest");
            Assert.IsFalse(tests.Out.Contains("passed"), "--dry-run must not actually run pytest");
        }

        [TestMethod]
        public void TheHelpTextNamesEveryAction()
        {
            LauncherFixtures.Result r = Run();

            foreach (string flag in new[] { "--run", "--compile", "--lint", "--code-review", "--profile", "--run-test" })
                StringAssert.Contains(r.Out, flag, "help must name " + flag);
        }
    }
}
