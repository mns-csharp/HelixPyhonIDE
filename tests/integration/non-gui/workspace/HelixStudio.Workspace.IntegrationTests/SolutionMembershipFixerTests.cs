using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.VirtualCompiler.Compliance;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The resolution the IDE offers for rule PY8001 — "this file is not part of any project in the
    /// solution", a HARD blocker on Run and Debug that the Problems panel could previously only
    /// describe.
    ///
    /// Driven against a real COPY of the python-test-sample-oop fixture, whose two loose root
    /// scripts (debug_target.py, exception_target.py) are exactly the files that trigger the rule in
    /// the shipped IDE. The oracle is the virtual compiler itself: PY8001 before the fix, no PY8001
    /// after it — not merely "a file appeared in a folder".
    /// </summary>
    [TestClass]
    public class SolutionMembershipFixerTests
    {
        private string _root;   // a temp copy of the fixture solution

        // Members as the solution declares them in [tool.uv.workspace].members.
        private static readonly string[] MemberDirs =
        {
            @"lib_core", @"lib_utils", @"app_cli", @"app_server", @"app_gui",
            "my_unit_test_package",
        };

        // The loose script this suite fixes. Written into the COPY rather than relied on being
        // present: the fixture's own root scripts are exactly what a user reaches for the "Add to
        // Project" command with, so they move — and a test that assumes they are still at the root
        // breaks the moment the feature it covers gets used on the real fixture (it did).
        private const string StrayFileName = "stray_root_script.py";
        private string _stray;

        [TestInitialize]
        public void CopyFixture()
        {
            string source = FindFixture();
            if (source == null) Assert.Inconclusive("python-test-sample-oop fixture not found from the test binary");

            // Never deleted in cleanup: the suite convention here is to leave temp solutions on disk
            // rather than race anything still reading them.
            _root = Path.Combine(Path.GetTempPath(), "helix-py8001-" + Guid.NewGuid().ToString("N"));
            CopyTree(source, _root);

            _stray = Path.Combine(_root, StrayFileName);
            File.WriteAllText(_stray,
                "\"\"\"A script sitting in the solution root, in no project at all.\"\"\"\n"
                + "\n"
                + "\n"
                + "class Stray:\n"
                + "    \"\"\"Object-oriented, so only PY8001 can be reported against it.\"\"\"\n"
                + "\n"
                + "    def run(self) -> int:\n"
                + "        return 1\n");
        }

        // ---------------------------------------------------------------- the offered targets

        [TestMethod]
        public void Targets_OfferEveryMemberPackage_WithSolutionRelativePaths()
        {
            List<MembershipTarget> targets = SolutionMembershipFixer.TargetsFor(_root, Members());

            CollectionAssert.Contains(targets.Select(t => t.RelativePath).ToList(), "lib_utils",
                "every member project's own folder is a destination");
            CollectionAssert.Contains(targets.Select(t => t.RelativePath).ToList(),
                "my_unit_test_package/src/my_unit_test_package",
                "a src-layout member offers its inner package, not just the project root");

            MembershipTarget libUtils = targets.First(t => t.RelativePath == "lib_utils");
            Assert.IsTrue(libUtils.IsPackage, "lib_utils has an __init__.py");
            Assert.IsTrue(libUtils.IsProjectRoot);
            Assert.AreEqual("lib_utils", libUtils.ProjectName);
        }

        [TestMethod]
        public void Targets_PutImportablePackagesFirstWithinAProject()
        {
            List<MembershipTarget> targets = SolutionMembershipFixer.TargetsFor(_root, Members());
            List<MembershipTarget> ofTestPackage =
                targets.Where(t => t.ProjectName == "my_unit_test_package").ToList();

            Assert.IsTrue(ofTestPackage.Count >= 2, "the src-layout project offers more than one folder");
            Assert.IsTrue(ofTestPackage[0].IsPackage,
                "the importable package is the first suggestion, not the bare project root");
        }

        [TestMethod]
        public void Targets_SkipCachesAndVirtualEnvs()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"lib_utils\__pycache__"));
            File.WriteAllText(Path.Combine(_root, @"lib_utils\__pycache__\__init__.py"), "");

            List<MembershipTarget> targets = SolutionMembershipFixer.TargetsFor(_root, Members());

            Assert.IsFalse(targets.Any(t => t.RelativePath.Contains("__pycache__")),
                "a cache directory is never somewhere to put source");
        }

        // ---------------------------------------------------------------- the fix itself

        [TestMethod]
        public void MovingAStrayFileIntoAMember_ClearsPY8001()
        {
            Assert.IsTrue(PolicyCodesFor(_stray).Contains(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace),
                "precondition: the compiler reports the loose file as PY8001");

            MembershipTarget libUtils = SolutionMembershipFixer.TargetsFor(_root, Members())
                .First(t => t.RelativePath == "lib_utils");
            MembershipFixResult result = SolutionMembershipFixer.AddFileToProject(_stray, libUtils);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual(Path.Combine(_root, @"lib_utils\" + StrayFileName), result.NewPath);
            Assert.IsTrue(File.Exists(result.NewPath), "the file is in the project now");
            Assert.IsFalse(File.Exists(_stray), "…and no longer where it was — this is a move, not a copy");

            Assert.IsFalse(PolicyCodesFor(result.NewPath).Contains(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace),
                "the blocker the user was shown is actually gone afterwards");
            StringAssert.Contains(result.Message, "lib_utils");
        }

        [TestMethod]
        public void MovingPreservesTheFileContents()
        {
            string before = File.ReadAllText(_stray);

            MembershipTarget target = SolutionMembershipFixer.TargetsFor(_root, Members())
                .First(t => t.RelativePath == "lib_core");
            MembershipFixResult result = SolutionMembershipFixer.AddFileToProject(_stray, target);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual(before, File.ReadAllText(result.NewPath));
        }

        [TestMethod]
        public void ANameCollision_RefusesInsteadOfOverwriting()
        {
            string occupied = Path.Combine(_root, @"lib_utils\" + StrayFileName);
            File.WriteAllText(occupied, "# a DIFFERENT file that happens to share the name\n");

            MembershipTarget libUtils = SolutionMembershipFixer.TargetsFor(_root, Members())
                .First(t => t.RelativePath == "lib_utils");
            MembershipFixResult result = SolutionMembershipFixer.AddFileToProject(_stray, libUtils);

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains(result.Message, "already exists");
            Assert.IsTrue(File.Exists(_stray), "the source is untouched when the move is refused");
            StringAssert.Contains(File.ReadAllText(occupied), "DIFFERENT",
                "and the file that was already there is NOT overwritten");
        }

        [TestMethod]
        public void MovingAFileIntoTheFolderItIsAlreadyIn_IsRefusedWithAReason()
        {
            string inPlace = Path.Combine(_root, @"lib_utils\geometry.py");
            MembershipTarget libUtils = SolutionMembershipFixer.TargetsFor(_root, Members())
                .First(t => t.RelativePath == "lib_utils");

            MembershipFixResult result = SolutionMembershipFixer.AddFileToProject(inPlace, libUtils);

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains(result.Message, "already in that folder");
            Assert.IsTrue(File.Exists(inPlace));
        }

        [TestMethod]
        public void AMissingFileOrNoTarget_FailsWithoutThrowing()
        {
            MembershipTarget libUtils = SolutionMembershipFixer.TargetsFor(_root, Members())
                .First(t => t.RelativePath == "lib_utils");

            Assert.IsFalse(SolutionMembershipFixer.AddFileToProject(
                Path.Combine(_root, "no_such_file.py"), libUtils).Succeeded);
            Assert.IsFalse(SolutionMembershipFixer.AddFileToProject(_stray, null).Succeeded);
            Assert.IsFalse(SolutionMembershipFixer.AddFileToProject(null, libUtils).Succeeded);
        }

        // ---------------------------------------------------------------- helpers

        private List<PackageDescriptor> Members()
        {
            var list = new List<PackageDescriptor>();
            foreach (string rel in MemberDirs)
            {
                string dir = Path.Combine(_root, rel);
                if (Directory.Exists(dir)) list.Add(new PackageDescriptor(Path.GetFileName(rel), dir));
            }
            return list;
        }

        // The solution-policy codes the real virtual compiler reports for one file, with the same
        // policy object MainForm hands it.
        private List<string> PolicyCodesFor(string file)
        {
            var policy = new SolutionPolicy(_root, Members().Select(m => m.DirectoryPath));
            var options = new CompileOptions
            {
                EnableAstValidator = false,
                EnableSymtablePass = false,
                EnableCompileValidator = false,
                EnableTypeChecker = false,
                EnableImportResolution = false,
                EnableCompliancePass = true,
                Policy = policy,
            };
            var result = VirtualCompiler.VirtualCompiler.CompileSource(File.ReadAllText(file), file, options);
            return result.Diagnostics.Select(d => d.Code).ToList();
        }

        private static string FindFixture()
        {
            string dir = Path.GetDirectoryName(new Uri(
                typeof(SolutionMembershipFixerTests).Assembly.CodeBase).LocalPath);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, @"tests\fixtures\python\python-test-sample-oop");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                if (dir.IndexOf("__pycache__", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (dir.IndexOf(".pytest_cache", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Directory.CreateDirectory(dir.Replace(from, to));
            }
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                if (file.IndexOf("__pycache__", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (file.IndexOf(".pytest_cache", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                File.Copy(file, file.Replace(from, to), true);
            }
        }
    }
}
