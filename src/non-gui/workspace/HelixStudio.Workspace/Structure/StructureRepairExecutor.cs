using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>Outcome of a repair run.</summary>
    public sealed class StructureRepairResult
    {
        public bool Succeeded { get; internal set; }
        public bool RolledBack { get; internal set; }
        public string FailureReason { get; internal set; }
        public List<string> Completed { get; } = new List<string>();

        /// <summary>The stage-6 re-verification report. Null when no re-check was supplied.</summary>
        public StructureParityReport VerificationReport { get; internal set; }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Succeeded ? "Structure repair: OK"
                : (RolledBack ? "Structure repair: FAILED, rolled back" : "Structure repair: FAILED"));
            if (!string.IsNullOrEmpty(FailureReason)) sb.Append("  reason: ").AppendLine(FailureReason);
            foreach (string c in Completed) sb.Append("  done: ").AppendLine(c);
            if (VerificationReport != null) sb.Append(VerificationReport.Describe());
            return sb.ToString();
        }
    }

    /// <summary>
    /// Stages 5 and 6 — executes a <see cref="StructureRepairPlan"/> transactionally, then proves
    /// the result.
    ///
    /// Two properties do the real work here:
    ///
    /// <list type="bullet">
    /// <item><b>Journalled, with rollback.</b> Every completed operation is appended to
    /// <c>.helixstudio/structure-repair.journal</c> before the next one starts, and any failure
    /// unwinds the completed ones in reverse. A half-restructured solution is worse than one this
    /// engine never touched: the user would be left with a layout that matches neither what they
    /// had nor what the IDE expects, and no record of which half is which.</item>
    /// <item><b>Re-verified, and rolled back if the re-check is not clean.</b> A non-empty report
    /// after repair is a failure of the ENGINE, not of the user, and it must not be reported as
    /// success. This is the same standard the skill applies to every other claim in this
    /// repository — the oracle runs again at the end, and its answer is the answer.</item>
    /// </list>
    /// </summary>
    public sealed class StructureRepairExecutor
    {
        private const string StateDirectoryName = ".helixstudio";
        private const string JournalName = "structure-repair.journal";
        private const string PreviousJournalName = "structure-repair.journal.previous";
        private const string BackupDirectoryName = "structure-repair-backup";

        /// <summary>Written before an operation runs.</summary>
        internal const string BeginMarker = "BEGIN";

        /// <summary>Written after it completes. A BEGIN with no DONE is an interrupted operation.</summary>
        internal const string DoneMarker = "DONE";

        /// <summary>
        /// The name a directory wears between the two halves of a case-only rename. Deterministic
        /// so that recovery can find it — see <see cref="RecaseDirectory"/>.
        /// </summary>
        internal const string RecaseTempSuffix = ".helix-recase-tmp";

        /// <summary>
        /// Moves an existing journal aside instead of overwriting it.
        ///
        /// The old code opened the journal with <c>File.WriteAllText</c>, which destroyed the
        /// previous run's record as its first act. That record is worth most in exactly the case
        /// where a second run happens: the previous repair failed, its rollback was incomplete, and
        /// the journal is the user's only account of which half of their solution moved. Clobbering
        /// it is the one thing this file must not do.
        /// </summary>
        private static void PreserveEarlierJournal(string journalPath)
        {
            try
            {
                if (!File.Exists(journalPath)) return;
                string previous = Path.Combine(Path.GetDirectoryName(journalPath), PreviousJournalName);
                File.Copy(journalPath, previous, true);
            }
            catch { /* never let bookkeeping stop a repair the user asked for */ }
        }

        /// <summary>
        /// Empties the backup directory before a run.
        ///
        /// Backups are keyed by path, and rollback restores whatever file sits at that key. A
        /// backup left behind by an EARLIER run has the same key, so without this an unwind would
        /// happily restore a manifest from a previous session over the user's newer one — a
        /// rollback that loses work is worse than no rollback.
        /// </summary>
        private static void ResetBackupDirectory(string backupDirectory)
        {
            try
            {
                if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, true);
            }
            catch { /* a stale backup is a risk; failing to start the repair is a certainty */ }
        }

        /// <summary>
        /// Runs the plan. <paramref name="reverify"/> should re-load the solution from the repaired
        /// disk and return a fresh report — it is passed in rather than called directly so this
        /// class stays free of the async solution-loading path and testable without one.
        /// </summary>
        public StructureRepairResult Execute(StructureRepairPlan plan, Func<StructureParityReport> reverify)
        {
            var result = new StructureRepairResult();
            if (plan == null || plan.IsEmpty) { result.Succeeded = true; return result; }

            // Refuse BEFORE touching anything. A plan that already declined to repair a
            // disk-repairable defect cannot pass stage 6, so executing it would move the user's
            // directories and then move every one of them back — real I/O, real risk, and a
            // "repair failed" message, for a collision that was known before the first write.
            if (plan.HasBlockingRefusal)
            {
                result.FailureReason = "the repair was not started: " + plan.BlockingRefusals[0];
                return result;
            }

            string root = plan.SolutionDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                result.FailureReason = "solution directory does not exist: " + root;
                return result;
            }

            string stateDirectory = Path.Combine(root, StateDirectoryName);
            string journalPath = Path.Combine(stateDirectory, JournalName);
            string backupDirectory = Path.Combine(stateDirectory, BackupDirectoryName);
            var completed = new List<StructureRepairOperation>();
            // The operation currently in flight. Cleared on success, so on the way out of the catch
            // it names the one that threw — the one operation that may be half-applied and that the
            // completed-list unwind would otherwise never look at.
            StructureRepairOperation interrupted = null;

            try
            {
                Directory.CreateDirectory(stateDirectory);
                PreserveEarlierJournal(journalPath);
                ResetBackupDirectory(backupDirectory);
                File.WriteAllText(journalPath,
                    "# structure repair for " + root + Environment.NewLine
                    + "# " + BeginMarker + " is written BEFORE the operation runs, " + DoneMarker
                    + " after it completes." + Environment.NewLine
                    + "# An entry with no matching " + DoneMarker + " is an operation that was "
                    + "interrupted; the solution is half-repaired and needs it undone." + Environment.NewLine);

                foreach (StructureRepairOperation op in plan.Operations)
                {
                    if (!op.TouchesDisk) continue;   // RenderNode / DropNode are the tree's business
                    interrupted = op;

                    // WRITE-AHEAD. The journal used to be appended AFTER the operation succeeded,
                    // which makes it a log of things that went right — precisely the entries that
                    // need no recovery. If the process died between the move and the append (a
                    // crash, a kill, the machine losing power), the one operation that WAS applied
                    // and IS unrecorded is the one nothing could ever undo. Recording the intent
                    // first costs one file append and makes the interrupted operation the entry
                    // that stands out.
                    File.AppendAllText(journalPath, BeginMarker + " " + op + Environment.NewLine);
                    Apply(root, backupDirectory, op, plan);
                    completed.Add(op);
                    File.AppendAllText(journalPath, DoneMarker + " " + op + Environment.NewLine);
                    result.Completed.Add(op.ToString());
                    interrupted = null;
                }
            }
            catch (Exception ex)
            {
                result.FailureReason = ex.Message;
                result.RolledBack = TryRollback(root, backupDirectory, completed, interrupted);
                return result;
            }

            if (reverify != null)
            {
                StructureParityReport after = reverify();
                result.VerificationReport = after;
                // Only the defects a DISK repair was supposed to eliminate count against the run.
                // Orphans and phantoms are the renderer's half of the contract and survive a
                // correct repair — see StructureDefect.IsDiskRepairable.
                int outstanding = after == null ? 0 : after.Defects.Count(d => d.IsDiskRepairable);
                if (outstanding > 0)
                {
                    result.FailureReason = "the solution still does not match the tree after repair — "
                        + outstanding + " unrepaired structural defect(s) remain";
                    result.RolledBack = TryRollback(root, backupDirectory, completed, null);
                    return result;
                }
            }

            result.Succeeded = true;
            return result;
        }

        // ---- forward -----------------------------------------------------------------------

        private static void Apply(string root, string backupDirectory, StructureRepairOperation op, StructureRepairPlan plan)
        {
            string source = StructurePath.ToAbsolute(root, op.SourcePath);
            string target = op.TargetPath == null ? null : StructurePath.ToAbsolute(root, op.TargetPath);

            switch (op.Kind)
            {
                case StructureRepairKind.MoveDirectory:
                case StructureRepairKind.RenameDirectory:
                    if (!Directory.Exists(source)) throw new DirectoryNotFoundException("missing source: " + op.SourcePath);
                    if (Directory.Exists(target) || File.Exists(target))
                        throw new IOException("destination already exists: " + op.TargetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    Directory.Move(source, target);
                    break;

                case StructureRepairKind.RecaseDirectory:
                    RecaseDirectory(source, target, op);
                    break;

                case StructureRepairKind.RewriteManifest:
                    BackUp(root, backupDirectory, op.SourcePath);
                    ManifestPathRewriter.Rewrite(source, plan.ProjectMoves, RemovedContainers(plan));
                    break;

                case StructureRepairKind.DeleteEmptyDirectory:
                    // Only ever a directory this plan emptied, and only when it really is empty:
                    // a container that still holds something was never this engine's to delete.
                    if (Directory.Exists(source)
                        && Directory.GetFileSystemEntries(source).Length == 0)
                        Directory.Delete(source);
                    break;
            }
        }

        /// <summary>
        /// A case-only rename through a temporary name.
        ///
        /// On NTFS <c>Directory.Move("Foo", "foo")</c> is either a no-op or an error, because the
        /// two names are the same path as far as the filesystem is concerned. Going via a name that
        /// differs by more than case is the only portable way to make the change stick.
        /// </summary>
        private static void RecaseDirectory(string source, string target, StructureRepairOperation op)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("missing source: " + op.SourcePath);

            // DETERMINISTIC, not a GUID. This is the only operation in the plan that is not atomic:
            // between the two moves the user's directory exists under neither its old name nor its
            // new one. If the process dies in that window, recovery has to be able to FIND it — and
            // a name containing a random GUID is a name nobody can predict. The cost is that a
            // leftover from a previous crash collides, which is exactly the case that should stop
            // and be looked at rather than be silently overwritten.
            string temporary = target + RecaseTempSuffix;
            if (Directory.Exists(temporary) || File.Exists(temporary))
                throw new IOException("a previous rename was interrupted and left '" + temporary
                    + "'. Move it back by hand before repairing again.");

            Directory.Move(source, temporary);
            try { Directory.Move(temporary, target); }
            catch { Directory.Move(temporary, source); throw; }
        }

        private static IEnumerable<string> RemovedContainers(StructureRepairPlan plan)
        {
            return plan.Operations
                .Where(o => o.Kind == StructureRepairKind.DeleteEmptyDirectory)
                .Select(o => o.SourcePath);
        }

        // ---- backup / rollback --------------------------------------------------------------

        private static void BackUp(string root, string backupDirectory, string relativeFile)
        {
            string source = StructurePath.ToAbsolute(root, relativeFile);
            if (!File.Exists(source)) return;
            string target = Path.Combine(backupDirectory, StructurePath.Normalize(relativeFile).Replace('/', '_'));
            Directory.CreateDirectory(backupDirectory);
            File.Copy(source, target, true);
        }

        /// <summary>
        /// Unwinds completed operations in reverse. Returns false if any step of the unwind itself
        /// fails — in which case the caller must say so rather than reporting a clean rollback, so
        /// the journal on disk is the user's record of what actually happened.
        /// </summary>
        private static bool TryRollback(
            string root, string backupDirectory, List<StructureRepairOperation> completed,
            StructureRepairOperation interrupted)
        {
            // The half-applied operation first, then the completed ones in reverse. Undoing a
            // completed move while a partially-applied one still sits at a temporary name would
            // unwind onto a path that is not yet settled.
            var unwind = new List<StructureRepairOperation>();
            if (interrupted != null) unwind.Add(interrupted);
            for (int i = completed.Count - 1; i >= 0; i--) unwind.Add(completed[i]);

            bool clean = true;
            foreach (StructureRepairOperation op in unwind)
            {
                try
                {
                    string source = StructurePath.ToAbsolute(root, op.SourcePath);
                    string target = op.TargetPath == null ? null : StructurePath.ToAbsolute(root, op.TargetPath);
                    switch (op.Kind)
                    {
                        case StructureRepairKind.MoveDirectory:
                        case StructureRepairKind.RenameDirectory:
                            if (Directory.Exists(target))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(source));
                                Directory.Move(target, source);
                            }
                            break;
                        // Undoing a case-only rename is itself a case-only rename, so it needs the
                        // same two-step. A plain Directory.Move here throws, the unwind reports
                        // "rollback incomplete", and the user is told a clean rollback failed when
                        // in fact nothing had gone wrong but the undo.
                        case StructureRepairKind.RecaseDirectory:
                            if (Directory.Exists(target)) { RecaseDirectory(target, source, op); break; }
                            // Interrupted between the two halves of the two-step: the directory is
                            // sitting under the temporary name with nothing else pointing at it.
                            // Without this it is stranded, under a name the user never chose, and
                            // the solution has silently lost a package.
                            string stranded = target + RecaseTempSuffix;
                            if (Directory.Exists(stranded)) Directory.Move(stranded, source);
                            break;
                        case StructureRepairKind.RewriteManifest:
                            string backup = Path.Combine(backupDirectory,
                                StructurePath.Normalize(op.SourcePath).Replace('/', '_'));
                            if (File.Exists(backup)) File.Copy(backup, source, true);
                            break;
                        case StructureRepairKind.DeleteEmptyDirectory:
                            if (!Directory.Exists(source)) Directory.CreateDirectory(source);
                            break;
                    }
                }
                catch { clean = false; }
            }
            return clean;
        }
    }
}
