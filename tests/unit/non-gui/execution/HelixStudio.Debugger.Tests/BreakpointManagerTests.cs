namespace HelixPythonNamespace.Debugger.Tests
{
    using System.IO;
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Debugger;

    [TestClass]
    public class BreakpointManagerTests
    {
        [TestMethod]
        public void ExplicitStateDirectoryNeverUsesUserProfile()
        {
            string state = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "helix-state-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var manager = new BreakpointManager(state);
                Assert.AreEqual(System.IO.Path.Combine(System.IO.Path.GetFullPath(state), "breakpoints.txt"), manager.SettingsPath);
                manager.Save();
                Assert.IsTrue(System.IO.File.Exists(manager.SettingsPath));
            }
            finally
            {
                if (System.IO.Directory.Exists(state)) System.IO.Directory.Delete(state, true);
            }
        }
        private const string File1 = @"C:\proj\a.py";

        [TestMethod]
        public void NewBreakpoint_IsEnabledByDefault()
        {
            var m = new BreakpointManager();
            m.AddBreakpoint(File1, 10);
            Assert.IsTrue(m.IsEnabled(File1, 10), "a fresh breakpoint is enabled");
            CollectionAssert.AreEquivalent(new[] { 10 }, m.GetEnabledBreakpoints(File1).ToList());
        }

        [TestMethod]
        public void Disable_KeepsInListButExcludesFromEnabled()
        {
            var m = new BreakpointManager();
            m.AddBreakpoint(File1, 10);
            m.AddBreakpoint(File1, 20);

            m.SetEnabled(File1, 10, false);

            // Still present for the panel/editor...
            CollectionAssert.AreEquivalent(new[] { 10, 20 }, m.GetBreakpoints(File1).ToList());
            Assert.IsFalse(m.IsEnabled(File1, 10), "disabled breakpoint reports not-enabled");
            // ...but excluded from what the adapter is told to stop on.
            CollectionAssert.AreEquivalent(new[] { 20 }, m.GetEnabledBreakpoints(File1).ToList());
        }

        [TestMethod]
        public void ToggleEnabled_FlipsAndReturnsNewState()
        {
            var m = new BreakpointManager();
            m.AddBreakpoint(File1, 5);
            Assert.IsFalse(m.ToggleEnabled(File1, 5), "first toggle disables → returns false");
            Assert.IsFalse(m.IsEnabled(File1, 5));
            Assert.IsTrue(m.ToggleEnabled(File1, 5), "second toggle re-enables → returns true");
            Assert.IsTrue(m.IsEnabled(File1, 5));
        }

        [TestMethod]
        public void SetEnabled_OnMissingBreakpoint_IsNoOp()
        {
            var m = new BreakpointManager();
            m.SetEnabled(File1, 99, false);         // no breakpoint there
            Assert.IsTrue(m.IsEnabled(File1, 99), "nothing to disable → still reported enabled");
            Assert.AreEqual(0, m.GetBreakpoints(File1).Count);
        }

        [TestMethod]
        public void Remove_ClearsDisabledState()
        {
            var m = new BreakpointManager();
            m.AddBreakpoint(File1, 10);
            m.SetEnabled(File1, 10, false);
            m.RemoveBreakpoint(File1, 10);
            // Re-adding starts enabled again (stale disabled-state must not linger).
            m.AddBreakpoint(File1, 10);
            Assert.IsTrue(m.IsEnabled(File1, 10), "disabled-state cleared on remove");
        }

        [TestMethod]
        public void SetLogMessage_MakesTracepoint_AndClearWorks()
        {
            var m = new BreakpointManager();
            m.AddBreakpoint(File1, 7);
            Assert.IsFalse(m.GetCondition(File1, 7)?.IsTracepoint ?? false, "plain breakpoint is not a tracepoint");

            m.SetLogMessage(File1, 7, "i={i}");
            var c = m.GetCondition(File1, 7);
            Assert.IsNotNull(c);
            Assert.IsTrue(c.IsTracepoint, "log message → tracepoint");
            Assert.AreEqual("i={i}", c.LogMessage);
            // The breakpoint still exists (a tracepoint is a kind of breakpoint).
            Assert.IsTrue(m.HasBreakpoint(File1, 7));

            m.SetLogMessage(File1, 7, "   ");   // blank clears it
            var c2 = m.GetCondition(File1, 7);
            Assert.IsTrue(c2 == null || !c2.IsTracepoint, "blank log message clears the tracepoint");
        }

        [TestMethod]
        public void SetCondition_PreservesExistingLogMessage()
        {
            var m = new BreakpointManager();
            m.SetLogMessage(File1, 5, "hit {x}");
            m.SetCondition(File1, 5, "x > 0", null);  // editing the condition must not drop the log message
            var c = m.GetCondition(File1, 5);
            Assert.AreEqual("x > 0", c.Condition);
            Assert.AreEqual("hit {x}", c.LogMessage, "log message survived a condition edit");
        }

        [TestMethod]
        public void SaveLoad_RoundTripsLogMessage()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "bp_log_" + System.Guid.NewGuid().ToString("N") + ".json");
            string realFile = Path.Combine(Path.GetTempPath(), "bp_logtarget_" + System.Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(realFile, "a = 1\nb = 2\n");
            try
            {
                var m = new BreakpointManager();
                m.SetLogMessage(realFile, 1, "reached line 1, a={a}");
                m.Save(tmp);
                var m2 = new BreakpointManager();
                m2.Load(tmp);
                var c = m2.GetCondition(realFile, 1);
                Assert.IsNotNull(c);
                Assert.AreEqual("reached line 1, a={a}", c.LogMessage, "log message survived save/load");
                Assert.IsTrue(c.IsTracepoint);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
                try { File.Delete(realFile); } catch { }
            }
        }

        [TestMethod]
        public void SaveLoad_RoundTripsDisabledState()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "bp_disabled_" + System.Guid.NewGuid().ToString("N") + ".json");
            // Use a real file so Load (which drops entries for non-existent files) keeps them.
            string realFile = Path.Combine(Path.GetTempPath(), "bp_target_" + System.Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(realFile, "x = 1\ny = 2\nz = 3\n");
            try
            {
                var m = new BreakpointManager();
                m.AddBreakpoint(realFile, 1);
                m.AddBreakpoint(realFile, 2);
                m.SetEnabled(realFile, 2, false);
                m.Save(tmp);

                var m2 = new BreakpointManager();
                m2.Load(tmp);
                Assert.IsTrue(m2.IsEnabled(realFile, 1), "line 1 was enabled");
                Assert.IsFalse(m2.IsEnabled(realFile, 2), "line 2 disabled-state survived save/load");
                CollectionAssert.AreEquivalent(new[] { 1 }, m2.GetEnabledBreakpoints(realFile).ToList());
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
                try { File.Delete(realFile); } catch { }
            }
        }
    }
}
