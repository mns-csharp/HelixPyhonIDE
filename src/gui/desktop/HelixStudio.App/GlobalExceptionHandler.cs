namespace HelixPythonNamespace.App
{
    using System;
    using System.IO;
    using System.Text;
    using System.Windows.Forms;

    /// <summary>
    /// Last-resort safety net so an unhandled exception surfaces a friendly, logged dialog instead
    /// of the raw .NET crash box (or a silent process death on a background thread). UI-thread
    /// exceptions are recoverable — the message loop keeps running — while a background-thread
    /// exception is terminal and can only be logged and reported before the process exits.
    /// </summary>
    internal static class GlobalExceptionHandler
    {
        /// <summary>
        /// Optional best-effort "save my work" hook, run before the crash dialog so a UI-thread
        /// fault doesn't lose unsaved edits. The host wires this to flush modified editor tabs; it
        /// must be self-guarding (a background-thread fault can't safely touch UI state).
        /// </summary>
        public static Action EmergencySave { get; set; }

        /// <summary>Invoke <see cref="EmergencySave"/>, swallowing any failure. Extracted for testing.</summary>
        internal static void RunEmergencySave()
        {
            try { EmergencySave?.Invoke(); } catch { /* never let the save hook worsen the crash */ }
        }

        /// <summary>Wire the WinForms + AppDomain handlers. Call once, before Application.Run.</summary>
        public static void Install()
        {
            // Route UI-thread exceptions to ThreadException (recoverable) rather than crashing.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Handle(e.Exception, terminating: false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Handle(e.ExceptionObject as Exception, terminating: e.IsTerminating);
        }

        private static void Handle(Exception ex, bool terminating)
        {
            // The handler must never throw — a fault here would re-enter or kill the process outright.
            try
            {
                RunEmergencySave();   // flush unsaved work first, before anything else can fail
                string report = BuildReport(ex);
                string logPath = TryWriteLog(report);
                ShowDialog(ex, report, logPath, terminating);
            }
            catch { /* nothing safe left to do */ }
        }

        /// <summary>
        /// Human-readable report of an exception and its full inner-exception chain. Pure (no I/O),
        /// so it is unit-testable. Returns a generic line for a null exception.
        /// </summary>
        public static string BuildReport(Exception ex)
        {
            if (ex == null) return "Unhandled non-CLR exception (no managed exception object).";

            var sb = new StringBuilder();
            int depth = 0;
            for (Exception cur = ex; cur != null; cur = cur.InnerException, depth++)
            {
                if (depth > 0) sb.AppendLine().Append("--- inner exception ---").AppendLine();
                sb.Append(cur.GetType().FullName).Append(": ").Append(cur.Message).AppendLine();
                if (!string.IsNullOrEmpty(cur.StackTrace)) sb.Append(cur.StackTrace).AppendLine();
                if (depth > 16) { sb.Append("… (inner-exception chain truncated)"); break; }
            }
            return sb.ToString().TrimEnd();
        }

        // Write the report under %LOCALAPPDATA%\HelixStudio\logs\crash-<utc>.log; returns the path, or
        // null if even logging failed (the dialog then just omits the "log written to …" line).
        private static string TryWriteLog(string report)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HelixPython", "logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crash-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(path,
                    "HelixStudio crash report — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    Environment.NewLine + report + Environment.NewLine);
                PruneOldCrashLogs(dir, keep: 20);
                return path;
            }
            catch { return null; }
        }

        /// <summary>
        /// Keep only the newest <paramref name="keep"/> crash logs in <paramref name="dir"/> so they
        /// can't grow without bound. Timestamped names sort chronologically, so the lexicographically
        /// largest are the newest. Best-effort — a delete failure never propagates.
        /// </summary>
        public static void PruneOldCrashLogs(string dir, int keep)
        {
            try
            {
                if (keep < 0) keep = 0;
                string[] logs = Directory.GetFiles(dir, "crash-*.log");
                if (logs.Length <= keep) return;
                Array.Sort(logs, StringComparer.Ordinal);            // oldest first
                int toDelete = logs.Length - keep;
                for (int i = 0; i < toDelete; i++)
                {
                    try { File.Delete(logs[i]); } catch { /* skip a locked/removed file */ }
                }
            }
            catch { /* pruning is never allowed to fail the crash report */ }
        }

        private static void ShowDialog(Exception ex, string report, string logPath, bool terminating)
        {
            // Put the full report on the clipboard so the user can paste it straight into a bug report.
            // Best-effort: Clipboard needs an STA thread and can fail from a background-thread fault.
            bool copied = false;
            try { if (!string.IsNullOrEmpty(report)) { Clipboard.SetText(report); copied = true; } }
            catch { copied = false; }

            string headline = ex != null ? ex.Message : "An unexpected error occurred.";
            var sb = new StringBuilder();
            sb.Append("An unexpected error occurred:").AppendLine().AppendLine().Append(headline).AppendLine();
            if (copied)
                sb.AppendLine().Append("The error details have been copied to your clipboard.").AppendLine();
            if (!string.IsNullOrEmpty(logPath))
                sb.AppendLine().Append("A diagnostic log was written to:").AppendLine().Append(logPath).AppendLine();
            sb.AppendLine().Append(terminating
                ? "The application must close. Please restart it."
                : "You can continue working, but saving and restarting is recommended.");

            // Offer to open the log directly when there is one (Yes/No), otherwise a plain OK.
            try
            {
                bool haveLog = !string.IsNullOrEmpty(logPath);
                if (haveLog)
                    sb.AppendLine().Append("Open the diagnostic log now?");

                DialogResult r = MessageBox.Show(sb.ToString(), "HelixStudio — Unexpected Error",
                    haveLog ? MessageBoxButtons.YesNo : MessageBoxButtons.OK, MessageBoxIcon.Error);

                if (haveLog && r == DialogResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(logPath); }
                    catch { /* a missing file association shouldn't compound the crash */ }
                }
            }
            catch { /* no UI available */ }
        }
    }
}
