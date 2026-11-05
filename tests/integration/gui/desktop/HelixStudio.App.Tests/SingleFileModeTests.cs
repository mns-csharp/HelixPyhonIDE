using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Single-file mode in the shell: a lone .py is routed to it, it is exclusive with a solution,
    /// and the commands that act on a file are reachable while it is on.
    ///
    /// <para>
    /// These are the claims that live in the HOST rather than in the rule. PY8005 itself is pinned
    /// by SingleFilePolicyTests over in the virtual compiler, where it can be asserted against real
    /// files with no window in the way; what cannot be asserted there is that the IDE ever asks it.
    /// </para>
    ///
    /// <para>
    /// Nothing here shows a window. <c>new MainForm()</c> composes nothing and would make every
    /// assertion vacuous, so the routing and gating decisions are asserted on the static predicates
    /// and on the coordinator's own state — the parts that are decisions rather than paint.
    /// </para>
    /// </summary>
    [TestClass]
    public class SingleFileModeTests
    {
        private string _dir;

        [TestInitialize]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "helix-sfmode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void TearDown()
        {
            // Left behind deliberately when it cannot be removed: a temp folder costs nothing, and
            // deleting one a stale async solution-load may still read from is how this suite has
            // put a real MessageBox on a developer's desktop before.
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private static bool IsLoose(string path)
        {
            MethodInfo m = typeof(MainForm).GetMethod("IsLooseSourceFile",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.IsLooseSourceFile should still exist");
            return (bool)m.Invoke(null, new object[] { path });
        }

        // ---- What single-file mode claims -------------------------------------

        [TestMethod]
        public void APythonFile_IsRoutedToSingleFileMode()
        {
            Assert.IsTrue(IsLoose(Path.Combine(_dir, "scratch.py")));
            Assert.IsTrue(IsLoose(Path.Combine(_dir, "scratch.pyw")));
            Assert.IsTrue(IsLoose(@"C:\somewhere\UPPER.PY"), "extension matching is case-insensitive");
        }

        [TestMethod]
        public void ASolutionManifest_IsNotALooseFile()
        {
            // The dispatcher behind File ▸ Open, the Recent list and the Welcome page is shared, so
            // a manifest that started matching here would stop opening as a solution.
            Assert.IsFalse(IsLoose(Path.Combine(_dir, "pyproject.toml")));
            Assert.IsFalse(IsLoose(Path.Combine(_dir, "notebook.ipynb")));
            Assert.IsFalse(IsLoose(Path.Combine(_dir, "1brt.pdb")));
            Assert.IsFalse(IsLoose(null));
            Assert.IsFalse(IsLoose(string.Empty));
        }

        [TestMethod]
        public void AMalformedPath_IsRefusedRatherThanThrown()
        {
            // This predicate sits on the path a double-clicked file takes into the process, so a
            // name the OS allows and Path.GetExtension does not must not take the open down.
            Assert.IsFalse(IsLoose("bad|name?.py"));
        }

        /// <summary>
        /// The rule set a loose file is judged by. Asserted here as well as in the compiler's own
        /// suite because this is the pairing the host depends on: rule 1 off, rule 5 on, rules 2
        /// and 3 untouched. A future change that turned rule 1 back on would report a workspace
        /// violation against a file that can never be in a workspace.
        /// </summary>
        [TestMethod]
        public void TheSingleFilePolicy_ReplacesRule1WithRule5()
        {
            var policy = HelixPythonNamespace.VirtualCompiler.Compliance.SolutionPolicy
                .ForSingleFile(Path.Combine(_dir, "scratch.py"));

            Assert.IsFalse(policy.EnforceWorkspaceMembership, "PY8001 cannot apply without a workspace");
            Assert.IsTrue(policy.EnforceSingleFileImports, "PY8005 takes its place");
            Assert.IsTrue(policy.EnforceObjectOrientation, "PY8002 does not relax outside a solution");
            Assert.IsTrue(policy.EnforceTestFilePlacement, "PY8003 does not relax outside a solution");
        }

        /// <summary>
        /// The starter file File ▸ New ▸ Python File writes must itself satisfy the rules it is
        /// about to be judged by. Handing the user a procedural hello-world that the IDE then
        /// refuses to run is the worst possible introduction to PY8002.
        /// </summary>
        [TestMethod]
        public void TheStarterFile_PassesTheRulesItWillBeJudgedBy()
        {
            MethodInfo starter = typeof(HelixPythonNamespace.App.Coordinators.SingleFileCoordinator)
                .GetMethod("StarterSource", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(starter, "SingleFileCoordinator.StarterSource should still exist");
            string source = (string)starter.Invoke(null, new object[] { "scratch" });

            string path = Path.Combine(_dir, "scratch.py");
            File.WriteAllText(path, source);

            var options = new HelixPythonNamespace.VirtualCompiler.CompileOptions
            {
                EnableImportResolution = false,
                Policy = HelixPythonNamespace.VirtualCompiler.Compliance.SolutionPolicy.ForSingleFile(path),
            };
            var result = HelixPythonNamespace.VirtualCompiler.VirtualCompiler
                .CompileSource(source, path, options);

            foreach (var d in result.Diagnostics)
                Assert.IsFalse(d.Code.StartsWith("PY8", StringComparison.Ordinal),
                    "the starter file must not be born blocked: " + d.Code + " " + d.Message);
        }

        [TestMethod]
        public void TheStarterFile_NamesItsClassAfterTheFile()
        {
            MethodInfo starter = typeof(HelixPythonNamespace.App.Coordinators.SingleFileCoordinator)
                .GetMethod("StarterSource", BindingFlags.Static | BindingFlags.NonPublic);
            StringAssert.Contains((string)starter.Invoke(null, new object[] { "my_data_tool" }),
                "class MyDataTool:");
            // A stem with nothing usable in it must still produce a file that parses.
            StringAssert.Contains((string)starter.Invoke(null, new object[] { "123" }), "class Script:");
            StringAssert.Contains((string)starter.Invoke(null, new object[] { string.Empty }), "class Script:");
        }
    }
}
