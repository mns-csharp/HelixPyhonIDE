using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>The solution-management rule a finding relates to (requirement #3).</summary>
    public enum SolutionAuditRule
    {
        /// <summary><c>main.py</c> and any underscore variant (<c>_main.py</c>, <c>__main__.py</c>,
        /// <c>main_.py</c>, …) are treated as the same entry module.</summary>
        MainModuleEquivalence = 0,
        /// <summary>A solution must contain at most one main-equivalent module.</summary>
        SingleMainModule = 1,
        /// <summary>A solution must contain at most one module-level <c>def main()</c>.</summary>
        SingleMainFunction = 2
    }

    public enum SolutionAuditSeverity { Info = 0, Warning = 1, Error = 2 }

    /// <summary>One rule outcome from <see cref="PythonSolutionAuditor"/>.</summary>
    public sealed class SolutionAuditFinding
    {
        public SolutionAuditFinding(SolutionAuditRule rule, SolutionAuditSeverity severity,
                                    string message, IList<string> paths, string suggestedResolution)
        {
            Rule = rule;
            Severity = severity;
            Message = message ?? string.Empty;
            Paths = paths != null ? new List<string>(paths) : new List<string>();
            SuggestedResolution = suggestedResolution ?? string.Empty;
        }

        public SolutionAuditRule Rule { get; }
        public SolutionAuditSeverity Severity { get; }
        public string Message { get; }
        /// <summary>Absolute paths of the offending files (empty for a passing finding).</summary>
        public List<string> Paths { get; }
        public string SuggestedResolution { get; }

        public override string ToString()
        {
            return "[" + Severity + "] " + Rule + ": " + Message;
        }
    }

    /// <summary>Result of auditing a Python solution against the #3 rules.</summary>
    public sealed class SolutionAuditReport
    {
        public SolutionAuditReport(string solutionRoot)
        {
            SolutionRoot = solutionRoot ?? string.Empty;
            Findings = new List<SolutionAuditFinding>();
            MainModules = new List<string>();
            MainFunctionFiles = new List<string>();
        }

        public string SolutionRoot { get; }
        public List<SolutionAuditFinding> Findings { get; }
        /// <summary>Every main-equivalent module discovered (absolute paths).</summary>
        public List<string> MainModules { get; }
        /// <summary>Every file containing a module-level <c>def main</c> (absolute paths).</summary>
        public List<string> MainFunctionFiles { get; }

        /// <summary>True when no finding is an <see cref="SolutionAuditSeverity.Error"/>.</summary>
        public bool IsCompliant
        {
            get
            {
                foreach (SolutionAuditFinding f in Findings)
                    if (f.Severity == SolutionAuditSeverity.Error) return false;
                return true;
            }
        }

        /// <summary>
        /// The IDE may auto-generate a <c>pyproject.toml</c> only when the source is compliant
        /// (requirement #4c: "If there are no major issues … automatically generate a toml file").
        /// </summary>
        public bool CanAutoGenerateToml { get { return IsCompliant; } }
    }

    /// <summary>
    /// Audits a Python source tree against the solution-management rules of requirement #3:
    /// <list type="number">
    /// <item>main.py ≡ any underscore variant (<c>_main_.py</c>, <c>__main__.py</c>, <c>main_.py</c>, …).</item>
    /// <item>At most one main-equivalent module per solution.</item>
    /// <item>At most one module-level <c>def main()</c> per solution.</item>
    /// </list>
    /// Pure and side-effect free — it reads files but never writes. The GUI open-flow and the
    /// project import flow both run this and surface the findings + suggested resolutions.
    /// </summary>
    public static class PythonSolutionAuditor
    {
        /// <summary>
        /// True when <paramref name="fileNameOrPath"/> names a main-equivalent Python module.
        /// The base name is lower-cased and stripped of every underscore; the file is
        /// main-equivalent when the remainder is exactly "main" and the extension is .py/.pyi.
        /// So main.py, _main.py, main_.py, __main__.py, _main_.py, ___main___.py all match,
        /// while domain.py, main2.py and mymain.py do not.
        /// </summary>
        public static bool IsMainModule(string fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;
            string ext = Path.GetExtension(fileNameOrPath);
            if (!string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".pyi", StringComparison.OrdinalIgnoreCase))
                return false;

            string baseName = Path.GetFileNameWithoutExtension(fileNameOrPath);
            if (string.IsNullOrEmpty(baseName)) return false;

            // Version 1.1 entry-module equivalence: any <name>_main.py is a canonical
            // entry-module form in addition to main.py and __main__.py.
            if (baseName.EndsWith("_main", StringComparison.OrdinalIgnoreCase))
                return true;

            StringBuilder sb = new StringBuilder(baseName.Length);
            foreach (char c in baseName)
                if (c != '_') sb.Append(char.ToLowerInvariant(c));

            return sb.ToString() == "main";
        }

        /// <summary>Audit a solution rooted at a directory on disk.</summary>
        public static SolutionAuditReport Audit(string sourceRoot)
        {
            SolutionAuditReport report = new SolutionAuditReport(sourceRoot);
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                report.Findings.Add(new SolutionAuditFinding(
                    SolutionAuditRule.SingleMainModule, SolutionAuditSeverity.Error,
                    "Source directory does not exist: " + (sourceRoot ?? "(null)"),
                    null, "Choose an existing directory to open or import."));
                return report;
            }
            return Audit(EnumeratePythonFiles(sourceRoot), sourceRoot);
        }

        /// <summary>
        /// Audit an explicit set of Python files. <paramref name="sourceRoot"/> is used only for
        /// report metadata. This overload lets the caller reuse an already-scanned file list.
        /// </summary>
        public static SolutionAuditReport Audit(IEnumerable<string> pyFilePaths, string sourceRoot)
        {
            SolutionAuditReport report = new SolutionAuditReport(sourceRoot);
            if (pyFilePaths == null) return report;

            foreach (string path in pyFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (IsMainModule(path))
                    report.MainModules.Add(path);

                string text;
                try { text = File.ReadAllText(path); }
                catch { continue; }

                if (ContainsModuleLevelMainFunction(text))
                    report.MainFunctionFiles.Add(path);
            }

            report.MainModules.Sort(StringComparer.OrdinalIgnoreCase);
            report.MainFunctionFiles.Sort(StringComparer.OrdinalIgnoreCase);

            // Rule: at most one main-equivalent module.
            if (report.MainModules.Count > 1)
            {
                report.Findings.Add(new SolutionAuditFinding(
                    SolutionAuditRule.SingleMainModule, SolutionAuditSeverity.Error,
                    "The solution contains " + report.MainModules.Count +
                    " main-equivalent modules (main.py and its underscore variants are equivalent); exactly one is allowed.",
                    report.MainModules,
                    "Keep a single entry module and rename or remove the others (e.g. rename a secondary 'main.py' to '<feature>_app.py')."));
            }
            else
            {
                report.Findings.Add(new SolutionAuditFinding(
                    SolutionAuditRule.SingleMainModule, SolutionAuditSeverity.Info,
                    report.MainModules.Count == 1
                        ? "Single entry module: " + Path.GetFileName(report.MainModules[0]) + "."
                        : "No main-equivalent module found.",
                    report.MainModules, string.Empty));
            }

            // Rule: at most one module-level def main().
            if (report.MainFunctionFiles.Count > 1)
            {
                report.Findings.Add(new SolutionAuditFinding(
                    SolutionAuditRule.SingleMainFunction, SolutionAuditSeverity.Error,
                    "The solution declares a module-level 'def main()' in " + report.MainFunctionFiles.Count +
                    " files; exactly one entry point is allowed.",
                    report.MainFunctionFiles,
                    "Consolidate to a single 'def main()' entry point; rename the others (e.g. to 'run()' or '<feature>_main()')."));
            }
            else
            {
                report.Findings.Add(new SolutionAuditFinding(
                    SolutionAuditRule.SingleMainFunction, SolutionAuditSeverity.Info,
                    report.MainFunctionFiles.Count == 1
                        ? "Single entry point: def main() in " + Path.GetFileName(report.MainFunctionFiles[0]) + "."
                        : "No module-level def main() found.",
                    report.MainFunctionFiles, string.Empty));
            }

            return report;
        }

        /// <summary>
        /// Enumerate .py/.pyi files under <paramref name="root"/>, skipping the same virtual-env /
        /// cache / VCS directories the loader ignores (<see cref="SolutionService.IgnoredDirectories"/>).
        /// </summary>
        public static IEnumerable<string> EnumeratePythonFiles(string root)
        {
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { subs = new string[0]; }
                foreach (string sub in subs)
                    if (!SolutionService.IgnoredDirectories.Contains(Path.GetFileName(sub)))
                        stack.Push(sub);

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }
                foreach (string f in files)
                {
                    string ext = Path.GetExtension(f);
                    if (string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".pyi", StringComparison.OrdinalIgnoreCase))
                        yield return f;
                }
            }
        }

        /// <summary>
        /// True when <paramref name="source"/> defines a module-level <c>def main</c> /
        /// <c>async def main</c>. Decided on the parse tree by
        /// <see cref="PythonEntryPointFacts"/>, so a <c>main</c> method inside a class, a
        /// commented-out def, and one quoted inside a docstring are all excluded by the language's
        /// own rules rather than by approximating them with column and quote bookkeeping. A file
        /// the parser cannot handle falls back to that line scan — see
        /// <see cref="PythonEntryPointFacts.Analyze"/>.
        /// </summary>
        public static bool ContainsModuleLevelMainFunction(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return PythonEntryPointFacts.Analyze(source).HasModuleLevelMainFunction;
        }
    }
}
