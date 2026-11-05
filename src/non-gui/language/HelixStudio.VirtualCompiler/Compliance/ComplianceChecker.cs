using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Compliance
{
    /// <summary>
    /// Enforces the solution-wide rules the IDE treats as hard blockers:
    ///
    /// <list type="number">
    /// <item><b>Workspace membership</b> — every user-written source file must belong to a member
    /// project. Source outside the workspace can still be executed through a runtime
    /// <c>sys.path</c> insertion, which means the IDE would run code it cannot show, compile-check,
    /// or breakpoint.</item>
    /// <item><b>Object orientation</b> — user-written code must live in classes. Module-level
    /// functions are invisible to the structural views the IDE is built around.</item>
    /// <item><b>Test-file placement</b> — only a test project may contain test files. A class
    /// library, an executable app or an executable GUI app that ships its own tests cannot be
    /// built, packaged or referenced apart from its test harness.</item>
    /// <item><b>Single-file imports</b> — a file opened OUTSIDE any solution may import installed
    /// packages only (<see cref="SingleFileImportRule"/>). This is rule 1 asked of the one context
    /// rule 1 cannot reach, so the two are mutually exclusive: <see cref="SolutionPolicy.ForSingleFile"/>
    /// turns rule 1 off and this one on.</item>
    /// </list>
    ///
    /// All are reported as errors so they block Run, and all exempt third-party code.
    ///
    /// Rule 2 has two carve-outs, neither a style choice.
    ///
    /// <c>def main()</c>: PSM-06…24 define an executable package as having exactly one top-level
    /// <c>def main()</c> plus the <c>if __name__ == "__main__":</c> guard, and Python has no way to
    /// express a process entry point inside a class. Without it the rule would block every
    /// executable package in every Python solution, including the ones the IDE itself scaffolds.
    ///
    /// <b>Test-protocol functions inside a test project</b>
    /// (<see cref="HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule"/>): pytest and unittest collect
    /// tests by module-level name, and a <c>@pytest.fixture</c> moved into a class is silently never
    /// applied. Without it rules 2 and 3 contradict each other — rule 3 requires the user's tests to
    /// live in a <c>kind = "test"</c> project, and rule 2 would then forbid the only shape the
    /// runners can see, leaving the IDE-wide "Allow non-object-oriented source" switch as the only
    /// way to write an ordinary test. An ordinary helper in a test file is NOT exempt; the carve-out
    /// is exactly as wide as the runner protocol.
    /// </summary>
    public sealed class ComplianceChecker
    {
        private readonly string _filePath;
        private readonly SolutionPolicy _policy;
        private readonly List<CompileDiagnostic> _diagnostics = new List<CompileDiagnostic>();

        public ComplianceChecker(string filePath, SolutionPolicy policy)
        {
            _filePath = filePath ?? string.Empty;
            _policy = policy;
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _diagnostics; } }

        public void Check(Module module)
        {
            if (_policy == null || module == null) return;

            // An untitled buffer in single-file mode has no path yet, so IsUserWritten -- which
            // answers "is this inside the solution and not vendored" -- has nothing to answer with
            // and says no. It is the user's code by construction: they are typing it. Without this
            // the rules would switch themselves off for exactly as long as the file is new, and
            // switch on at the first save, which is the worst moment to learn about them.
            bool unsavedSingleFile = _policy.EnforceSingleFileImports && string.IsNullOrEmpty(_filePath);
            if (!unsavedSingleFile && !_policy.IsUserWritten(_filePath)) return;   // third-party / outside the solution

            // The owning member decides both rule 3 (which project may hold tests) and rule 2's
            // test-protocol carve-out (which project may hold module-level test functions), so it
            // is resolved once. Null means no member owns the file — rule 1's business.
            string owner = _policy.FindOwningProjectDirectory(_filePath);
            bool inTestProject = !string.IsNullOrEmpty(owner) && _policy.IsTestProjectDirectory(owner);

            if (_policy.EnforceWorkspaceMembership) CheckMembership();
            if (_policy.EnforceObjectOrientation) CheckObjectOrientation(module, inTestProject);
            if (_policy.EnforceTestFilePlacement) CheckTestFilePlacement(module, owner, inTestProject);
            if (_policy.EnforceSingleFileImports) CheckSingleFileImports(module);
        }

        // ---- Rule 5 -----------------------------------------------------------

        /// <summary>
        /// A file opened outside any solution may import installed packages only. Rule 1 asks the
        /// same question of a file inside one; a lone file has no workspace to be a member of, so
        /// <see cref="SolutionPolicy.ForSingleFile"/> turns rule 1 off and this on, and the two are
        /// never both reported.
        /// </summary>
        private void CheckSingleFileImports(Module module)
        {
            foreach (CompileDiagnostic d in SingleFileImportRule.FindViolations(module, _filePath))
                _diagnostics.Add(d);
        }

        // ---- Rule 1 -----------------------------------------------------------

        private void CheckMembership()
        {
            if (_policy.IsWorkspaceMember(_filePath)) return;
            Report(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace,
                default(HelixPythonNamespace.Python.Text.SourceSpan),
                "This file is not part of any project in the solution. Code the IDE cannot show "
                + "must not be executed — add its directory as a workspace member "
                + "([tool.uv.workspace].members) so it can be browsed, compiled and debugged.");
        }

        // ---- Rule 2 -----------------------------------------------------------

        private void CheckObjectOrientation(Module module, bool inTestProject)
        {
            foreach (Stmt statement in module.Body)
            {
                string name;
                HelixPythonNamespace.Python.Text.SourceSpan span;
                if (statement is FunctionDef fn) { name = fn.Name; span = fn.Span; }
                else if (statement is AsyncFunctionDef afn) { name = afn.Name; span = afn.Span; }
                else continue;

                // The PSM-mandated process entry point. Python cannot express it inside a class.
                if (IsEntryPoint(name)) continue;

                // Inside a test project, the functions the runner can only discover at module
                // level. Without this, rules 2 and 3 contradict each other — rule 3 requires the
                // user's tests to live in a test project, and rule 2 would then forbid the only
                // shape pytest and unittest can collect. Gated on the project kind, not the
                // function name, so production code cannot opt out of rule 2 by renaming.
                if (inTestProject && PythonTestContentDetector.IsTestProtocolFunction(statement))
                    continue;

                Report(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, span,
                    "Module-level function '" + name + "' is not object-oriented. Move it into a "
                    + "class as a method (use @staticmethod if it needs no instance state); only "
                    + "'def main()'" + (inTestProject
                        ? " and the test-runner protocol (test_*, @pytest.fixture, pytest_* hooks,"
                          + " setup_module/teardown_module) may"
                        : " may")
                    + " stay at module level.");
            }
        }

        // ---- Rule 3 -----------------------------------------------------------

        private void CheckTestFilePlacement(Module module, string owner, bool inTestProject)
        {
            // A file no member owns is rule 1's business, not rule 3's. Reporting both would tell
            // the user to move the same file to two different places at once.
            //
            // Single-file mode is the exception, and the one case where "no owner" is not rule 1's
            // business: rule 1 is off there (see SolutionPolicy.ForSingleFile) and a lone file can
            // never acquire an owner, so bailing out would silently exempt it from rule 3 — a test
            // file would be the one thing you could run outside a solution that you cannot run
            // inside one.
            bool singleFile = _policy.EnforceSingleFileImports;
            if (string.IsNullOrEmpty(owner) && !singleFile) return;

            // The one project kind allowed to hold tests.
            if (inTestProject) return;

            // Name/directory conventions first — they are the cheap, primary signal. Content
            // detection then catches test material that simply avoided the conventions: a file
            // called verification.py holding a TestCalculator class is a test file whatever it is
            // named, and a name-only rule could never see it.
            // In single-file mode the file's own directory stands in for the owning project, so the
            // name limb (test_*, *_test, conftest.py) still applies and the directory limb, which
            // is relative to the owner, correctly finds nothing.
            string ownerForNaming = owner ?? SafeDirectoryName(_filePath);
            bool byName = HelixPythonNamespace.Core.Policy.TestFilePlacementRule.IsTestFile(
                _filePath, ownerForNaming, HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python);
            if (!byName && !PythonTestContentDetector.IsTestModule(module)) return;

            if (singleFile)
            {
                // No project to name, and no move that fixes it: the remedy is the same one rule 5
                // gives, because a test project is a project and projects live in solutions.
                Report(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject,
                    default(HelixPythonNamespace.Python.Text.SourceSpan),
                    "'" + System.IO.Path.GetFileName(_filePath) + "' is a test file"
                    + (byName ? string.Empty : " (it declares unit tests)")
                    + ", and only a test project may contain one. A file opened on its own is in no "
                    + "project at all — create a solution (File > New > Solution) with a test "
                    + "project (kind = \"test\") and put it there.");
                return;
            }

            Report(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject,
                default(HelixPythonNamespace.Python.Text.SourceSpan),
                HelixPythonNamespace.Core.Policy.TestFilePlacementRule.BuildMessage(
                    _filePath, System.IO.Path.GetFileName(owner), _policy.GetProjectKind(owner),
                    detectedByContent: !byName));
        }

        // Path.GetDirectoryName throws on "" and on a malformed path; an untitled single-file
        // buffer has exactly that, and rule 3's name limb works fine with no directory at all.
        private static string SafeDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return System.IO.Path.GetDirectoryName(path); }
            catch (ArgumentException) { return null; }
            catch (System.IO.PathTooLongException) { return null; }
        }

        // `main` and the PSM entry-module name variants (__main__/_main/main_/<name>_main).
        private static bool IsEntryPoint(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (string.Equals(name, "main", StringComparison.Ordinal)) return true;
            string trimmed = name.Trim('_');
            return string.Equals(trimmed, "main", StringComparison.Ordinal)
                || name.EndsWith("_main", StringComparison.Ordinal);
        }

        private void Report(string code, HelixPythonNamespace.Python.Text.SourceSpan span, string message)
        {
            _diagnostics.Add(new CompileDiagnostic(
                _filePath, code, ParseSeverity.Error, span, message));
        }
    }
}
