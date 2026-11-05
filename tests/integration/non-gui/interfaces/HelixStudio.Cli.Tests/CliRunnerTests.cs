namespace HelixPythonNamespace.Cli.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Cli;

    [TestClass]
    public class CliRunnerTests
    {
        private sealed class RunResult
        {
            public int ExitCode;
            public string Out;
            public string Err;
        }

        private static RunResult Run(params string[] args)
        {
            StringWriter o = new StringWriter();
            StringWriter e = new StringWriter();
            int rc = CliRunner.Run(new List<string>(args), o, e);
            return new RunResult { ExitCode = rc, Out = o.ToString(), Err = e.ToString() };
        }

        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_cli_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteFile(string dir, string name, string content)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }

        // ---- usage / argument handling ----

        [TestMethod]
        public void NoArgs_PrintsUsage_Exit2()
        {
            RunResult r = Run();
            Assert.AreEqual(2, r.ExitCode);
            Assert.IsTrue(r.Out.Contains("Usage:"));
        }

        [TestMethod]
        public void HelpFlag_Exit0()
        {
            Assert.AreEqual(0, Run("--help").ExitCode);
            Assert.AreEqual(0, Run("help").ExitCode);
            Assert.AreEqual(0, Run("-h").ExitCode);
        }

        [TestMethod]
        public void UnknownSubcommand_Exit2()
        {
            RunResult r = Run("frobnicate", "x");
            Assert.AreEqual(2, r.ExitCode);
            Assert.IsTrue(r.Err.Contains("unknown subcommand"));
        }

        [TestMethod]
        public void MissingTarget_Exit2()
        {
            RunResult r = Run("compile");
            Assert.AreEqual(2, r.ExitCode);
            Assert.IsTrue(r.Err.Contains("missing target"));
        }

        [TestMethod]
        public void UnknownFlag_Exit2()
        {
            RunResult r = Run("compile", "--bogus", "x");
            Assert.AreEqual(2, r.ExitCode);
            Assert.IsTrue(r.Err.Contains("unknown flag"));
        }

        [TestMethod]
        public void ExtraArg_Exit2()
        {
            RunResult r = Run("compile", "a", "b");
            Assert.AreEqual(2, r.ExitCode);
            Assert.IsTrue(r.Err.Contains("unexpected extra arg"));
        }

        [TestMethod]
        public void NoPyFiles_Exit1()
        {
            string dir = MakeTempDir();
            try
            {
                RunResult r = Run("compile", dir);
                Assert.AreEqual(1, r.ExitCode);
                Assert.IsTrue(r.Err.Contains("no .py files"));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- compile ----

        [TestMethod]
        public void Compile_CleanFile_Exit0()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "ok.py", "def f(x):\n    return x + 1\n");
                RunResult r = Run("compile", dir, "--quiet");
                Assert.AreEqual(0, r.ExitCode);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Compile_SyntaxError_ReportsErrorsWithoutCrashing()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "bad.py", "def f(:\n    pass\n");
                RunResult r = Run("compile", dir, "--quiet", "--json");
                Assert.IsTrue(r.ExitCode > 0, "Expected a non-zero error count.");
                Assert.IsTrue(r.Out.Contains("\"severity\":\"error\""));
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Compile_QuietSuppressesSummary()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "ok.py", "x = 1\n");
                RunResult r = Run("compile", dir, "--quiet");
                Assert.IsFalse(r.Err.Contains("compile:"));
                Assert.IsFalse(r.Err.Contains("elapsed:"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Compile_NonQuietEmitsSummaryAndElapsed()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "ok.py", "x = 1\n");
                RunResult r = Run("compile", dir);
                Assert.IsTrue(r.Err.Contains("compile:"));
                Assert.IsTrue(r.Err.Contains("elapsed:"));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- lint ----

        [TestMethod]
        public void Lint_CleanFile_Exit0()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "ok.py", "def f(x):\n    return x\n");
                RunResult r = Run("lint", dir, "--quiet");
                Assert.AreEqual(0, r.ExitCode);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- deadcode ----

        [TestMethod]
        public void DeadCode_FindsUnusedSymbols()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "m.py", "def used():\n    return helper()\n\ndef helper():\n    return 1\n\ndef never_called():\n    return 2\n");
                RunResult r = Run("deadcode", dir, "--quiet");
                Assert.AreEqual(0, r.ExitCode); // informational: never fails
                Assert.IsTrue(r.Out.Contains("never_called"), "Expected the uncalled function to be flagged.");
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void DeadCode_JsonOutput()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "m.py", "def never_called():\n    return 2\n");
                RunResult r = Run("deadcode", dir, "--quiet", "--json");
                Assert.IsTrue(r.Out.Contains("\"kind\":"));
                Assert.IsTrue(r.Out.Contains("\"symbol\":\"never_called\""));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- analyze ----

        [TestMethod]
        public void Analyze_FiresSolidViolationOnAbstractOnlyClass()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "shapes.py",
                    "from abc import ABC, abstractmethod\n" +
                    "\n" +
                    "class Shape(ABC):\n" +
                    "    @abstractmethod\n" +
                    "    def area(self):\n" +
                    "        ...\n" +
                    "\n" +
                    "class Circle(Shape):\n" +
                    "    def __init__(self, r):\n" +
                    "        self.r = r\n" +
                    "    def area(self):\n" +
                    "        return 3.14 * self.r * self.r\n");
                RunResult r = Run("analyze", dir, "--quiet");
                Assert.IsTrue(r.Out.Contains("ABS001"), "Expected the abstract-only-class rule to fire. Got:\n" + r.Out);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Analyze_JsonOutput()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "shapes.py",
                    "from abc import ABC, abstractmethod\n" +
                    "class Shape(ABC):\n" +
                    "    @abstractmethod\n" +
                    "    def area(self):\n" +
                    "        ...\n");
                RunResult r = Run("analyze", dir, "--quiet", "--json");
                Assert.IsTrue(r.Out.Contains("\"rule\":\"ABS001\""));
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Analyze_FilterRestrictsByRulePrefix()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "shapes.py",
                    "from abc import ABC, abstractmethod\n" +
                    "class Shape(ABC):\n" +
                    "    @abstractmethod\n" +
                    "    def area(self):\n" +
                    "        ...\n");
                RunResult r = Run("analyze", dir, "--quiet", "--filter=ZZZ");
                Assert.IsFalse(r.Out.Contains("ABS001"), "Filter ZZZ should suppress ABS001 lines.");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- helpers ----

        [TestMethod]
        public void CollectPyFiles_HandlesFileDirAndMissing()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "a.py", "x = 1\n");
                WriteFile(dir, "b.txt", "not python\n");
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                WriteFile(Path.Combine(dir, "sub"), "c.py", "y = 2\n");

                Assert.AreEqual(2, CliRunner.CollectPyFiles(dir).Count);                 // recursive, .py only
                Assert.AreEqual(1, CliRunner.CollectPyFiles(Path.Combine(dir, "a.py")).Count);
                Assert.AreEqual(0, CliRunner.CollectPyFiles(Path.Combine(dir, "b.txt")).Count);
                Assert.AreEqual(0, CliRunner.CollectPyFiles(Path.Combine(dir, "nope")).Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Todo_FindsTagsAndIgnoresStringLiterals()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "a.py",
                    "# TODO: wire this up\n" +
                    "x = 1  # FIXME later\n" +
                    "s = \"# TODO not a comment\"\n" +
                    "# HACK temporary\n");
                RunResult all = Run("todo", dir, "--quiet");
                Assert.AreEqual(0, all.ExitCode);                  // informational, never fails
                Assert.IsTrue(all.Out.Contains("TODO"), all.Out);
                Assert.IsTrue(all.Out.Contains("FIXME"), all.Out);
                Assert.IsTrue(all.Out.Contains("HACK"), all.Out);
                // The "# TODO" inside the string literal on line 3 must not be reported:
                // exactly two TODO-tagged lines exist (line 1 real, line 3 is a string).
                int todoLines = 0;
                foreach (string l in all.Out.Split('\n')) if (l.Contains(": TODO ")) todoLines++;
                Assert.AreEqual(1, todoLines, all.Out);

                RunResult fixmeOnly = Run("todo", dir, "--quiet", "--filter=FIXME");
                Assert.IsTrue(fixmeOnly.Out.Contains("FIXME"));
                Assert.IsFalse(fixmeOnly.Out.Contains("HACK"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void CollectPyFiles_SkipsVendorAndCacheDirs()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "real.py", "x = 1\n");
                foreach (string noise in new[] { ".venv", "build", "__pycache__", ".pytest_cache", "node_modules" })
                {
                    string nd = Path.Combine(dir, noise);
                    Directory.CreateDirectory(nd);
                    WriteFile(nd, "junk.py", "def x(:\n bad\n");
                }
                // Only the one real source file is collected; vendored/generated dirs are pruned.
                Assert.AreEqual(1, CliRunner.CollectPyFiles(dir).Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Metrics_ReportsPerFunctionComplexity()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "m.py",
                    "def branchy(a):\n" +
                    "    if a > 0:\n" +
                    "        return 1\n" +
                    "    elif a < 0:\n" +
                    "        return -1\n" +
                    "    return 0\n");
                RunResult r = Run("metrics", dir, "--quiet");
                Assert.AreEqual(0, r.ExitCode);                  // no gate set → never fails
                Assert.IsTrue(r.Out.Contains("branchy"), r.Out);
                Assert.IsTrue(r.Out.Contains("complexity 3"), r.Out);  // base 1 + if + elif
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Metrics_MaxComplexityGate_ExitsWithOverCount()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "m.py",
                    "def branchy(a):\n" +
                    "    if a > 0:\n" +
                    "        return 1\n" +
                    "    elif a < 0:\n" +
                    "        return -1\n" +
                    "    return 0\n");
                Assert.AreEqual(1, Run("metrics", dir, "--quiet", "--max-complexity=2").ExitCode); // 3 > 2
                Assert.AreEqual(0, Run("metrics", dir, "--quiet", "--max-complexity=3").ExitCode); // 3 == 3, not over
                Assert.AreEqual(2, Run("metrics", dir, "--max-complexity=abc").ExitCode);          // invalid → arg error
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void EscapeJson_EscapesControlAndQuotes()
        {
            Assert.AreEqual("a\\\"b", CliRunner.EscapeJson("a\"b"));
            Assert.AreEqual("a\\\\b", CliRunner.EscapeJson("a\\b"));
            Assert.AreEqual("a\\nb", CliRunner.EscapeJson("a\nb"));
            Assert.AreEqual("a\\tb", CliRunner.EscapeJson("a\tb"));
            Assert.AreEqual("\\u0001", CliRunner.EscapeJson(((char)1).ToString()));
        }

        [TestMethod]
        public void SeverityLabel_MapsAllLevels()
        {
            Assert.AreEqual("error", CliRunner.SeverityLabel(HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error));
            Assert.AreEqual("warning", CliRunner.SeverityLabel(HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Warning));
            Assert.AreEqual("info", CliRunner.SeverityLabel(HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Info));
        }

        // ---- debug subcommand ----

        [TestMethod]
        public void Debug_MissingScript_Exit4()
        {
            RunResult r = Run("debug", Path.Combine(MakeTempDir(), "nope.py"));
            Assert.AreEqual(4, r.ExitCode);
            Assert.IsTrue(r.Err.Contains("script not found"), r.Err);
        }

        [TestMethod]
        public void Debug_BadBreakSpec_Exit2()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "s.py", "print(1)\n");
                RunResult r = Run("debug", Path.Combine(dir, "s.py"), "--break=notaline");
                Assert.AreEqual(2, r.ExitCode);
                Assert.IsTrue(r.Err.Contains("bad --break"), r.Err);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Debug_BadTimeout_Exit2()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "s.py", "print(1)\n");
                Assert.AreEqual(2, Run("debug", Path.Combine(dir, "s.py"), "--timeout=abc").ExitCode);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Debug_BadStepMode_Exit2()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "s.py", "print(1)\n");
                RunResult r = Run("debug", Path.Combine(dir, "s.py"), "--step=sideways");
                Assert.AreEqual(2, r.ExitCode);
                Assert.IsTrue(r.Err.Contains("bad --step"), r.Err);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Debug_BadMaxStops_Exit2()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "s.py", "print(1)\n");
                Assert.AreEqual(2, Run("debug", Path.Combine(dir, "s.py"), "--max-stops=zero").ExitCode);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Debug_BadExceptionFilter_Exit2()
        {
            string dir = MakeTempDir();
            try
            {
                WriteFile(dir, "s.py", "print(1)\n");
                RunResult r = Run("debug", Path.Combine(dir, "s.py"), "--exceptions=bogus");
                Assert.AreEqual(2, r.ExitCode);
                Assert.IsTrue(r.Err.Contains("invalid --exceptions"), r.Err);
            }
            finally { Directory.Delete(dir, true); }
        }

        // Live end-to-end for exception breakpoints (pass #7). Self-skips without debugpy.
        // Guards that the engine's setExceptionBreakpoints filters are configurable — they
        // were hardcoded empty, so the debugger could never break on a Python exception.
        [TestMethod]
        public void Debug_LiveSession_ExceptionBreakpoints()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                // First division is caught; the second propagates uncaught.
                WriteFile(dir, "x.py",
                    "def div(a, b):\n" +            // 1
                    "    return a / b\n" +          // 2  <- raises ZeroDivisionError
                    "def main():\n" +               // 3
                    "    try:\n" +                  // 4
                    "        div(10, 0)\n" +        // 5  caught
                    "    except ZeroDivisionError:\n" +  // 6
                    "        print('CAUGHT')\n" +   // 7
                    "    div(1, 0)\n" +             // 8  uncaught
                    "main()\n");                    // 9
                string script = Path.Combine(dir, "x.py");

                // uncaught: only the propagating exception stops (once, at the raise site).
                RunResult unc = Run("debug", script, "--exceptions=uncaught", "--max-stops=4", "--timeout=40000");
                Assert.AreEqual(0, unc.ExitCode, "uncaught clean; out=" + unc.Out + " err=" + unc.Err);
                Assert.IsTrue(unc.Out.Contains("(exception)"), "should stop on the uncaught exception; out=" + unc.Out);
                Assert.IsTrue(unc.Err.Contains("1 stop(s)"), "uncaught stops exactly once; err=" + unc.Err);

                // raised: the caught exception stops too, so strictly more stops than uncaught.
                RunResult rai = Run("debug", script, "--exceptions=raised", "--max-stops=8", "--timeout=40000");
                Assert.AreEqual(0, rai.ExitCode, "raised clean; out=" + rai.Out + " err=" + rai.Err);
                Assert.IsTrue(rai.Out.Contains("stop #2"), "raised breaks on the caught exception too; out=" + rai.Out);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Live end-to-end for stop-on-entry, single-stepping, and watch expressions
        // (the pass-#6/#7 debugger surfaces). Self-skips when no python+debugpy.
        // stop-on-entry is synthesized as a breakpoint on the first executable line because
        // debugpy ignores stopOnEntry in attach mode — this guards that fix from regressing.
        [TestMethod]
        public void Debug_LiveSession_StopOnEntryStepAndWatch()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                // Line 1 is the module docstring; line 2 (`def f`) is the first executable
                // statement, so stop-on-entry must stop there, not in the docstring.
                WriteFile(dir, "t.py",
                    "\"\"\"module doc\"\"\"\n" +     // 1
                    "def f(n):\n" +                   // 2  <- entry point
                    "    s = 0\n" +                   // 3
                    "    for i in range(n):\n" +      // 4
                    "        s += i\n" +              // 5
                    "    return s\n" +                // 6
                    "print('RESULT', f(3))\n");       // 7
                string script = Path.Combine(dir, "t.py");

                // (a) stop-on-entry: one stop, on line 2, in <module>, before any output.
                RunResult entry = Run("debug", script, "--stop-on-entry", "--max-stops=1", "--timeout=40000");
                Assert.AreEqual(0, entry.ExitCode, "stop-on-entry clean; out=" + entry.Out + " err=" + entry.Err);
                Assert.IsTrue(entry.Out.Contains(":2 in <module>"), "entry should stop on line 2; out=" + entry.Out);
                Assert.IsTrue(entry.Err.Contains("1 stop(s)"), entry.Err);
                Assert.IsFalse(entry.Out.Contains("RESULT"), "entry stop precedes program output; out=" + entry.Out);

                // (b) step + watch: break on the loop body, single-step, evaluate expressions.
                RunResult step = Run("debug", script, "--break=5", "--step=over",
                                     "--watch=s", "--watch=i", "--max-stops=4", "--timeout=40000");
                Assert.AreEqual(0, step.ExitCode, "step clean; out=" + step.Out + " err=" + step.Err);
                Assert.IsTrue(step.Out.Contains("(step)"), "single-stepping should report step stops; out=" + step.Out);
                Assert.IsTrue(step.Out.Contains("watch[s="), "watch should evaluate s; out=" + step.Out);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // End-to-end: drives a real debugpy/DAP session. Self-skips (Inconclusive) when no
        // Python+debugpy is on PATH. Regression guard for the DAP handshake fix — if `attach`
        // ever blocks the handshake again, the breakpoint won't bind, hits==0, and the exit
        // code stops being 0 (it becomes 4/5/6), failing this test.
        [TestMethod]
        public void Debug_LiveSession_BindsBreakpointAndExitsClean()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                // Breakpoint on the loop body (line 4) — hit range(5) = 5 times.
                WriteFile(dir, "t.py",
                    "def f(n):\n" +
                    "    s = 0\n" +
                    "    for i in range(n):\n" +
                    "        s += i\n" +
                    "    return s\n" +
                    "print('RESULT', f(5))\n");
                string script = Path.Combine(dir, "t.py");
                RunResult r = Run("debug", script, "--break=4", "--timeout=40000");

                Assert.AreEqual(0, r.ExitCode, "expected clean run; out=" + r.Out + " err=" + r.Err);
                Assert.IsTrue(r.Out.Contains("stop #5"), "breakpoint should bind and hit 5x; out=" + r.Out);
                Assert.IsTrue(r.Out.Contains("RESULT 10"), "program output captured; out=" + r.Out);
                Assert.IsTrue(r.Err.Contains("1 breakpoint(s) requested, 5 stop(s)"), r.Err);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static bool DebugpyAvailable()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"import debugpy\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // ---- refactor (structural transforms) ----

        // Four methods across two cohesion clusters (balance / user): S1_SrpSplit matches,
        // but only 4 members so the SRP001 analysis rule (> 7 members) does NOT fire.
        private const string GodClassSmall =
            "class Account:\n"
            + "    def deposit(self, amount):\n"
            + "        self.balance += amount\n"
            + "\n"
            + "    def withdraw(self, amount):\n"
            + "        self.balance -= amount\n"
            + "\n"
            + "    def log_in(self, user):\n"
            + "        self.user = user\n"
            + "\n"
            + "    def log_out(self):\n"
            + "        self.user = None\n";

        // Eight members, so SRP001 fires and `--auto` has a hint to gate on.
        private const string GodClassLarge =
            "class Ledger:\n"
            + "    def deposit(self, amount):\n"
            + "        self.balance += amount\n"
            + "\n"
            + "    def withdraw(self, amount):\n"
            + "        self.balance -= amount\n"
            + "\n"
            + "    def freeze(self):\n"
            + "        self.balance = 0\n"
            + "\n"
            + "    def audit(self):\n"
            + "        return self.balance\n"
            + "\n"
            + "    def log_in(self, user):\n"
            + "        self.user = user\n"
            + "\n"
            + "    def log_out(self):\n"
            + "        self.user = None\n"
            + "\n"
            + "    def rename(self, user):\n"
            + "        self.user = user\n"
            + "\n"
            + "    def whoami(self):\n"
            + "        return self.user\n";

        [TestMethod]
        public void Refactor_ListsCandidates()
        {
            string dir = MakeTempDir();
            WriteFile(dir, "account.py", GodClassSmall);
            RunResult r = Run("refactor", Path.Combine(dir, "account.py"), "--quiet");
            Assert.AreEqual(0, r.ExitCode);
            StringAssert.Contains(r.Out, "S1_SrpSplit");
            StringAssert.Contains(r.Out, "S2_DipIntroduce");
        }

        [TestMethod]
        public void Refactor_JsonEmitsLossyFlag()
        {
            string dir = MakeTempDir();
            WriteFile(dir, "account.py", GodClassSmall);
            RunResult r = Run("refactor", Path.Combine(dir, "account.py"), "--json", "--quiet");
            Assert.AreEqual(0, r.ExitCode);
            StringAssert.Contains(r.Out, "\"kind\":\"S1_SrpSplit\"");
            StringAssert.Contains(r.Out, "\"lossy\":true");
            StringAssert.Contains(r.Out, "\"lossy\":false");  // S2_DipIntroduce is byte-exact
        }

        [TestMethod]
        public void Refactor_ApplyPrintsTransformedSource()
        {
            string dir = MakeTempDir();
            WriteFile(dir, "account.py", GodClassSmall);
            RunResult r = Run("refactor", Path.Combine(dir, "account.py"), "--apply", "--kind=srp", "--quiet");
            Assert.AreEqual(0, r.ExitCode);
            StringAssert.Contains(r.Out, "class AccountBalance:");
            StringAssert.Contains(r.Out, "class AccountUser:");
            Assert.IsFalse(r.Out.Contains("class Account:"));
        }

        [TestMethod]
        public void Refactor_RoundtripReportsExactPass()
        {
            string dir = MakeTempDir();
            WriteFile(dir, "account.py", GodClassSmall);
            RunResult r = Run("refactor", Path.Combine(dir, "account.py"), "--roundtrip=srp", "--quiet");
            Assert.AreEqual(0, r.ExitCode);
            StringAssert.Contains(r.Out, "round-trip exact-equality: PASS");
        }

        [TestMethod]
        public void Refactor_AutoGatesToHintedFamilyOnly()
        {
            string dir = MakeTempDir();
            WriteFile(dir, "god.py", GodClassLarge);
            string path = Path.Combine(dir, "god.py");

            // Ungated: several families match.
            RunResult ungated = Run("refactor", path, "--quiet");
            Assert.AreEqual(0, ungated.ExitCode);
            StringAssert.Contains(ungated.Out, "S2_DipIntroduce");

            // SRP001 fires (8 members > 7), so --auto keeps only the SRP split.
            RunResult gated = Run("refactor", path, "--auto", "--quiet");
            Assert.AreEqual(0, gated.ExitCode);
            StringAssert.Contains(gated.Out, "S1_SrpSplit");
            Assert.IsFalse(gated.Out.Contains("S2_DipIntroduce"));
        }

        [TestMethod]
        public void Refactor_AutoOffersNothingWhenNoRuleFires()
        {
            // 4 members: SRP001 (> 7) does not fire, so no family is hinted.
            string dir = MakeTempDir();
            WriteFile(dir, "account.py", GodClassSmall);
            RunResult r = Run("refactor", Path.Combine(dir, "account.py"), "--auto", "--quiet");
            Assert.AreEqual(0, r.ExitCode);
            Assert.AreEqual("", r.Out);
        }

        [TestMethod]
        public void Refactor_UnknownKind_Exit2()
        {
            string dir = MakeTempDir();
            WriteFile(dir, "account.py", GodClassSmall);
            RunResult r = Run("refactor", Path.Combine(dir, "account.py"), "--roundtrip=bogus", "--quiet");
            Assert.AreEqual(2, r.ExitCode);
            StringAssert.Contains(r.Err, "unknown kind: bogus");
        }

        [TestMethod]
        public void Refactor_NoCandidateOfKind_Exit1()
        {
            string dir = MakeTempDir();
            WriteFile(dir, "account.py", GodClassSmall);
            RunResult r = Run("refactor", Path.Combine(dir, "account.py"), "--apply", "--kind=unfactory", "--quiet");
            Assert.AreEqual(1, r.ExitCode);
            StringAssert.Contains(r.Err, "no candidate of kind unfactory");
        }

        [TestMethod]
        public void Refactor_MissingFile_Exit2()
        {
            string dir = MakeTempDir();
            RunResult r = Run("refactor", Path.Combine(dir, "nope.py"), "--quiet");
            Assert.AreEqual(2, r.ExitCode);
            StringAssert.Contains(r.Err, "must be a single .py file");
        }
    }
}
