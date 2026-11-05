using System;
using System.IO;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Policy
{
    /// <summary>
    /// Solution policy rule 3 (<c>PY8003</c>): <b>only a test project may contain test files.</b>
    ///
    /// A class library, an executable app, an executable GUI app — every project kind except
    /// <see cref="ProjectKind.TestProject"/> — is barred from holding unit-test files. Like rules 1
    /// and 2 this is a HARD blocker, not lint: the virtual compiler reports it as an error, and
    /// Compile, Run and Debug all refuse to proceed while a violation exists.
    ///
    /// The reason is the same one that makes the IDE keep tests in a sibling project at all
    /// (docs/python-solution-model.md's test-package rule, mirroring VS2022's MSTest model): a
    /// package that ships its own tests cannot be built, packaged or referenced without dragging
    /// its test harness and test-only dependencies along, and the IDE cannot offer per-project
    /// "Run Tests"/"Debug Tests" against a project that is simultaneously the subject and the test.
    ///
    /// This class is the ONE authority for the rule. It lives in the foundation assembly precisely
    /// so both callers can share it without duplicating the predicate:
    /// <list type="bullet">
    /// <item>the Python compliance pass (<c>ComplianceChecker</c>) for <c>.py</c> files, and</item>
    /// <item>the solution-wide sweep (<c>MainForm.FindPolicyViolations</c>) for the non-Python
    /// languages, whose files never reach the Python compiler.</item>
    /// </list>
    ///
    /// Third-party code is exempt, as it is from rules 1 and 2 — the caller applies that filter
    /// (<c>SolutionPolicy.IsUserWritten</c> / <c>ThirdPartyDetector</c>) before asking.
    /// </summary>
    public static class TestFilePlacementRule
    {
        /// <summary>The diagnostic code reported for a violation. Unsuppressible, like all PY8xxx.</summary>
        public const string DiagnosticCode = "PY8003";

        /// <summary>
        /// True when <paramref name="filePath"/> is a test file by the conventions of
        /// <paramref name="language"/> — a test-marked file NAME, or any file under one of the
        /// language's test directories.
        ///
        /// <paramref name="projectDirectory"/> is the directory of the project that OWNS the file,
        /// and the directory-name check is deliberately applied only to the path BELOW it. A
        /// solution stored anywhere under an ancestor folder literally named <c>tests</c> (this
        /// repository's own <c>tests/fixtures/python/…</c> layout is exactly that) must not have
        /// every one of its files classified as a test file because of a folder above the project
        /// root — that mistake previously exempted real entry modules from PSM-06 detection and
        /// left Run/Debug permanently "blocked: missing an entry module".
        /// </summary>
        public static bool IsTestFile(string filePath, string projectDirectory, ProjectLanguage language)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            LanguageWorkspaceDescriptor descriptor = LanguageDescriptors.For(language);
            string fileName = Path.GetFileName(filePath);
            string stem = Path.GetFileNameWithoutExtension(filePath);

            if (descriptor.TestFileNames != null)
                foreach (string exact in descriptor.TestFileNames)
                    if (string.Equals(fileName, exact, StringComparison.OrdinalIgnoreCase)) return true;

            if (descriptor.TestFileNamePrefixes != null)
                foreach (string prefix in descriptor.TestFileNamePrefixes)
                    if (!string.IsNullOrEmpty(prefix)
                        && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

            if (descriptor.TestFileNameSuffixes != null)
                foreach (string suffix in descriptor.TestFileNameSuffixes)
                    if (!string.IsNullOrEmpty(suffix)
                        && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;

            if (descriptor.TestDirectoryNames != null)
                foreach (string testDir in descriptor.TestDirectoryNames)
                    if (IsUnderProjectRelativeDirectory(filePath, projectDirectory, testDir)) return true;

            return false;
        }

        /// <summary>
        /// The user-facing explanation of a violation. Names the offending file and the project
        /// that must not hold it, and states the resolution (move it to a test project) rather
        /// than only the prohibition.
        /// </summary>
        /// <param name="detectedByContent">
        /// True when the file was recognized by what it declares rather than by its name. Without
        /// saying so, "Test file 'verification.py'" reads like a bug to the user — nothing about
        /// that name suggests a test, and they would have no idea what to change.
        /// </param>
        public static string BuildMessage(string filePath, string projectName, ProjectKind kind,
            bool detectedByContent = false)
        {
            string fileName = string.IsNullOrEmpty(filePath) ? "This file" : Path.GetFileName(filePath);
            string owner = string.IsNullOrEmpty(projectName) ? "this project" : "'" + projectName + "'";
            string label = KindLabel(kind);
            string a = Article(label);
            string why = detectedByContent
                ? " (it declares unit tests — a unittest.TestCase subclass, a Test* class with "
                  + "test_* methods, or pytest test functions)"
                : string.Empty;
            return "Test file '" + fileName + "'" + why + " is inside " + owner + ", which is "
                + a + " " + label + ". " + char.ToUpperInvariant(a[0]) + a.Substring(1) + " " + label
                + " must not contain unit test files — move it to a test project "
                + "(kind = \"test\"), conventionally the sibling '<project>_tests', which "
                + "references the project it exercises.";
        }

        // "an executable app", "a class library". The labels are a closed set (see KindLabel), all
        // plain English words, so the vowel test is sufficient — no acronym-initialism cases.
        private static string Article(string label)
        {
            if (string.IsNullOrEmpty(label)) return "a";
            return "aeiouAEIOU".IndexOf(label[0]) >= 0 ? "an" : "a";
        }

        /// <summary>The kind, phrased the way the rule talks about it in a message.</summary>
        public static string KindLabel(ProjectKind kind)
        {
            switch (kind)
            {
                case ProjectKind.App: return "executable app";
                case ProjectKind.GuiApp: return "executable GUI app";
                case ProjectKind.FlaskWebApp: return "web app";
                case ProjectKind.Executable: return "executable app";
                case ProjectKind.CudaExecutable: return "CUDA executable";
                case ProjectKind.CudaLibrary: return "CUDA library";
                case ProjectKind.TestProject: return "test project";
                default: return "class library";
            }
        }

        // `<projectDir>/<segment>/…`, where every part of <segment> must match a whole path
        // component — "tests" must not match a folder named "tests_helpers", and "src/test/java"
        // must match those three components in order.
        private static bool IsUnderProjectRelativeDirectory(string filePath, string projectDirectory, string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;

            string relative = ProjectRelativePath(filePath, projectDirectory);
            if (relative == null) return false;

            // Both separators on every platform — see IO.PathComparison.SplitSegments. On POSIX
            // the two Path.*SeparatorChar constants are the same character, so a Windows-style
            // path would split into a single segment and no directory convention could ever match.
            string[] parts = IO.PathComparison.SplitSegments(relative);
            string[] wanted = IO.PathComparison.SplitSegments(segment);
            if (wanted.Length == 0) return false;

            // The last part of `parts` is the file name itself, so a match must start strictly
            // before it — a FILE named "tests" is not a test directory.
            for (int i = 0; i + wanted.Length < parts.Length; i++)
            {
                bool all = true;
                for (int j = 0; j < wanted.Length; j++)
                {
                    if (!string.Equals(parts[i + j], wanted[j], StringComparison.OrdinalIgnoreCase))
                    {
                        all = false;
                        break;
                    }
                }
                if (all) return true;
            }
            return false;
        }

        // The path below projectDirectory, or the bare file name when the file is not under it
        // (never the full absolute path — see the IsTestFile remarks).
        private static string ProjectRelativePath(string filePath, string projectDirectory)
        {
            if (string.IsNullOrEmpty(projectDirectory)) return Path.GetFileName(filePath);
            try
            {
                // Path IDENTITY: does this file actually live under this project? Follows the
                // filesystem's own case rules, because getting it wrong here means measuring a
                // file's path against a project that does not contain it.
                string full = Path.GetFullPath(filePath);
                string dir = Path.GetFullPath(projectDirectory);
                if (IO.PathComparison.IsUnderDirectory(full, dir))
                    return full.Substring(IO.PathComparison.TrimTrailing(dir).Length + 1);
                return Path.GetFileName(full);
            }
            catch { return Path.GetFileName(filePath); }
        }
    }
}
