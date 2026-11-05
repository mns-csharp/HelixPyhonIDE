using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 227: the app now installs a global crash handler (Application.ThreadException +
    /// AppDomain.UnhandledException) that logs and shows a friendly dialog instead of the raw .NET
    /// crash box. This covers the pure report builder — the full inner-exception chain must be
    /// captured, and a null (non-CLR) exception must not throw.
    /// </summary>
    [TestClass]
    public class GlobalExceptionHandlerTests
    {
        private static MethodInfo BuildReport()
        {
            Type t = typeof(MainForm).Assembly.GetType("HelixPythonNamespace.App.GlobalExceptionHandler");
            Assert.IsNotNull(t, "GlobalExceptionHandler type expected in the App assembly.");
            MethodInfo m = t.GetMethod("BuildReport", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(m, "BuildReport(Exception) expected.");
            return m;
        }

        [TestMethod]
        public void BuildReport_IncludesFullInnerExceptionChain()
        {
            var ex = new InvalidOperationException("outer boom",
                        new ArgumentException("inner boom"));
            string report = (string)BuildReport().Invoke(null, new object[] { ex });

            StringAssert.Contains(report, "InvalidOperationException");
            StringAssert.Contains(report, "outer boom");
            StringAssert.Contains(report, "inner exception");
            StringAssert.Contains(report, "ArgumentException");
            StringAssert.Contains(report, "inner boom");
        }

        [TestMethod]
        public void BuildReport_NullException_DoesNotThrow_AndIsNonEmpty()
        {
            string report = (string)BuildReport().Invoke(null, new object[] { null });
            Assert.IsFalse(string.IsNullOrWhiteSpace(report),
                "A null (non-CLR) exception must still produce a report line, not crash the handler.");
        }

        [TestMethod]
        public void RunEmergencySave_InvokesHook_AndSwallowsFailures()
        {
            System.Type t = typeof(MainForm).Assembly.GetType("HelixPythonNamespace.App.GlobalExceptionHandler");
            PropertyInfo hook = t.GetProperty("EmergencySave", BindingFlags.Public | BindingFlags.Static);
            MethodInfo run = t.GetMethod("RunEmergencySave", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(hook); Assert.IsNotNull(run);
            object original = hook.GetValue(null);
            try
            {
                int calls = 0;
                hook.SetValue(null, (System.Action)(() => calls++));
                run.Invoke(null, null);
                Assert.AreEqual(1, calls, "EmergencySave hook must be invoked.");

                // A throwing hook must not escape (the crash handler continues regardless).
                hook.SetValue(null, (System.Action)(() => { throw new System.InvalidOperationException("boom"); }));
                run.Invoke(null, null); // must not throw

                // Null hook is a no-op.
                hook.SetValue(null, null);
                run.Invoke(null, null);
            }
            finally { hook.SetValue(null, original); }
        }

        [TestMethod]
        public void PruneOldCrashLogs_KeepsOnlyTheNewestN()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix_crashlog_test_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                // 25 timestamped logs (names sort chronologically) + a non-matching file to ignore.
                DateTime first = DateTime.UtcNow.Date.AddDays(-25);
                for (int i = 1; i <= 25; i++)
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir,
                        "crash-" + first.AddDays(i - 1).ToString("yyyyMMdd") + "-000000.log"), "x");
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "keep-me.txt"), "y");

                System.Type t = typeof(MainForm).Assembly.GetType("HelixPythonNamespace.App.GlobalExceptionHandler");
                MethodInfo prune = t.GetMethod("PruneOldCrashLogs", BindingFlags.Public | BindingFlags.Static);
                Assert.IsNotNull(prune, "PruneOldCrashLogs expected.");
                prune.Invoke(null, new object[] { dir, 20 });

                string[] remaining = System.IO.Directory.GetFiles(dir, "crash-*.log");
                Assert.AreEqual(20, remaining.Length, "Only the newest 20 crash logs should remain.");
                // The newest (crash-20260125-...) must survive; the oldest (crash-20260101-...) must go.
                Assert.IsTrue(System.IO.File.Exists(System.IO.Path.Combine(dir,
                    "crash-" + first.AddDays(24).ToString("yyyyMMdd") + "-000000.log")));
                Assert.IsFalse(System.IO.File.Exists(System.IO.Path.Combine(dir,
                    "crash-" + first.ToString("yyyyMMdd") + "-000000.log")));
                Assert.IsTrue(System.IO.File.Exists(System.IO.Path.Combine(dir, "keep-me.txt")),
                    "Non-crash files must not be touched.");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }
    }
}
