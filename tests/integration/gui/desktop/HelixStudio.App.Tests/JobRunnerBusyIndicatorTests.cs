using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 271: the status bar now shows a marquee busy indicator for the duration of a job so the
    /// user can see the app is working (not frozen) during compile/analyze/load. Guards that the
    /// JobRunner shows the indicator when a job begins and hides it when it ends, matching the Cancel
    /// button it sits next to.
    /// </summary>
    [TestClass]
    public class JobRunnerBusyIndicatorTests
    {
        [TestMethod]
        public void BusyIndicator_TogglesWithTheJob()
        {
            using (var marshal = new Control())
            {
                var status = new ToolStripStatusLabel();
                var cancel = new ToolStripButton { Visible = false };
                var busy = new ToolStripProgressBar { Visible = false };

                var runner = new JobRunner(marshal, status, cancel) { BusyIndicator = busy };

                Assert.IsFalse(busy.Available, "indicator hidden before any job");

                MethodInfo begin = typeof(JobRunner).GetMethod("BeginJob", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo end = typeof(JobRunner).GetMethod("EndJob", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(begin, "BeginJob expected");
                Assert.IsNotNull(end, "EndJob expected");

                begin.Invoke(runner, new object[] { "Compiling", null });
                Assert.IsTrue(busy.Available, "indicator shown while a job runs");
                Assert.IsTrue(cancel.Available, "Cancel also shown (parity)");

                end.Invoke(runner, new object[] { "Done" });
                Assert.IsFalse(busy.Available, "indicator hidden after the job ends");
                Assert.IsFalse(cancel.Available, "Cancel also hidden (parity)");
            }
        }

        [TestMethod]
        public void Lifecycle_ExposesSingleAuthoritativeOperationAndEvents()
        {
            using (var marshal = new Control())
            {
                var runner = new JobRunner(marshal, new ToolStripStatusLabel(), new ToolStripButton());
                JobRunnerEventArgs started = null;
                JobRunnerEventArgs finished = null;
                runner.JobStarted += delegate(object sender, JobRunnerEventArgs e) { started = e; };
                runner.JobFinished += delegate(object sender, JobRunnerEventArgs e) { finished = e; };

                Assert.AreEqual(JobRunnerState.Idle, runner.State);
                Assert.IsTrue(runner.BeginExternal("Building workspace", delegate { }));
                Assert.AreEqual(JobRunnerState.Running, runner.State);
                Assert.AreEqual("Building workspace", runner.CurrentTitle);
                Assert.IsFalse(runner.BeginExternal("Competing build", delegate { }),
                    "a second foreground operation must not acquire the scheduler");
                Assert.IsNotNull(started);
                Assert.AreEqual("Building workspace", started.Title);

                runner.Finish("Build succeeded");

                Assert.AreEqual(JobRunnerState.Idle, runner.State);
                Assert.IsNull(runner.CurrentTitle);
                Assert.IsNotNull(finished);
                Assert.AreEqual("Building workspace", finished.Title);
                Assert.AreEqual("Build succeeded", finished.FinalStatus);
                Assert.IsFalse(finished.Cancelled);
            }
        }

        [TestMethod]
        public void Cancel_TransitionsToCancellingUntilExternalOperationFinishes()
        {
            using (var marshal = new Control())
            {
                var runner = new JobRunner(marshal, new ToolStripStatusLabel(), new ToolStripButton());
                Assert.IsTrue(runner.BeginExternal("Building", delegate { }));

                runner.Cancel();

                Assert.AreEqual(JobRunnerState.Cancelling, runner.State);
                Assert.IsTrue(runner.IsBusy, "cancelling work still owns the scheduler");
                runner.Finish("Build cancelled");
                Assert.AreEqual(JobRunnerState.Idle, runner.State);
            }
        }
    }
}
