using System;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// FR-X-01: the interactive-terminal engine hosts a real shell and pipes stdin/stdout, so the
    /// terminal is exercisable headlessly (no WinForms) — the NFR-03 contract the panel sits on.
    /// </summary>
    [TestClass]
    public class TerminalSessionTests
    {
        private static string WaitForOutput(TerminalSession s, string token, int timeoutMs)
        {
            var sb = new StringBuilder();
            var seen = new ManualResetEventSlim(false);
            s.OutputReceived += chunk =>
            {
                lock (sb)
                {
                    sb.Append(chunk);
                    if (sb.ToString().IndexOf(token, StringComparison.Ordinal) >= 0) seen.Set();
                }
            };
            s.Start();
            s.SendInput("echo " + token);
            seen.Wait(timeoutMs);
            lock (sb) { return sb.ToString(); }
        }

        [TestMethod]
        public void Echo_RoundTripsThroughTheShell()
        {
            using (var s = new TerminalSession())
            {
                string outp = WaitForOutput(s, "TERMINAL_OK_4711", 8000);
                StringAssert.Contains(outp, "TERMINAL_OK_4711",
                    "the shell should echo the command's output back through OutputReceived");
                s.Close();
            }
        }

        [TestMethod]
        public void IsRunning_TrueAfterStart_FalseAfterClose()
        {
            var s = new TerminalSession();
            s.Start();
            Assert.IsTrue(s.IsRunning, "session should be running right after Start");
            s.Close();
            // Close writes 'exit' and waits; the shell should be gone.
            Assert.IsFalse(s.IsRunning, "session should not be running after Close");
            s.Dispose();
        }

        [TestMethod]
        public void SendInput_AfterDispose_DoesNotThrow()
        {
            var s = new TerminalSession();
            s.Start();
            s.Dispose();
            s.SendInput("echo should-be-a-no-op");  // must be a safe no-op, not a crash
        }

        [TestMethod]
        public void MultipleCommands_AllProduceOutput()
        {
            using (var s = new TerminalSession())
            {
                var sb = new StringBuilder();
                var both = new ManualResetEventSlim(false);
                s.OutputReceived += chunk =>
                {
                    lock (sb)
                    {
                        sb.Append(chunk);
                        string t = sb.ToString();
                        if (t.Contains("FIRST_9001") && t.Contains("SECOND_9002")) both.Set();
                    }
                };
                s.Start();
                s.SendInput("echo FIRST_9001");
                s.SendInput("echo SECOND_9002");
                both.Wait(8000);
                lock (sb)
                {
                    StringAssert.Contains(sb.ToString(), "FIRST_9001");
                    StringAssert.Contains(sb.ToString(), "SECOND_9002");
                }
                s.Close();
            }
        }
    }
}
