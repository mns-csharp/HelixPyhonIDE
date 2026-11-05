using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Iter 393 (PSM Slice 4): per-package compliance validation (docs/python-solution-model.md).
    /// Each package is validated independently, so a solution with several executable packages
    /// (each with its own main.py + def main()) is compliant (PSM-07); the errors are PSM-03/04/05/
    /// 06/12/13 scoped to one package.
    /// </summary>
    [TestClass]
    public class PackageComplianceValidatorTests
    {
        private string _root;
        private SolutionModel _sln;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_psm_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sln = new SolutionModel(Path.Combine(_root, "pyproject.toml"), "ws");
        }

        private ProjectModel AddPackage(string name, ProjectKind kind)
        {
            string dir = Path.Combine(_root, "src", name);
            Directory.CreateDirectory(dir);
            var p = new ProjectModel(Path.Combine(dir, "pyproject.toml"), name, _sln) { Kind = kind };
            File.WriteAllText(p.FilePath, "[project]\nname = \"" + name + "\"\n");
            _sln.Projects.Add(p);
            return p;
        }

        private void File_(ProjectModel p, string rel, string body)
        {
            string path = Path.Combine(p.ProjectDirectory, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, body);
        }

        private const string MainPy =
            "def main() -> None:\n" +
            "    pass\n\n" +
            "if __name__ == \"__main__\":\n" +
            "    raise SystemExit(main())\n";

        private static bool Has(System.Collections.Generic.IReadOnlyList<PackageComplianceFinding> f, string pkg, PackageRule r)
        {
            return f.Any(x => x.Package == pkg && x.Rule == r);
        }

        [TestMethod]
        public void TwoValidExecutables_AreCompliant()
        {
            var a = AddPackage("app_a", ProjectKind.App); File_(a, "main.py", MainPy);
            var b = AddPackage("app_b", ProjectKind.App); File_(b, "main.py", MainPy);
            var findings = PackageComplianceValidator.Validate(_sln);
            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }

        [TestMethod]
        public void ExecutableWithoutMain_FlagsPsm03()
        {
            var a = AddPackage("app_a", ProjectKind.App); File_(a, "run.py", "x = 1\n");
            var f = PackageComplianceValidator.Validate(_sln);
            Assert.IsTrue(Has(f, "app_a", PackageRule.ExecutableMissingMain));
        }

        [TestMethod]
        public void LibraryWithMain_FlagsPsm04()
        {
            var lib = AddPackage("lib", ProjectKind.ClassLibrary); File_(lib, "main.py", MainPy);
            var f = PackageComplianceValidator.Validate(_sln);
            Assert.IsTrue(Has(f, "lib", PackageRule.LibraryHasMain));
        }

        [TestMethod]
        public void MainWithoutDefMain_FlagsPsm05()
        {
            var a = AddPackage("app_a", ProjectKind.App); File_(a, "main.py", "print('hi')\n");
            var f = PackageComplianceValidator.Validate(_sln);
            Assert.IsTrue(Has(f, "app_a", PackageRule.MainMissingMainFunc));
        }

        [TestMethod]
        public void DefMainOutsideMain_FlagsPsm06()
        {
            var a = AddPackage("app_a", ProjectKind.App);
            File_(a, "main.py", MainPy);
            File_(a, "helper.py", MainPy);   // def main() in a non-main file
            var f = PackageComplianceValidator.Validate(_sln);
            Assert.IsTrue(Has(f, "app_a", PackageRule.MainFuncOutsideMain));
            Assert.IsTrue(Has(f, "app_a", PackageRule.MultipleMainFuncs)); // PSM-13 too
        }

        [TestMethod]
        public void MultipleMainModules_FlagsPsm12()
        {
            var a = AddPackage("app_a", ProjectKind.App);
            File_(a, "main.py", MainPy);
            File_(a, "__main__.py", MainPy);   // main-equivalent duplicate
            var f = PackageComplianceValidator.Validate(_sln);
            Assert.IsTrue(Has(f, "app_a", PackageRule.MultipleMainModules));
        }

        [TestMethod]
        public void OnePackagesViolation_DoesNotTaintTheOther()
        {
            var good = AddPackage("good", ProjectKind.App); File_(good, "main.py", MainPy);
            var bad = AddPackage("bad", ProjectKind.App); File_(bad, "run.py", "x = 1\n");
            var f = PackageComplianceValidator.Validate(_sln);
            Assert.IsFalse(f.Any(x => x.Package == "good"), "compliant package must have no findings");
            Assert.IsTrue(Has(f, "bad", PackageRule.ExecutableMissingMain));
        }

        [DataTestMethod]
        [DataRow("__main__.py")]
        [DataRow("console_main.py")]
        public void EquivalentEntryModuleNames_AreCompliant(string fileName)
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, fileName, MainPy);

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count,
                string.Join("; ", findings.Select(x => x.ToString())));
        }

        [TestMethod]
        public void EntryModuleBelowProjectRoot_FlagsPsm07()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, Path.Combine("src", "main.py"), MainPy);

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsTrue(Has(findings, "app", PackageRule.EntryModuleOutsideProjectRoot));
        }

        [TestMethod]
        public void AsyncParameterizedMainWithoutCanonicalGuard_FlagsPsm11Psm12Psm16()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py", "async def main(argv):\n    pass\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsTrue(Has(findings, "app", PackageRule.AsyncMain));
            Assert.IsTrue(Has(findings, "app", PackageRule.MainHasParameters));
            Assert.IsTrue(Has(findings, "app", PackageRule.MissingCanonicalGuard));
        }

        [TestMethod]
        public void LibraryMainAndGuard_FlagsPsm22Psm23Psm24()
        {
            var lib = AddPackage("lib", ProjectKind.ClassLibrary);
            File_(lib, "__main__.py", MainPy);

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsTrue(Has(findings, "lib", PackageRule.LibraryHasMain));
            Assert.IsTrue(Has(findings, "lib", PackageRule.LibraryHasMainFunction));
            Assert.IsTrue(Has(findings, "lib", PackageRule.LibraryHasInvocationGuard));
        }

        [TestMethod]
        public void LibraryMainAndGuard_ReportTheirActualSourceLines()
        {
            var lib = AddPackage("lib", ProjectKind.ClassLibrary);
            File_(lib, "helper.py",
                "# module header\n\n" +
                "def main() -> None:\n" +
                "    pass\n\n" +
                "if __name__ == \"__main__\":\n" +
                "    raise SystemExit(main())\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(3, findings.Single(
                x => x.Rule == PackageRule.MainFuncOutsideMain).Line);
            Assert.AreEqual(3, findings.Single(
                x => x.Rule == PackageRule.LibraryHasMainFunction).Line);
            Assert.AreEqual(6, findings.Single(
                x => x.Rule == PackageRule.LibraryHasInvocationGuard).Line);
        }

        [TestMethod]
        public void MainWithDefaultedParameter_IsCompliant()
        {
            // def main(argv=None) is a completely standard, testable idiom — since the
            // default lets main() still be invoked with zero arguments, it doesn't
            // violate "the entry point must not declare parameters".
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "def main(argv=None) -> int:\n    return 0\n\n" +
                "if __name__ == \"__main__\":\n    raise SystemExit(main())\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsFalse(Has(findings, "app", PackageRule.MainHasParameters));
        }

        [TestMethod]
        public void MainWithOnlyStarArgsOrKwargs_IsCompliant()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "def main(*args, **kwargs) -> int:\n    return 0\n\n" +
                "if __name__ == \"__main__\":\n    raise SystemExit(main())\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsFalse(Has(findings, "app", PackageRule.MainHasParameters));
        }

        [TestMethod]
        public void MainWithARequiredParameter_StillFlagsPsm12()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "def main(argv) -> int:\n    return 0\n\n" +
                "if __name__ == \"__main__\":\n    raise SystemExit(main())\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsTrue(Has(findings, "app", PackageRule.MainHasParameters));
        }

        [TestMethod]
        public void SysExitGuard_IsCanonical_LikeRaiseSystemExit()
        {
            // sys.exit(x) and raise SystemExit(x) are the same operation — sys.exit is
            // documented as being implemented via SystemExit — so this must not flag
            // PSM-16 just because it spells the exit differently.
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "import sys\n\ndef main() -> int:\n    return 0\n\n" +
                "if __name__ == \"__main__\":\n    sys.exit(main())\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsFalse(Has(findings, "app", PackageRule.MissingCanonicalGuard));
        }

        [TestMethod]
        public void BareMainCallGuard_IsCanonical_TheSingleMostCommonRealWorldForm()
        {
            // if __name__ == "__main__":\n    main()  — no exit-code wrapping at all —
            // is the single most common entry-point idiom in real-world Python. It must
            // not be flagged just because it isn't defensively propagating a return code.
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "def main() -> None:\n    pass\n\n" +
                "if __name__ == \"__main__\":\n    main()\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsFalse(Has(findings, "app", PackageRule.MissingCanonicalGuard));
        }

        [TestMethod]
        public void LibraryWithTestFileInvocationGuard_IsCompliant()
        {
            // A tests\test_*.py file with its own `if __name__ == "__main__":
            // unittest.main()` runner is a completely normal, legitimate Python
            // convention — it must not make an otherwise-clean LIBRARY project
            // non-compliant (this is exactly what PackageSplitter produces when it
            // sweeps a package's tests\ folder into the shared library package).
            var lib = AddPackage("lib", ProjectKind.ClassLibrary);
            File_(lib, "helper.py", "def helper():\n    return 1\n");
            File_(lib, Path.Combine("tests", "test_helper.py"),
                "import unittest\n\n" +
                "class T(unittest.TestCase):\n" +
                "    def test_it(self):\n" +
                "        pass\n\n" +
                "if __name__ == \"__main__\":\n" +
                "    unittest.main()\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }

        [TestMethod]
        public void ExecutableWithTestFileDefMain_DoesNotCountAsASecondEntryPoint()
        {
            // Same exemption for an executable project: a test file that happens to
            // ALSO declare its own def main() (a standalone test runner) must not be
            // confused for a second application entry point.
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py", MainPy);
            File_(app, Path.Combine("tests", "test_something.py"),
                "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }

        // ------------------------------------------------------------------ SRS §5.9
        // Exclude/Include must exempt a file from compliance too (loop 532) — not just
        // from the Explorer tree/Types-Functions population (loop 530). Otherwise
        // excluding an offending debug script does nothing to unblock Run.

        [TestMethod]
        public void ExcludedFile_IsNotValidated()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py", MainPy);
            File_(app, "debug_tool.py", MainPy);   // would normally flag PSM-14/15
            app.ExcludedPaths.Add("debug_tool.py");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }

        [TestMethod]
        public void ExcludedDirectory_ExemptsEverythingBeneathIt()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py", MainPy);
            File_(app, Path.Combine("scratch", "debug_tool.py"), MainPy);
            app.ExcludedPaths.Add("scratch");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }

        [TestMethod]
        public void NonExcludedSibling_IsStillValidated()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py", MainPy);
            File_(app, "excluded_one.py", MainPy);
            File_(app, "still_checked.py", MainPy);
            app.ExcludedPaths.Add("excluded_one.py");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsTrue(Has(findings, "app", PackageRule.MultipleMainFuncs),
                "the non-excluded duplicate main() must still be flagged");
            Assert.IsTrue(findings.Any(f => f.Paths.Any(p => p.Contains("still_checked.py"))));
            Assert.IsFalse(findings.Any(f => f.Paths.Any(p => p.Contains("excluded_one.py"))));
        }

        // ---------------------------------------------------------- PSM-16 on the parse tree
        // These pin the shapes the regex-based validator got wrong. It required a newline
        // immediately after the guard's colon, so valid entry modules with an inline comment
        // failed PSM-16 and blocked Run and Debug.

        [TestMethod]
        public void GuardWithATrailingComment_IsCanonical()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "def main() -> None:   # the process entry point\n" +
                "    pass\n\n" +
                "if __name__ == \"__main__\":   # if this module is run directly...\n" +
                "    main()   # ...start the runner\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }

        [TestMethod]
        public void GuardWithATrailingComment_IsCanonical_ForEveryExitSpelling()
        {
            // The same comment, against each of the three accepted guard bodies.
            string[] bodies =
            {
                "    main()   # ...start the runner\n",
                "    sys.exit(main())   # ...run it and use its result as the exit code\n",
                "    raise SystemExit(main())   # ...and propagate the exit code\n",
            };

            foreach (string body in bodies)
            {
                Init();   // fresh temp root + solution per body
                var app = AddPackage("app", ProjectKind.App);
                File_(app, "main.py",
                    "import sys\n\ndef main() -> int:\n    return 0\n\n" +
                    "if __name__ == \"__main__\":   # if this file is run directly...\n" + body);

                var findings = PackageComplianceValidator.Validate(_sln);

                Assert.IsFalse(Has(findings, "app", PackageRule.MissingCanonicalGuard),
                    "guard body must be canonical: " + body.Trim());
            }
        }

        [TestMethod]
        public void GuardBodyDoingSetupBeforeTheCall_IsCanonical()
        {
            // The regex demanded the call be the FIRST line of the body. A body that switches the
            // console encoding, or logs, before invoking main() still invokes main() through the
            // guard — which is the whole of what PSM-16 asks.
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "import sys\n\ndef main() -> int:\n    return 0\n\n" +
                "if __name__ == \"__main__\":\n" +
                "    # give the console a stable encoding first\n" +
                "    sys.stdout.reconfigure(encoding=\"utf-8\")\n" +
                "    sys.exit(main())\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.IsFalse(Has(findings, "app", PackageRule.MissingCanonicalGuard));
        }

        [TestMethod]
        public void GuardWithSingleQuotesOrReversedOperands_IsStillTheGuard()
        {
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "def main() -> None:\n    pass\n\n" +
                "if '__main__' == __name__:\n    main()\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }

        [TestMethod]
        public void AGuardThatNeverCallsMain_StillFlagsPsm16_AtTheGuardLine()
        {
            // The rule still has teeth: a guard that runs something else is not an invocation.
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "def main() -> None:\n" +
                "    pass\n\n" +
                "if __name__ == \"__main__\":\n" +
                "    print(\"nothing to do\")\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            var psm16 = findings.Single(x => x.Rule == PackageRule.MissingCanonicalGuard);
            Assert.AreEqual(4, psm16.Line, "the finding must point at the guard, not at line 1");
        }

        [TestMethod]
        public void AMainMethodInsideAClass_IsNotTheModuleEntryPoint()
        {
            // Scope comes from the tree, not from a column-0 anchor: `main` as a method of a
            // class is not a module-level def, however it is indented or line-wrapped.
            var app = AddPackage("app", ProjectKind.App);
            File_(app, "main.py",
                "class App:\n" +
                "    def main(\n" +
                "        self,\n" +
                "    ) -> None:\n" +
                "        pass\n\n" +
                "def main() -> None:\n" +
                "    App().main()\n\n" +
                "if __name__ == \"__main__\":\n    main()\n");

            var findings = PackageComplianceValidator.Validate(_sln);

            Assert.AreEqual(0, findings.Count, string.Join("; ", findings.Select(x => x.ToString())));
        }



        private static string FixtureEntryModule()
        {
            const string relative = @"tests\fixtures\python\bugmap_oop\src\rouse_model_verification\__main__.py";
            var dir = new System.IO.DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
                dir = dir.Parent;
            if (dir == null) Assert.Inconclusive("Optional workspace test data is unavailable.");
            return Path.Combine(dir.FullName, relative);
        }
    }
}
