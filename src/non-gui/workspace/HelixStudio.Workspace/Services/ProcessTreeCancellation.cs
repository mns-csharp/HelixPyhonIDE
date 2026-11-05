namespace HelixPythonNamespace.Workspace.Services
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    /// <summary>Bridges cooperative cancellation to a native process tree on .NET Framework.</summary>
    internal static class ProcessTreeCancellation
    {
        public static CancellationTokenRegistration Register(Process process, CancellationToken token)
        {
            if (process == null || !token.CanBeCanceled) return default(CancellationTokenRegistration);
            return token.Register(delegate { Kill(process); });
        }

        private static void Kill(Process process)
        {
            int pid;
            try { if (process.HasExited) return; pid = process.Id; }
            catch { return; }

            bool treeKilled = false;
            try
            {
                var start = new ProcessStartInfo("taskkill", "/T /F /PID " + pid)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process killer = Process.Start(start))
                    if (killer != null)
                    {
                        killer.WaitForExit(3000);
                        treeKilled = killer.HasExited && killer.ExitCode == 0;
                    }
            }
            catch { }
            if (!treeKilled)
                try { if (!process.HasExited) process.Kill(); } catch { }
        }
    }
}
