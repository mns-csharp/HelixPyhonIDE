using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.App.Services
{
    /// <summary>
    /// Renders a <see cref="SolutionAuditReport"/> (requirement #3 rules) to the user and,
    /// for a non-compliant project, asks whether to proceed. UI is a standard warning
    /// <see cref="MessageBox"/> — consistent with the rest of the IDE's confirm dialogs.
    /// The offending paths and each rule's suggested resolution are shown inline so the user
    /// can fix the source (#4b) before/after opening. When the violation is auto-resolvable
    /// (duplicate main-equivalent module, #3c) the dialog offers a one-click fix that renames
    /// the surplus modules via <see cref="SolutionResolutionPlanner"/>.
    /// </summary>
    internal static class SolutionAuditPresenter
    {
        /// <summary>
        /// If <paramref name="report"/> has any Error finding, show them with resolutions and
        /// ask the user how to continue. Returns true when the caller should proceed to open
        /// (compliant report, null report, "open anyway", or after auto-fixes are applied);
        /// false when the user cancels. Auto-applyable renames (#3c) are offered as a one-click
        /// fix; a duplicate <c>def main()</c> (#3d) stays advisory since fixing it edits user code.
        /// </summary>
        public static bool ConfirmOrBlock(IWin32Window owner, SolutionAuditReport report, string context)
        {
            if (report == null || report.IsCompliant) return true;

            // Non-interactive host (test harness / CI / headless): never show a modal that would block
            // a thread with no user to click it. Proceed as "open without changing anything".
            if (string.Equals(System.Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1", System.StringComparison.Ordinal))
                return true;

            SolutionResolutionPlan plan = SolutionResolutionPlanner.Plan(report);
            string title = context + " — Compliance Issues";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("This Python project does not comply with the IDE's solution-management rules:");
            sb.AppendLine();
            foreach (SolutionAuditFinding f in report.Findings)
            {
                if (f.Severity != SolutionAuditSeverity.Error) continue;
                sb.AppendLine("• " + f.Message);
                for (int i = 0; i < f.Paths.Count; i++)
                    sb.AppendLine("      " + f.Paths[i]);
                if (!string.IsNullOrEmpty(f.SuggestedResolution))
                    sb.AppendLine("   Resolution: " + f.SuggestedResolution);
                sb.AppendLine();
            }

            if (plan.HasAutomatableFixes)
            {
                if (plan.Renames.Count > 0)
                {
                    sb.AppendLine("The IDE can resolve the duplicate entry module automatically by renaming:");
                    foreach (FileRenameAction a in plan.Renames)
                        sb.AppendLine("      " + Path.GetFileName(a.OldPath) + "  →  " + Path.GetFileName(a.NewPath) +
                                      "      (" + (Path.GetDirectoryName(a.OldPath) ?? string.Empty) + ")");
                    sb.AppendLine();
                }
                if (plan.FunctionRenames.Count > 0)
                {
                    sb.AppendLine("The IDE can resolve the duplicate entry point automatically by renaming:");
                    foreach (FunctionRenameAction a in plan.FunctionRenames)
                        sb.AppendLine("      def " + a.OldName + "()  →  def " + a.NewName + "()" +
                                      "      (" + Path.GetFileName(a.FilePath) + ")");
                    sb.AppendLine();
                }
                sb.AppendLine("[Yes] Apply these fixes and open      [No] Open unchanged      [Cancel] Don't open");

                DialogResult choice = MessageBox.Show(owner, sb.ToString(), title,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

                if (choice == DialogResult.Cancel) return false;
                if (choice == DialogResult.No) return true; // open without changing anything

                ResolutionApplyResult applied =
                    SolutionResolutionPlanner.Apply(plan, new PhysicalWorkspaceFileSystem());
                ShowApplyOutcome(owner, applied, title);
                return true; // proceed to open; the loader re-scans the (now-renamed) files
            }

            // Nothing auto-applyable (e.g. only a duplicate def main) — advisory proceed/cancel.
            sb.Append("Open the project anyway?");
            DialogResult r = MessageBox.Show(owner, sb.ToString(), title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return r == DialogResult.Yes;
        }

        /// <summary>
        /// The report as Problems-panel rows: one per offending file, so each violation is a row a
        /// user can read, sort and double-click to open — the same treatment every other diagnostic
        /// in the IDE gets.
        ///
        /// Before iter 353 the findings only ever existed inside a modal. Dismiss it and the detail
        /// was gone; all that was left was a status line reading "Loaded with compliance warnings —
        /// see solution-management rules", which named no rule, no file, and no place to look.
        ///
        /// The suggested resolution rides along in the message, because a diagnostic a user cannot
        /// act on is only half a diagnostic.
        ///
        /// Each row's message is written for the ONE file the row names, not the aggregate. The
        /// audit reports a single finding ("...a module-level 'def main()' in 10 files...") with all
        /// ten paths; fanning that finding out verbatim (iter 353) put the same "in 10 files"
        /// sentence on every single-file row, so each row read as if it were about all ten. The
        /// aggregate count is kept as context, but the row leads with what is wrong with THIS file.
        /// </summary>
        public static IReadOnlyList<CompileDiagnostic> ToDiagnostics(SolutionAuditReport report)
        {
            var rows = new List<CompileDiagnostic>();
            if (report == null) return rows;

            foreach (SolutionAuditFinding f in report.Findings)
            {
                // A passing rule reports Info with no paths; only violations belong in the list.
                if (f.Severity == SolutionAuditSeverity.Info) continue;

                ParseSeverity severity = f.Severity == SolutionAuditSeverity.Error
                    ? ParseSeverity.Error
                    : ParseSeverity.Warning;
                string code = CodeFor(f.Rule);
                var span = new SourceSpan(SourcePosition.Zero, SourcePosition.Zero);

                // No files to blame (e.g. a "directory does not exist" finding): keep the finding's
                // own aggregate message, since there is no per-file angle to take.
                if (f.Paths.Count == 0)
                {
                    rows.Add(new CompileDiagnostic(string.Empty, code, severity, span,
                        WithResolution(f.Message, f.SuggestedResolution)));
                    continue;
                }
                foreach (string path in f.Paths)
                {
                    string perFile = WithResolution(
                        PerFileMessage(f.Rule, f.Paths.Count), f.SuggestedResolution);
                    rows.Add(new CompileDiagnostic(path, code, severity, span, perFile));
                }
            }
            return rows;
        }

        /// <summary>The lead sentence for one offending file, given its rule and the total count.</summary>
        private static string PerFileMessage(SolutionAuditRule rule, int total)
        {
            switch (rule)
            {
                case SolutionAuditRule.SingleMainModule:
                    return "Main-equivalent entry module — a solution may contain only one (" +
                           total + " found).";
                case SolutionAuditRule.SingleMainFunction:
                    return "Declares a module-level 'def main()' — a solution may declare only one " +
                           "entry point (" + total + " found).";
                default:
                    return "Violates a solution-management rule (" + total + " file(s) affected).";
            }
        }

        private static string WithResolution(string message, string resolution)
        {
            return string.IsNullOrEmpty(resolution) ? message : message + "  Resolution: " + resolution;
        }

        /// <summary>
        /// How many distinct rules the report actually failed — the honest "issue" count, as opposed
        /// to the number of files those issues touch. Ten files sharing one broken rule is one issue.
        /// </summary>
        public static int CountViolatedRules(SolutionAuditReport report)
        {
            if (report == null) return 0;
            int n = 0;
            foreach (SolutionAuditFinding f in report.Findings)
                if (f.Severity == SolutionAuditSeverity.Error) n++;
            return n;
        }

        /// <summary>Stable diagnostic codes for the solution-management rules.</summary>
        private static string CodeFor(SolutionAuditRule rule)
        {
            switch (rule)
            {
                case SolutionAuditRule.MainModuleEquivalence: return "SLN001";
                case SolutionAuditRule.SingleMainModule: return "SLN002";
                case SolutionAuditRule.SingleMainFunction: return "SLN003";
                default: return "SLN000";
            }
        }

        /// <summary>
        /// Import-time warning that Run/Debug will refuse this project, because
        /// <c>ComputeRunPreflight</c> gates on <see cref="PackageComplianceValidator"/> —
        /// a DIFFERENT, stricter rule set (PSM-06 through PSM-24) than the
        /// <see cref="SolutionAuditReport"/> <see cref="ConfirmOrBlock"/> checks. Before
        /// this existed, a project could finish import with no warning at all and only
        /// discover the block later by clicking Run — this closes that gap. Purely
        /// informational (no return value): these findings describe PRE-EXISTING source
        /// (usually several standalone scripts each declaring their own entry point,
        /// e.g. a debug-tools directory), not something the wizard wrote or can safely
        /// rewrite — renaming a script's own <c>def main()</c>/guard would change how
        /// that script behaves when run standalone, which is out of scope for an import
        /// tool that reorganizes files rather than rewriting third-party logic.
        /// </summary>
        public static void WarnRunWillBeBlocked(
            IWin32Window owner, IReadOnlyList<PackageComplianceFinding> findings, string context)
        {
            if (findings == null || findings.Count == 0) return;

            // Same guard as ConfirmOrBlock: never show a modal a headless/CI caller has
            // nobody to dismiss.
            if (string.Equals(System.Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1", System.StringComparison.Ordinal))
                return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(context + " succeeded, but the imported source itself violates " +
                findings.Count + " of the IDE's project-entry-point rule(s). Run/Debug will refuse " +
                "to start this project until these are fixed:");
            sb.AppendLine();
            foreach (PackageComplianceFinding f in findings)
                sb.AppendLine("• " + f);
            sb.AppendLine();
            sb.AppendLine("These are pre-existing issues in the imported source, not something this import " +
                "changed or can safely fix automatically — most often several standalone scripts each " +
                "declaring their own entry point. The project is open and browsable; Run/Debug will show " +
                "the same list until the source is corrected.");

            MessageBox.Show(owner, sb.ToString(), context + " — Run Will Be Blocked",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Summarise what the one-click auto-fix actually changed on disk.</summary>
        private static void ShowApplyOutcome(IWin32Window owner, ResolutionApplyResult applied, string title)
        {
            StringBuilder sb = new StringBuilder();
            if (applied.Applied.Count > 0)
            {
                sb.AppendLine("Renamed " + applied.Applied.Count + " module(s):");
                foreach (FileRenameAction a in applied.Applied)
                    sb.AppendLine("      " + Path.GetFileName(a.OldPath) + "  →  " + Path.GetFileName(a.NewPath));
            }
            if (applied.Failed.Count > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("Could not rename " + applied.Failed.Count + " module(s):");
                foreach (ApplyFailure fail in applied.Failed)
                    sb.AppendLine("      " + Path.GetFileName(fail.Action.OldPath) + " — " + fail.Reason);
            }
            if (applied.FunctionsApplied.Count > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("Renamed " + applied.FunctionsApplied.Count + " function(s):");
                foreach (FunctionRenameAction a in applied.FunctionsApplied)
                    sb.AppendLine("      def " + a.OldName + "() → def " + a.NewName + "()  (" + Path.GetFileName(a.FilePath) + ")");
            }
            if (applied.FunctionsFailed.Count > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("Could not rename " + applied.FunctionsFailed.Count + " function(s):");
                foreach (FunctionApplyFailure fail in applied.FunctionsFailed)
                    sb.AppendLine("      " + Path.GetFileName(fail.Action.FilePath) + " — " + fail.Reason);
            }
            if (sb.Length == 0) return;

            MessageBox.Show(owner, sb.ToString(), title,
                MessageBoxButtons.OK,
                applied.AllSucceeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
