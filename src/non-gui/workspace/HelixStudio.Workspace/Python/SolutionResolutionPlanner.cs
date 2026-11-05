using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// A single automatable fix: rename <see cref="OldPath"/> to <see cref="NewPath"/>.
    /// The new base name is deliberately chosen so it is NOT main-equivalent
    /// (see <see cref="PythonSolutionAuditor.IsMainModule"/>), which is what actually
    /// resolves the "at most one main-equivalent module" rule (#3c).
    /// </summary>
    public sealed class FileRenameAction
    {
        public FileRenameAction(string oldPath, string newPath, SolutionAuditRule rule, string reason)
        {
            OldPath = oldPath ?? string.Empty;
            NewPath = newPath ?? string.Empty;
            Rule = rule;
            Reason = reason ?? string.Empty;
        }

        public string OldPath { get; }
        public string NewPath { get; }
        public SolutionAuditRule Rule { get; }
        public string Reason { get; }

        public override string ToString()
        {
            return Path.GetFileName(OldPath) + " -> " + Path.GetFileName(NewPath);
        }
    }

    /// <summary>
    /// A single automatable fix: rename the module-level <c>def</c> <see cref="OldName"/> to
    /// <see cref="NewName"/> inside <see cref="FilePath"/> only (declaration + same-file call
    /// sites), via the scope-aware <see cref="AstIdentifierRewriter"/> — never touches other
    /// files, since a module-level <c>def main()</c> has no cross-file callers by definition
    /// (nothing outside the file could import a name that collides with every other module's
    /// own <c>main</c>).
    /// </summary>
    public sealed class FunctionRenameAction
    {
        public FunctionRenameAction(string filePath, string oldName, string newName, SolutionAuditRule rule, string reason)
        {
            FilePath = filePath ?? string.Empty;
            OldName = oldName ?? string.Empty;
            NewName = newName ?? string.Empty;
            Rule = rule;
            Reason = reason ?? string.Empty;
        }

        public string FilePath { get; }
        public string OldName { get; }
        public string NewName { get; }
        public SolutionAuditRule Rule { get; }
        public string Reason { get; }

        public override string ToString()
        {
            return Path.GetFileName(FilePath) + ": def " + OldName + "() -> def " + NewName + "()";
        }
    }

    /// <summary>
    /// A concrete, reviewable plan for bringing a solution into compliance with the #3 rules.
    /// <see cref="Renames"/> and <see cref="FunctionRenames"/> can both be applied automatically
    /// (one-click); <see cref="ManualFindings"/> holds only findings neither covers.
    /// </summary>
    public sealed class SolutionResolutionPlan
    {
        public SolutionResolutionPlan()
        {
            Renames = new List<FileRenameAction>();
            FunctionRenames = new List<FunctionRenameAction>();
            ManualFindings = new List<SolutionAuditFinding>();
        }

        public List<FileRenameAction> Renames { get; }
        public List<FunctionRenameAction> FunctionRenames { get; }
        public List<SolutionAuditFinding> ManualFindings { get; }

        /// <summary>The entry module kept unchanged (absolute path), or empty when none applies.</summary>
        public string CanonicalMainModule { get; internal set; } = string.Empty;

        /// <summary>The file whose <c>def main()</c> is kept unchanged, or empty when none applies.</summary>
        public string CanonicalMainFunctionFile { get; internal set; } = string.Empty;

        public bool HasAutomatableFixes { get { return Renames.Count > 0 || FunctionRenames.Count > 0; } }
        public bool HasManualFindings { get { return ManualFindings.Count > 0; } }
    }

    /// <summary>Minimal file-system seam so <see cref="SolutionResolutionPlanner.Apply"/> is unit-testable.</summary>
    public interface IWorkspaceFileSystem
    {
        bool FileExists(string path);
        void MoveFile(string oldPath, string newPath);
        string ReadAllText(string path);
        void WriteAllText(string path, string content);
    }

    /// <summary>Real, on-disk implementation of <see cref="IWorkspaceFileSystem"/>.</summary>
    public sealed class PhysicalWorkspaceFileSystem : IWorkspaceFileSystem
    {
        public bool FileExists(string path) { return File.Exists(path); }
        public void MoveFile(string oldPath, string newPath) { File.Move(oldPath, newPath); }
        public string ReadAllText(string path) { return File.ReadAllText(path); }
        public void WriteAllText(string path, string content) { File.WriteAllText(path, content); }
    }

    /// <summary>Outcome of applying a <see cref="SolutionResolutionPlan"/> to disk.</summary>
    public sealed class ResolutionApplyResult
    {
        public ResolutionApplyResult()
        {
            Applied = new List<FileRenameAction>();
            Failed = new List<ApplyFailure>();
            FunctionsApplied = new List<FunctionRenameAction>();
            FunctionsFailed = new List<FunctionApplyFailure>();
        }

        public List<FileRenameAction> Applied { get; }
        public List<ApplyFailure> Failed { get; }
        public List<FunctionRenameAction> FunctionsApplied { get; }
        public List<FunctionApplyFailure> FunctionsFailed { get; }
        public bool AllSucceeded { get { return Failed.Count == 0 && FunctionsFailed.Count == 0; } }
    }

    /// <summary>A rename action that could not be applied, with the reason.</summary>
    public sealed class ApplyFailure
    {
        public ApplyFailure(FileRenameAction action, string reason)
        {
            Action = action;
            Reason = reason ?? string.Empty;
        }

        public FileRenameAction Action { get; }
        public string Reason { get; }
    }

    /// <summary>A function-rename action that could not be applied, with the reason.</summary>
    public sealed class FunctionApplyFailure
    {
        public FunctionApplyFailure(FunctionRenameAction action, string reason)
        {
            Action = action;
            Reason = reason ?? string.Empty;
        }

        public FunctionRenameAction Action { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// Turns a <see cref="SolutionAuditReport"/>'s error findings into a concrete, collision-free
    /// resolution plan and (optionally) applies it. Planning is pure; applying is guarded — it
    /// never overwrites an existing file (requirement #3/#4: "offer a proper resolution").
    /// </summary>
    public static class SolutionResolutionPlanner
    {
        /// <summary>
        /// Build a plan from an audit report. For the single-main-module rule (#3c) it keeps one
        /// canonical entry module and proposes a non-conflicting rename for every other one. For
        /// the duplicate-<c>def main()</c> rule (#3d) it keeps one canonical file's <c>def main()</c>
        /// and proposes renaming the function itself (declaration + same-file call sites, via the
        /// scope-aware <see cref="AstIdentifierRewriter"/>) in every other offending file — a
        /// module-level <c>def main()</c> has no cross-file callers by construction (every other
        /// module in the same violation already declares its OWN <c>main</c>, so nothing outside
        /// the file could be calling this one by that name), which is what makes a same-file-only
        /// rewrite actually safe here.
        /// </summary>
        public static SolutionResolutionPlan Plan(SolutionAuditReport report)
        {
            SolutionResolutionPlan plan = new SolutionResolutionPlan();
            if (report == null) return plan;

            if (report.MainModules != null && report.MainModules.Count > 1)
                PlanMainModuleRenames(report.MainModules, plan);

            if (report.MainFunctionFiles != null && report.MainFunctionFiles.Count > 1)
                PlanMainFunctionRenames(report.MainFunctionFiles, plan);

            // Any other rule this planner doesn't (yet) know how to auto-fix stays manual.
            foreach (SolutionAuditFinding f in report.Findings)
            {
                if (f.Severity != SolutionAuditSeverity.Error) continue;
                if (f.Rule == SolutionAuditRule.SingleMainModule) continue;
                if (f.Rule == SolutionAuditRule.SingleMainFunction) continue;
                plan.ManualFindings.Add(f);
            }

            return plan;
        }

        private static void PlanMainFunctionRenames(List<string> mainFunctionFiles, SolutionResolutionPlan plan)
        {
            // Keep the SAME file canonical on both axes when possible, so one file ends up
            // "main.py with def main()" end to end rather than two different files each
            // keeping one half of the entry point.
            string canonical = !string.IsNullOrEmpty(plan.CanonicalMainModule) &&
                                mainFunctionFiles.Contains(plan.CanonicalMainModule, StringComparer.OrdinalIgnoreCase)
                ? plan.CanonicalMainModule
                : SelectCanonical(mainFunctionFiles);
            plan.CanonicalMainFunctionFile = canonical;

            // Reserve every proposed name so two files in this same pass can't collide with
            // each other; Apply() re-checks against each file's actual content for names
            // already in use there (a plan built from paths alone can't see file content).
            HashSet<string> reservedNames = new HashSet<string>(StringComparer.Ordinal) { "main" };

            foreach (string file in mainFunctionFiles)
            {
                if (string.Equals(file, canonical, StringComparison.OrdinalIgnoreCase)) continue;

                string newName = ProposeNonMainFunctionName(file, reservedNames);
                reservedNames.Add(newName);
                plan.FunctionRenames.Add(new FunctionRenameAction(
                    file, "main", newName, SolutionAuditRule.SingleMainFunction,
                    "Only one module-level def main() is allowed; renamed so " +
                    Path.GetFileName(canonical) + " remains the sole entry point."));
            }
        }

        /// <summary>
        /// Propose a collision-free function name derived from the file's own base name
        /// (e.g. <c>save_pdb.py</c> -&gt; <c>save_pdb_main</c>), matching the audit's own suggested
        /// resolution text ("rename the others e.g. to 'run()' or '&lt;feature&gt;_main()'").
        /// </summary>
        private static string ProposeNonMainFunctionName(string filePath, HashSet<string> reserved)
        {
            string baseName = Sanitize(Path.GetFileNameWithoutExtension(filePath) ?? string.Empty);
            if (baseName.Length == 0) baseName = "module";
            string candidate = baseName + "_main";
            int n = 2;
            while (reserved.Contains(candidate))
            {
                candidate = baseName + "_main_" + n;
                n++;
            }
            return candidate;
        }

        private static void PlanMainModuleRenames(List<string> mainModules, SolutionResolutionPlan plan)
        {
            string canonical = SelectCanonical(mainModules);
            plan.CanonicalMainModule = canonical;

            // Reserve every existing main-module path so a proposed target can never collide with one.
            HashSet<string> reserved = new HashSet<string>(mainModules, StringComparer.OrdinalIgnoreCase);

            foreach (string module in mainModules)
            {
                if (string.Equals(module, canonical, StringComparison.OrdinalIgnoreCase)) continue;

                string target = ProposeNonMainName(module, reserved);
                reserved.Add(target);
                plan.Renames.Add(new FileRenameAction(
                    module, target, SolutionAuditRule.SingleMainModule,
                    "Only one main-equivalent module is allowed; renamed so " +
                    Path.GetFileName(canonical) + " remains the sole entry module."));
            }
        }

        /// <summary>
        /// Prefer a literal <c>main.py</c>, then <c>__main__.py</c> (the package entry Python itself
        /// runs), otherwise the first module in sorted order — deterministic regardless of input order.
        /// </summary>
        private static string SelectCanonical(List<string> mainModules)
        {
            List<string> sorted = new List<string>(mainModules);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);

            string exactMain = sorted.FirstOrDefault(
                p => string.Equals(Path.GetFileName(p), "main.py", StringComparison.OrdinalIgnoreCase));
            if (exactMain != null) return exactMain;

            string dunderMain = sorted.FirstOrDefault(
                p => string.Equals(Path.GetFileName(p), "__main__.py", StringComparison.OrdinalIgnoreCase));
            if (dunderMain != null) return dunderMain;

            return sorted[0];
        }

        /// <summary>
        /// Propose a collision-free file name in the same directory whose base is NOT main-equivalent.
        /// Derives the base from the containing folder (e.g. <c>tools/main.py</c> -&gt; <c>tools_main.py</c>);
        /// appends a numeric suffix if that path is already taken/reserved.
        /// </summary>
        private static string ProposeNonMainName(string module, HashSet<string> reserved)
        {
            string dir = Path.GetDirectoryName(module) ?? string.Empty;
            string ext = Path.GetExtension(module);
            if (string.IsNullOrEmpty(ext)) ext = ".py";

            string folder = Sanitize(Path.GetFileName(dir));
            if (folder.Length == 0) folder = "entry";
            // *_main.py is a canonical-equivalent entry module in SRS version 1.1, so a
            // demoted module must use a neutral suffix.
            string baseName = folder + "_entry";

            string candidate = Path.Combine(dir, baseName + ext);
            int n = 2;
            while (reserved.Contains(candidate))
            {
                candidate = Path.Combine(dir, baseName + "_" + n + ext);
                n++;
            }
            return candidate;
        }

        /// <summary>Reduce a folder name to a safe Python-identifier-ish token.</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string result = sb.ToString().Trim('_');
            if (result.Length > 0 && char.IsDigit(result[0])) result = "m_" + result;
            return result;
        }

        /// <summary>
        /// Apply the automatable renames. Never overwrites: a target that already exists (or a
        /// source that has vanished) is recorded as a failure and skipped, so a partial apply is safe.
        /// </summary>
        public static ResolutionApplyResult Apply(SolutionResolutionPlan plan, IWorkspaceFileSystem fs)
        {
            if (fs == null) throw new ArgumentNullException(nameof(fs));
            ResolutionApplyResult result = new ResolutionApplyResult();
            if (plan == null) return result;

            foreach (FileRenameAction action in plan.Renames)
            {
                try
                {
                    if (!fs.FileExists(action.OldPath))
                    {
                        result.Failed.Add(new ApplyFailure(action, "Source file no longer exists."));
                        continue;
                    }
                    if (fs.FileExists(action.NewPath))
                    {
                        result.Failed.Add(new ApplyFailure(action, "Target file already exists; not overwritten."));
                        continue;
                    }
                    fs.MoveFile(action.OldPath, action.NewPath);
                    result.Applied.Add(action);
                }
                catch (Exception ex)
                {
                    result.Failed.Add(new ApplyFailure(action, ex.Message));
                }
            }

            foreach (FunctionRenameAction action in plan.FunctionRenames)
            {
                try
                {
                    if (!fs.FileExists(action.FilePath))
                    {
                        result.FunctionsFailed.Add(new FunctionApplyFailure(action, "Source file no longer exists."));
                        continue;
                    }
                    string source = fs.ReadAllText(action.FilePath);

                    // The plan proposed a name from the file's own path alone (pure, no I/O);
                    // re-check it against this file's ACTUAL content now that we have it, and
                    // bump a numeric suffix on collision — mirrors the file-rename guard above,
                    // just against identifiers instead of paths.
                    string newName = action.NewName;
                    int n = 2;
                    while (AstIdentifierRewriter.FindOccurrences(source, newName).Count > 0)
                    {
                        newName = action.NewName + "_" + n;
                        n++;
                    }

                    AstIdentifierRewriter.RewriteResult rewritten =
                        AstIdentifierRewriter.Rewrite(source, action.OldName, newName);
                    if (rewritten.Count == 0)
                    {
                        result.FunctionsFailed.Add(new FunctionApplyFailure(action,
                            "Could not locate 'def " + action.OldName + "(' in this file; it may have changed since the audit ran."));
                        continue;
                    }

                    fs.WriteAllText(action.FilePath, rewritten.Text);
                    result.FunctionsApplied.Add(newName == action.NewName
                        ? action
                        : new FunctionRenameAction(action.FilePath, action.OldName, newName, action.Rule, action.Reason));
                }
                catch (Exception ex)
                {
                    result.FunctionsFailed.Add(new FunctionApplyFailure(action, ex.Message));
                }
            }

            return result;
        }
    }
}
