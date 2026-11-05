using System;
using System.IO;
using System.Linq;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.VirtualCompiler.Compliance;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Rule 5 (PY8005) and the policy shape of single-file mode: a file opened OUTSIDE any
    /// solution may import installed packages and nothing else.
    ///
    /// <para>
    /// The rule exists because rule 1 cannot reach this context. PY8001 says executable source
    /// must be a workspace member so the IDE can show, compile-check and breakpoint everything it
    /// runs; a lone file has no workspace, so rule 1 has nothing to test and single-file mode would
    /// otherwise be a hole straight through the policy — put the modules beside the script, open
    /// the script alone, and the IDE runs a program it never parsed.
    /// </para>
    /// <para>
    /// These tests write REAL files to a temp directory rather than asserting on inline source,
    /// because limb 2 is a question about the filesystem: does a module of that name actually sit
    /// beside the script? An inline-only test would pass against a resolver that never looked.
    /// </para>
    /// </summary>
    [TestClass]
    public class SingleFilePolicyTests
    {
        private string _dir;

        [TestInitialize]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "helix-single-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private string Write(string relative, string text)
        {
            string p = Path.Combine(_dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            return p;
        }

        /// <summary>Compile one file under the single-file policy and keep only the PY8xxx rows.</summary>
        private CompileDiagnostic[] Check(string file, string source)
        {
            var options = new CompileOptions
            {
                EnableAstValidator = false, EnableSymtablePass = false,
                EnableCompileValidator = false, EnableTypeChecker = false,
                EnableImportResolution = false,
                Policy = SolutionPolicy.ForSingleFile(file),
            };
            return VirtualCompiler.CompileSource(source, file, options).Diagnostics
                .Where(d => d.Code.StartsWith("PY8", StringComparison.Ordinal)).ToArray();
        }

        private CompileDiagnostic[] CheckScript(string source)
        {
            string f = Write("scratch.py", source);
            return Check(f, source);
        }

        private static CompileDiagnostic[] OfCode(CompileDiagnostic[] all, string code)
        {
            return all.Where(d => d.Code == code).ToArray();
        }

        // ---- Installed packages are the whole point of the mode -----------------

        [TestMethod]
        public void InstalledPackages_AreAllowed()
        {
            var d = CheckScript(
                "import os\n"
                + "import numpy as np\n"
                + "from pathlib import Path\n"
                + "from collections import OrderedDict\n"
                + "class Job:\n"
                + "    @staticmethod\n"
                + "    def run():\n"
                + "        return np.zeros(3)\n");
            Assert.AreEqual(0, d.Length, Describe(d));
        }

        [TestMethod]
        public void AnImportOfSomethingThatIsNotThere_IsNotRule5sBusiness()
        {
            // Nothing of that name beside the script, so it is either an installed package the
            // known-ecosystem list has not heard of, or a genuine typo. Both are PY5002's story,
            // told in CPython's own words; saying "you wrote this module" would be a lie.
            var d = CheckScript("import zzz_not_installed_anywhere\n");
            Assert.AreEqual(0, OfCode(d, SingleFileImportRule.DiagnosticCode).Length, Describe(d));
        }

        // ---- Limb 2: a module sitting beside the script -------------------------

        [TestMethod]
        public void AModuleFileBesideTheScript_IsBlocked()
        {
            Write("helpers.py", "class Helper:\n    pass\n");
            var d = CheckScript("import helpers\n");

            var rule5 = OfCode(d, SingleFileImportRule.DiagnosticCode);
            Assert.AreEqual(1, rule5.Length, Describe(d));
            Assert.AreEqual(HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error, rule5[0].Severity);
            StringAssert.Contains(rule5[0].Message, "helpers.py");
            StringAssert.Contains(rule5[0].Message, "create a solution");
        }

        [TestMethod]
        public void APackageDirectoryBesideTheScript_IsBlocked()
        {
            Write(Path.Combine("mypkg", "__init__.py"), "");
            var d = CheckScript("from mypkg import thing\n");
            Assert.AreEqual(1, OfCode(d, SingleFileImportRule.DiagnosticCode).Length, Describe(d));
        }

        [TestMethod]
        public void OnlyTheTopLevelNameIsNamed_NotTheWholeDottedPath()
        {
            // 'mypkg.sub' is not something the user can act on independently -- 'mypkg' is the
            // folder beside them, and moving it is the whole fix.
            Write(Path.Combine("mypkg", "__init__.py"), "");
            Write(Path.Combine("mypkg", "sub.py"), "class S:\n    pass\n");
            var d = OfCode(CheckScript("import mypkg.sub\n"), SingleFileImportRule.DiagnosticCode);

            Assert.AreEqual(1, d.Length);
            StringAssert.Contains(d[0].Message, "'mypkg'");
            Assert.IsFalse(d[0].Message.Contains("mypkg.sub"), "should name the top-level package only");
        }

        [TestMethod]
        public void AFolderWithNoInitPy_IsStillTheUsersCode()
        {
            // PEP 420 namespace package: importable, has no __init__.py, and is exactly as invisible
            // to the IDE as a regular one.
            Directory.CreateDirectory(Path.Combine(_dir, "nspkg"));
            Write(Path.Combine("nspkg", "leaf.py"), "class L:\n    pass\n");
            var d = OfCode(CheckScript("import nspkg\n"), SingleFileImportRule.DiagnosticCode);
            Assert.AreEqual(1, d.Length, "a namespace package beside the script is still user code");
        }

        [TestMethod]
        public void AnImportInsideAFunction_IsBlockedToo()
        {
            // The import checker walks only the top level, correctly -- it models import time.
            // This rule is about what the program may REACH, and a deferred import reaches it one
            // call later.
            Write("helpers.py", "class Helper:\n    pass\n");
            var d = CheckScript(
                "class Loader:\n"
                + "    @staticmethod\n"
                + "    def load():\n"
                + "        import helpers\n"
                + "        return helpers\n");
            Assert.AreEqual(1, OfCode(d, SingleFileImportRule.DiagnosticCode).Length, Describe(d));
        }

        [TestMethod]
        public void AnImportInsideTryExcept_IsBlockedToo()
        {
            // A try/except import is the optional-dependency shape, which is meaningful for an
            // installed package and meaningless for the file next door.
            Write("helpers.py", "class Helper:\n    pass\n");
            var d = CheckScript(
                "try:\n"
                + "    import helpers\n"
                + "except ImportError:\n"
                + "    helpers = None\n");
            Assert.AreEqual(1, OfCode(d, SingleFileImportRule.DiagnosticCode).Length, Describe(d));
        }

        // ---- Limb 1: relative imports -------------------------------------------

        [TestMethod]
        public void ARelativeImport_IsBlocked_EvenWithNothingBesideTheScript()
        {
            var d = OfCode(CheckScript("from . import helpers\n"), SingleFileImportRule.DiagnosticCode);
            Assert.AreEqual(1, d.Length);
            StringAssert.Contains(d[0].Message, "not in a package");
        }

        [TestMethod]
        public void ADottedRelativeImport_IsBlocked()
        {
            var d = OfCode(CheckScript("from ..pkg.mod import Thing\n"), SingleFileImportRule.DiagnosticCode);
            Assert.AreEqual(1, d.Length);
            StringAssert.Contains(d[0].Message, "..pkg.mod");
        }

        // ---- Limb 3: sys.path ----------------------------------------------------

        [TestMethod]
        public void MutatingSysPath_IsBlocked()
        {
            foreach (string line in new[]
                     {
                         "sys.path.insert(0, '/somewhere')",
                         "sys.path.append('/somewhere')",
                         "sys.path.extend(['/a', '/b'])",
                         "sys.path = ['/somewhere']",
                         "sys.path[0] = '/somewhere'",
                         "sys.path += ['/somewhere']",
                     })
            {
                var d = OfCode(CheckScript("import sys\n" + line + "\n"),
                    SingleFileImportRule.DiagnosticCode);
                Assert.AreEqual(1, d.Length, "should be blocked: " + line);
            }
        }

        [TestMethod]
        public void SomebodyElsesPathVariable_IsNotSysPath()
        {
            // 'path' is one of the most common local names in Python. Matching a bare `path` would
            // make this rule a hard blocker on correct lines, which is a worse error than the
            // evasion it would close.
            var d = CheckScript(
                "class Router:\n"
                + "    @staticmethod\n"
                + "    def build():\n"
                + "        path = []\n"
                + "        path.insert(0, 'home')\n"
                + "        return path\n");
            Assert.AreEqual(0, d.Length, Describe(d));
        }

        // ---- Rules 2 and 3 still apply outside a solution ------------------------

        [TestMethod]
        public void ModuleLevelFunction_IsStillNotObjectOriented()
        {
            var d = OfCode(CheckScript("def crunch(rows):\n    return sum(rows)\n"),
                VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented);
            Assert.AreEqual(1, d.Length, "rule 2 does not stop applying because no solution is open");
        }

        [TestMethod]
        public void DefMain_IsStillTheCarveOut()
        {
            var d = CheckScript(
                "def main():\n"
                + "    print('hi')\n"
                + "\n"
                + "if __name__ == '__main__':\n"
                + "    main()\n");
            Assert.AreEqual(0, d.Length, Describe(d));
        }

        [TestMethod]
        public void ATestFileOpenedAlone_IsBlockedWithNoProjectToName()
        {
            string f = Write("test_thing.py", "class TestThing:\n    def test_it(self):\n        pass\n");
            var d = OfCode(Check(f, File.ReadAllText(f)),
                VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject);

            Assert.AreEqual(1, d.Length, "rule 3 must not be silently skipped just because no member owns the file");
            StringAssert.Contains(d[0].Message, "in no project at all");
            // The old message names an owning project and its kind; there is neither here, so it
            // must not be reused verbatim or the user is told to look for something that does not exist.
            Assert.IsFalse(d[0].Message.Contains("which is a"), "must not reuse the owning-project wording");
        }

        // ---- Rule 1 and rule 5 are mutually exclusive ----------------------------

        [TestMethod]
        public void SingleFileMode_NeverReportsRule1()
        {
            // Both rules say "the IDE must be able to see what it runs". Reporting both would name
            // two different fixes for one fault -- join a workspace that does not exist, and create
            // a solution.
            Write("helpers.py", "class Helper:\n    pass\n");
            var d = CheckScript("import helpers\n");
            Assert.AreEqual(0, OfCode(d, VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace).Length,
                "PY8001 has no meaning without a workspace: " + Describe(d));
        }

        [TestMethod]
        public void ThirdPartySource_IsExemptEvenOpenedAlone()
        {
            // Opening a file from inside site-packages to read it must not turn the IDE into a
            // critic of somebody else's package.
            string f = Write(Path.Combine("site-packages", "requests", "sessions.py"),
                "def request(method, url):\n    return None\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f)).Length);
        }

        // ---- Policy shape --------------------------------------------------------

        [TestMethod]
        public void ForSingleFile_TurnsRule1OffAndRule5On()
        {
            SolutionPolicy p = SolutionPolicy.ForSingleFile(Path.Combine(_dir, "scratch.py"));
            Assert.IsFalse(p.EnforceWorkspaceMembership);
            Assert.IsTrue(p.EnforceSingleFileImports);
            Assert.IsTrue(p.EnforceObjectOrientation);
            Assert.IsTrue(p.EnforceTestFilePlacement);
            Assert.AreEqual(0, p.MemberDirectories.Count);
        }

        [TestMethod]
        public void AnUnsavedBuffer_StillGetsTheRulesItCanBeGiven()
        {
            // No path means no directory to resolve limb 2 against. Limbs 1 and 3 need no path, and
            // rule 2 needs none either -- a brand-new untitled file is still held to them.
            var options = new CompileOptions
            {
                EnableAstValidator = false, EnableSymtablePass = false,
                EnableCompileValidator = false, EnableTypeChecker = false,
                EnableImportResolution = false,
                Policy = SolutionPolicy.ForSingleFile(null),
            };
            CompileDiagnostic[] d = VirtualCompiler
                .CompileSource("from . import x\ndef helper():\n    pass\n", string.Empty, options)
                .Diagnostics.Where(x => x.Code.StartsWith("PY8", StringComparison.Ordinal)).ToArray();

            Assert.AreEqual(1, OfCode(d, SingleFileImportRule.DiagnosticCode).Length, Describe(d));
            Assert.AreEqual(1, OfCode(d, VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented).Length, Describe(d));
        }

        // ---- Unsuppressible, like every other PY8xxx -----------------------------

        [TestMethod]
        public void NoqaCannotSilenceRule5()
        {
            Write("helpers.py", "class Helper:\n    pass\n");
            var d = OfCode(CheckScript("import helpers  # noqa\n"), SingleFileImportRule.DiagnosticCode);
            Assert.AreEqual(1, d.Length, "a hard blocker must not be defeated by a comment");
        }

        private static string Describe(CompileDiagnostic[] diagnostics)
        {
            if (diagnostics.Length == 0) return "(no diagnostics)";
            return string.Join(" | ", diagnostics.Select(d => d.Code + ": " + d.Message).ToArray());
        }
    }
}
