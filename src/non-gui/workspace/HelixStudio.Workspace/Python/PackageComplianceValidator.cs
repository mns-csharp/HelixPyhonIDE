using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Stable rule identifiers from SRS__Python_Project_System_and_SPPFE.md.
    /// Values match the normative PSM number emitted in diagnostics.
    /// </summary>
    public enum PackageRule
    {
        ExecutableMissingMain = 6,
        EntryModuleOutsideProjectRoot = 7,
        MultipleMainModules = 8,
        MainMissingMainFunc = 9,
        MainMustBeTopLevel = 10,
        AsyncMain = 11,
        MainHasParameters = 12,
        InvalidMainReturn = 13,
        MultipleMainFuncs = 14,
        MainFuncOutsideMain = 15,
        MissingCanonicalGuard = 16,
        InvalidInvocationGuard = 17,
        TopLevelExecutableStatements = 20,
        LibraryHasMain = 22,
        LibraryHasMainFunction = 23,
        LibraryHasInvocationGuard = 24,
    }

    public sealed class PackageComplianceFinding
    {
        public PackageComplianceFinding(
            string package,
            PackageRule rule,
            string message,
            IList<string> paths,
            int line = 1)
        {
            Package = package ?? string.Empty;
            Rule = rule;
            Message = message ?? string.Empty;
            Paths = paths != null ? new List<string>(paths) : new List<string>();
            Line = Math.Max(1, line);
        }

        public string Package { get; }
        public PackageRule Rule { get; }
        public string Message { get; }
        public List<string> Paths { get; }
        /// <summary>One-based source line for the primary path.</summary>
        public int Line { get; }

        public override string ToString()
        {
            return "[PSM-" + ((int)Rule).ToString("00") + "] " + Package + ": " + Message;
        }
    }

    /// <summary>
    /// Side-effect-free validation of each loaded Python project against PSM-06 through PSM-24.
    /// Entry-module equivalence is owned by <see cref="PythonSolutionAuditor.IsMainModule"/>;
    /// every question about what a module CONTAINS (a top-level main, a __main__ guard, what that
    /// guard invokes, and on which line) is answered by <see cref="PythonEntryPointFacts"/>, which
    /// reads the parse tree. Nothing here pattern-matches source text: the regexes this class used
    /// to carry rejected `if __name__ == "__main__":  # run directly` as a non-canonical guard —
    /// the comment stood where the pattern demanded a newline — and blocked Run on valid code.
    /// </summary>
    public static class PackageComplianceValidator
    {
        public static IReadOnlyList<PackageComplianceFinding> Validate(SolutionModel solution)
        {
            var findings = new List<PackageComplianceFinding>();
            if (solution == null || solution.Projects == null) return findings;

            var projects = ExistingProjectDirectories(solution);
            foreach (KeyValuePair<ProjectModel, string> item in projects)
                ValidateProject(item.Key, item.Value, projects, findings);

            return findings;
        }

        private static void ValidateProject(
            ProjectModel project,
            string projectDirectory,
            List<KeyValuePair<ProjectModel, string>> projects,
            List<PackageComplianceFinding> findings)
        {
            List<string> files = OwnPythonFiles(projectDirectory, projects, project);
            var entryModules = new List<string>();
            var mainFunctionFiles = new List<string>();
            var guardFiles = new List<string>();
            // One parse per file, reused for every rule below AND for the line each finding
            // reports — the tree already knows where every construct is, so nothing has to go
            // looking for it a second time afterwards.
            var facts = new Dictionary<string, PythonEntryPointFacts>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                // Test scripts routinely carry their own `if __name__ == "__main__":
                // unittest.main()` (or pytest-runner) block, and this is a completely
                // normal, legitimate convention — it says nothing about whether the
                // PROJECT itself (app or library) has a proper single entry point, so
                // test files are exempt from every #3/#PSM entry-point rule here.
                if (IsTestFile(file, projectDirectory)) continue;

                if (PythonSolutionAuditor.IsMainModule(file)) entryModules.Add(file);

                string source;
                try { source = File.ReadAllText(file); }
                catch { continue; }

                PythonEntryPointFacts moduleFacts = PythonEntryPointFacts.Analyze(source, file);
                facts[file] = moduleFacts;

                if (moduleFacts.HasModuleLevelMainFunction) mainFunctionFiles.Add(file);
                if (moduleFacts.HasInvocationGuard) guardFiles.Add(file);
            }

            List<string> rootEntryModules = entryModules.FindAll(
                file => SameDirectory(Path.GetDirectoryName(file), projectDirectory));
            bool requiresEntryModule = ProjectKinds.RequiresMainModule(project.Kind);

            if (entryModules.Count > 1)
                Add(findings, project, PackageRule.MultipleMainModules,
                    "Executable project '" + project.Name +
                    "' contains multiple equivalent entry modules.", entryModules);

            if (mainFunctionFiles.Count > 1)
                Add(findings, project, PackageRule.MultipleMainFuncs,
                    "Executable project '" + project.Name +
                    "' contains multiple top-level main() functions.", mainFunctionFiles,
                    MainFunctionLine(facts, mainFunctionFiles[0]));

            foreach (string file in mainFunctionFiles)
                if (!PythonSolutionAuditor.IsMainModule(file))
                    Add(findings, project, PackageRule.MainFuncOutsideMain,
                        "The name main is reserved for the project entry module, not '" +
                        Path.GetFileName(file) + "'.", file, MainFunctionLine(facts, file));

            if (requiresEntryModule && entryModules.Count == 0)
                Add(findings, project, PackageRule.ExecutableMissingMain,
                    "Executable project '" + project.Name +
                    "' is missing an entry module (main.py, __main__.py, or *_main.py).",
                    projectDirectory);
            else if (requiresEntryModule && entryModules.Count > 0 && rootEntryModules.Count == 0)
                Add(findings, project, PackageRule.EntryModuleOutsideProjectRoot,
                    "The entry module must be located directly in the project root.", entryModules);
            else if (!project.IsExecutable && entryModules.Count > 0)
                Add(findings, project, PackageRule.LibraryHasMain,
                    "Library project '" + project.Name + "' must not contain an entry module.",
                    entryModules);

            foreach (string entryModule in rootEntryModules)
                ValidateRootEntryModule(project, entryModule, Facts(facts, entryModule), findings);

            if (!project.IsExecutable && mainFunctionFiles.Count > 0)
                Add(findings, project, PackageRule.LibraryHasMainFunction,
                    "Library project '" + project.Name +
                    "' must not define a top-level main() function.", mainFunctionFiles,
                    MainFunctionLine(facts, mainFunctionFiles[0]));

            if (!project.IsExecutable && guardFiles.Count > 0)
                Add(findings, project, PackageRule.LibraryHasInvocationGuard,
                    "Library project '" + project.Name +
                    "' must not contain an application invocation guard.", guardFiles,
                    InvocationGuardLine(facts, guardFiles[0]));
            else
                foreach (string guardFile in guardFiles)
                    if (!rootEntryModules.Contains(guardFile))
                        Add(findings, project, PackageRule.InvalidInvocationGuard,
                            "Only the project-root entry module may contain an application invocation guard.",
                            guardFile, InvocationGuardLine(facts, guardFile));
        }

        private static PythonEntryPointFacts Facts(
            Dictionary<string, PythonEntryPointFacts> facts, string path)
        {
            PythonEntryPointFacts found;
            return path != null && facts.TryGetValue(path, out found) ? found : null;
        }

        /// <summary>
        /// The line a finding about <paramref name="path"/>'s <c>def main</c> should point at, from
        /// the parse tree. 1 when the file was never analyzed (unreadable) or has no such function —
        /// callers only ask when they already know it has one.
        /// </summary>
        private static int MainFunctionLine(
            Dictionary<string, PythonEntryPointFacts> facts, string path)
        {
            PythonEntryPointFacts found = Facts(facts, path);
            return found != null && found.MainFunctionLine > 0 ? found.MainFunctionLine : 1;
        }

        /// <summary>The line of <paramref name="path"/>'s <c>__main__</c> guard; 1 when unknown.</summary>
        private static int InvocationGuardLine(
            Dictionary<string, PythonEntryPointFacts> facts, string path)
        {
            PythonEntryPointFacts found = Facts(facts, path);
            return found != null && found.InvocationGuardLine > 0 ? found.InvocationGuardLine : 1;
        }

        private static void ValidateRootEntryModule(
            ProjectModel project,
            string entryModule,
            PythonEntryPointFacts facts,
            List<PackageComplianceFinding> findings)
        {
            // No usable tree — the file was unreadable, or its syntax errors made the parser
            // recover rather than report. Either way its entry-point shape is unknown, and
            // reporting PSM-09/11/12/16 from a guess would blame the wrong thing: the syntax
            // error the compiler already reports is what the user has to fix first.
            if (facts == null || !facts.Trustworthy) return;

            if (!facts.HasModuleLevelMainFunction)
            {
                Add(findings, project, PackageRule.MainMissingMainFunc,
                    "The entry module must define exactly one top-level def main().", entryModule);
            }
            else
            {
                if (facts.MainIsAsync)
                    Add(findings, project, PackageRule.AsyncMain,
                        "The project entry point must be synchronous.", entryModule,
                        facts.MainFunctionLine);
                if (facts.MainHasRequiredParameter)
                    Add(findings, project, PackageRule.MainHasParameters,
                        "The project entry point must not declare parameters.", entryModule,
                        facts.MainFunctionLine);
            }

            // PSM-16 asks one question — does the __main__ guard invoke main()? — and the tree
            // answers it structurally. Comments, blank lines, an `import sys` before the call,
            // single vs. double quotes, `main()` vs. `sys.exit(main())` vs.
            // `raise SystemExit(main())`: all irrelevant, none of them a violation.
            if (!facts.GuardInvokesMain)
                Add(findings, project, PackageRule.MissingCanonicalGuard,
                    "The entry module must invoke main() through the canonical __name__ guard.",
                    entryModule,
                    facts.InvocationGuardLine > 0 ? facts.InvocationGuardLine : 1);
        }

        private static List<KeyValuePair<ProjectModel, string>> ExistingProjectDirectories(
            SolutionModel solution)
        {
            var result = new List<KeyValuePair<ProjectModel, string>>();
            foreach (ProjectModel project in solution.Projects)
            {
                if (project == null) continue;
                // PSM rules are PYTHON rules. Applying them to a non-Python project means a
                // stray .py beside it could block Run for a solution with no Python in it.
                if (project.Language != HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python) continue;
                string directory = project.ProjectDirectory;
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    result.Add(new KeyValuePair<ProjectModel, string>(
                        project, NormalizeDirectory(directory)));
            }
            return result;
        }

        private static List<string> OwnPythonFiles(
            string directory,
            List<KeyValuePair<ProjectModel, string>> projects,
            ProjectModel project)
        {
            // A file/folder excluded from the project (SRS §5.9) is exempt from PSM
            // compliance too, not just from the Explorer/analysis tree — an excluded
            // debug script must not be able to block Run for the whole project.
            HashSet<string> excluded = HelixPythonNamespace.Workspace.Services.SolutionService
                .ResolveExcludedFullPaths(directory, project?.ExcludedPaths);

            var result = new List<string>();
            foreach (string file in PythonSolutionAuditor.EnumeratePythonFiles(directory))
            {
                if (HelixPythonNamespace.Workspace.Services.SolutionService.IsExcluded(file, excluded)) continue;
                if (string.Equals(
                    NearestProjectDirectory(file, projects),
                    directory,
                    StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            }
            return result;
        }

        private static string NearestProjectDirectory(
            string file,
            List<KeyValuePair<ProjectModel, string>> projects)
        {
            string best = null;
            int bestLength = -1;
            string containingDirectory =
                NormalizeDirectory(Path.GetDirectoryName(file) ?? string.Empty) +
                Path.DirectorySeparatorChar;

            foreach (KeyValuePair<ProjectModel, string> project in projects)
            {
                string candidate = project.Value + Path.DirectorySeparatorChar;
                if (containingDirectory.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Length > bestLength)
                {
                    best = project.Value;
                    bestLength = candidate.Length;
                }
            }
            return best;
        }

        private static void Add(
            List<PackageComplianceFinding> findings,
            ProjectModel project,
            PackageRule rule,
            string message,
            string path,
            int line = 1)
        {
            Add(findings, project, rule, message, new List<string> { path }, line);
        }

        private static void Add(
            List<PackageComplianceFinding> findings,
            ProjectModel project,
            PackageRule rule,
            string message,
            IList<string> paths,
            int line = 1)
        {
            findings.Add(new PackageComplianceFinding(project.Name, rule, message, paths, line));
        }

        /// <summary>
        /// True for a <c>test_*.py</c> / <c>*_test.py</c> file, or any file under a
        /// directory literally named <c>tests</c>/<c>test</c> — the same naming
        /// convention the import analyzer already uses to recognize test directories.
        /// </summary>
        // A file counts as a test file when its NAME says so, or when it sits inside a "tests"/
        // "test" subfolder of its OWN project. Deliberately checks only the path portion BELOW
        // projectDirectory, not the full absolute path: a solution stored anywhere under an
        // ancestor directory literally named "tests" (e.g. this repo's own tests/fixtures/python/…
        // fixture layout) must not have every one of its files misclassified as a test file just
        // because "tests" appears somewhere above the project root — that previously exempted
        // every fixture project's real entry module from PSM-06 entry-point detection, making
        // Run/Debug permanently "blocked: missing an entry module" even though main.py existed.
        private static bool IsTestFile(string path, string projectDirectory)
        {
            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase))
                return true;

            string relative = path;
            if (!string.IsNullOrEmpty(projectDirectory))
            {
                string normalizedDir = NormalizeDirectory(projectDirectory) + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                    relative = fullPath.Substring(normalizedDir.Length);
            }

            foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.Equals(part, "tests", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "test", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool SameDirectory(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            return string.Equals(
                NormalizeDirectory(left),
                NormalizeDirectory(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDirectory(string directory)
        {
            return Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
