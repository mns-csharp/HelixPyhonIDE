namespace HelixPythonNamespace.Launcher
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Workspace.Execution;

    /// <summary>
    /// The three verbs that READ the solution — <c>--compile</c>, <c>--lint</c>,
    /// <c>--code-review</c> — each over the whole solution or over one package.
    ///
    /// They report the four solution rules rather than being gated by them. See
    /// <see cref="LauncherVerbs.Executes"/> for why: refusing to compile-check source because it
    /// failed a compile-check leaves the user with no way to find out what is wrong.
    ///
    /// The file set comes from <see cref="SolutionSourceInventory"/>, the same walk Build ▸ Compile
    /// and Analysis ▸ Lint use in the IDE, so "N files were read" means the same thing on both.
    /// </summary>
    internal static class AnalysisVerbs
    {
        // ---- --compile -----------------------------------------------------------------

        internal static int Compile(SolutionModel solution, ProjectModel scope, SolutionRunOptions options,
                                    LauncherOptions cli, TextWriter outw, TextWriter errw)
        {
            List<HelixPythonNamespace.VirtualCompiler.WorkspaceFile> files =
                SolutionSourceInventory.WorkspaceFiles(solution, scope);
            if (cli.DryRun) return DescribeFileSet("compile", files.Count, ScopeLabel(scope), FilePathsOf(files), outw);
            if (files.Count == 0) return NothingToDo("compile", scope, outw);

            var compileOptions = new HelixPythonNamespace.VirtualCompiler.CompileOptions
            {
                // Resolve imports against the SAME sys.path the launcher hands python.exe, so a
                // packaging fault shows up here instead of as an ImportError on the first run.
                SearchPath = SolutionRunPlanner.BuildPythonSearchPath(solution, null),
                // Same policy Run enforces, so a compile surfaces the blockers up front instead of
                // the user discovering them only when a launch is refused.
                Policy = SolutionRunPlanner.BuildSolutionPolicy(solution, options),
            };

            var all = new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>();
            try
            {
                IReadOnlyList<HelixPythonNamespace.VirtualCompiler.CompileFileResult> perFile =
                    HelixPythonNamespace.VirtualCompiler.VirtualCompiler.CompileSolution(
                        files, compileOptions, CancellationToken.None);
                foreach (HelixPythonNamespace.VirtualCompiler.CompileFileResult f in perFile)
                {
                    if (f == null || f.Result == null || f.Result.Diagnostics == null) continue;
                    all.AddRange(f.Result.Diagnostics);
                }

                // The inventory only walks MEMBER projects, so a non-member file could never report
                // itself — and rule 1 is precisely about those. Sweep the solution tree separately.
                List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> policy =
                    SolutionRunPlanner.FindPolicyViolations(solution, options);
                all.AddRange(scope == null ? policy : WithinScope(policy, scope));
            }
            catch (Exception ex)
            {
                // A crashing compiler must not be indistinguishable from a clean one.
                all.Add(new HelixPythonNamespace.VirtualCompiler.CompileDiagnostic(
                    "", "PY9999", HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error,
                    default(HelixPythonNamespace.Python.Text.SourceSpan),
                    "Compiler crashed: " + ex.Message));
            }

            // PY8004 is a property of the whole solution and has no diagnostic code of its own — it
            // is a load-gate state, so it is reported as itself rather than dressed up as a
            // diagnostic the rest of the system would not recognise.
            if (!string.IsNullOrEmpty(options.StructureBlockReason))
            {
                errw.WriteLine("PY8004: " + options.StructureBlockReason);
                errw.WriteLine();
            }

            int errors = 0, warnings = 0;
            foreach (HelixPythonNamespace.VirtualCompiler.CompileDiagnostic d in all)
            {
                outw.WriteLine(d.ToString());
                if (d.Severity == HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error) errors++;
                else warnings++;
            }

            outw.WriteLine(string.Format("Compile ({0}): {1} error(s), {2} warning(s) in {3} file(s).",
                ScopeLabel(scope), errors, warnings, files.Count));
            if (scope != null)
                outw.WriteLine("  Scoped to one package: the solution-wide rules (PY8001, PY8004) are "
                    + "reported only for files inside it. Compile the whole solution to see all of them.");

            // A structure the IDE will not vouch for is not a clean compile, whatever the file-level
            // diagnostics said.
            bool structureUnverified = !string.IsNullOrEmpty(options.StructureBlockReason);
            return errors > 0 || structureUnverified
                ? SolutionLauncher.ExitFindings
                : 0;
        }

        // ---- --lint --------------------------------------------------------------------

        internal static int Lint(SolutionModel solution, ProjectModel scope,
                                 LauncherOptions cli, TextWriter outw, TextWriter errw)
        {
            List<string> files = SolutionSourceInventory.PythonFiles(solution, scope);
            if (cli.DryRun) return DescribeFileSet("lint", files.Count, ScopeLabel(scope), files, outw);
            if (files.Count == 0) return NothingToDo("lint", scope, outw);

            var linter = new HelixPythonNamespace.Lint.PythonLinter();
            int errors = 0, total = 0, unreadable = 0;
            foreach (string file in files)
            {
                string source;
                try { source = File.ReadAllText(file); }
                catch { unreadable++; continue; }

                foreach (HelixPythonNamespace.Lint.LintFinding f in linter.Run(source, file, CancellationToken.None))
                {
                    outw.WriteLine(string.Format("{0}({1},{2}): {3} {4}: {5}",
                        file, f.Span.Start.Line, f.Span.Start.Column,
                        f.Severity.ToString().ToLowerInvariant(), f.Code, f.Message));
                    total++;
                    if (f.Severity == AnalysisSeverity.Error) errors++;
                }
            }

            outw.WriteLine(string.Format("Lint ({0}): {1} finding(s) in {2} file(s).",
                ScopeLabel(scope), total, files.Count));
            // A file nobody could read contributed no findings, and its silence is absence of
            // evidence rather than a clean result.
            if (unreadable > 0)
                errw.WriteLine("  " + unreadable + " file(s) could not be read and were NOT linted.");

            return errors > 0 || unreadable > 0 ? SolutionLauncher.ExitFindings : 0;
        }

        // ---- --code-review -------------------------------------------------------------

        internal static int CodeReview(SolutionModel solution, ProjectModel scope,
                                       LauncherOptions cli, TextWriter outw, TextWriter errw)
        {
            List<string> files = SolutionSourceInventory.PythonFiles(solution, scope);
            string label = scope == null ? "whole solution" : "package " + scope.Name;
            if (cli.DryRun) return DescribeFileSet("review", files.Count, ScopeLabel(scope), files, outw);
            if (files.Count == 0) return NothingToDo("review", scope, outw);

            var sources = new List<HelixPythonNamespace.CodeReview.ReviewSource>();
            foreach (string file in files)
            {
                try { sources.Add(new HelixPythonNamespace.CodeReview.ReviewSource(file, File.ReadAllText(file))); }
                catch { /* an unreadable file is reported by the engine as unreviewed */ }
            }

            HelixPythonNamespace.CodeReview.ReviewReport report = new HelixPythonNamespace.CodeReview.CodeReviewEngine()
                .Review(sources, HelixPythonNamespace.CodeReview.ReviewOptions.Default, label, CancellationToken.None);

            foreach (HelixPythonNamespace.CodeReview.ReviewFinding f in report.Findings)
            {
                outw.WriteLine(string.Format("{0}({1}): {2} {3}: {4}",
                    f.FilePath, f.Line, f.Severity, f.RuleId, f.Message));
                // A review comment is three things — what is wrong, why a reviewer cares, and what
                // to do instead. Printing only the first turns the review into a lint pass.
                if (!string.IsNullOrEmpty(f.Rationale)) outw.WriteLine("      why: " + f.Rationale);
                if (!string.IsNullOrEmpty(f.Suggestion)) outw.WriteLine("      fix: " + f.Suggestion);
            }

            outw.WriteLine();
            outw.WriteLine("Code review (" + label + "): "
                + HelixPythonNamespace.CodeReview.ReviewMarkdownWriter.VerdictText(report.Verdict));
            if (!string.IsNullOrEmpty(report.VerdictReason)) outw.WriteLine("  " + report.VerdictReason);
            outw.WriteLine(string.Format("  {0} finding(s) over {1} of {2} file(s) submitted.",
                report.Findings.Count, report.Summary.FilesReviewed, report.Summary.FilesSubmitted));

            // IsCompleteReview gates the verdict: silence from a file nobody opened is absence of
            // evidence, and a report that did not say so would read as "this code is clean".
            if (!report.IsCompleteReview)
            {
                errw.WriteLine("  REVIEW INCOMPLETE - this verdict does not cover everything submitted.");
                foreach (string unparsed in report.Unparsed) errw.WriteLine("    not parsed: " + unparsed);
                foreach (string failure in report.RuleFailures) errw.WriteLine("    rule failed: " + failure);
            }

            // A CI gate wants "would a reviewer sign this off", which is exactly the verdict.
            return report.Verdict == HelixPythonNamespace.CodeReview.ReviewVerdict.RequestChanges
                   || !report.IsCompleteReview
                ? SolutionLauncher.ExitFindings
                : 0;
        }

        // ---- shared --------------------------------------------------------------------

        internal static string ScopeLabel(ProjectModel scope)
        {
            return scope == null ? "whole solution" : scope.Name;
        }

        private static int NothingToDo(string verb, ProjectModel scope, TextWriter outw)
        {
            outw.WriteLine("Nothing to " + verb + ": no Python files under " + ScopeLabel(scope) + ".");
            return 0;
        }

        private static int DescribeFileSet(string verb, int count, string scopeLabel,
                                           IEnumerable<string> paths, TextWriter outw)
        {
            outw.WriteLine(verb + " (" + scopeLabel + "): " + count + " file(s) would be read.");
            foreach (string p in paths) outw.WriteLine("  " + p);
            return 0;
        }

        private static List<string> FilePathsOf(IEnumerable<HelixPythonNamespace.VirtualCompiler.WorkspaceFile> files)
        {
            var paths = new List<string>();
            foreach (HelixPythonNamespace.VirtualCompiler.WorkspaceFile f in files) paths.Add(f.FilePath);
            return paths;
        }

        /// <summary>
        /// The solution-wide policy findings that belong to a scoped package. A PY8001 orphan lives
        /// outside every project by definition, so it cannot be under this one — which is correct:
        /// it is not this package's problem, and the summary says the scope narrowed the check.
        /// </summary>
        private static List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> WithinScope(
            IEnumerable<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> diagnostics, ProjectModel scope)
        {
            var kept = new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>();
            string dir = scope.ProjectDirectory;
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(scope.FilePath))
                dir = Path.GetDirectoryName(scope.FilePath);
            if (string.IsNullOrEmpty(dir)) return kept;

            string prefix;
            try
            {
                prefix = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch { return kept; }

            foreach (HelixPythonNamespace.VirtualCompiler.CompileDiagnostic d in diagnostics)
            {
                if (string.IsNullOrEmpty(d.FilePath)) continue;
                string full;
                try { full = Path.GetFullPath(d.FilePath); }
                catch { continue; }
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) kept.Add(d);
            }
            return kept;
        }
    }
}
