using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// A cell that never finishes.
    ///
    /// <para>
    /// Reported against a two-line cell — <c>import numpy as np</c> / <c>print(np.__version__)</c>
    /// — that sat at "Executing cell 1 of 1" for 1m 6s with no output, while the runtime strip
    /// still read "Connected · Python 3.11.0 (ipython)". The IDE had no way to tell that from a
    /// slow cell, so it showed the same spinner for both, forever. These pin the two halves of
    /// telling them apart, both against a REAL kernel process.
    /// </para>
    ///
    /// <para>
    /// The mechanism is the host's own ping. It answers requests one at a time on its main thread,
    /// so a pong that comes back while the IDE believes a cell is running proves the host is not
    /// running it. Nothing is killed and nothing is re-run — a cell may have completed with its
    /// status lost, and re-running somebody's side effects to tidy up a spinner is not this IDE's
    /// call.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookRunStallTests
    {
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, target.GetType().Name + " has no field " + name);
            field.SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Private);
            Assert.IsNotNull(field, target.GetType().Name + " has no field " + name);
            return field.GetValue(target);
        }

        private static string NewNotebook(string code)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-stall-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "Untitled.ipynb");
            NotebookDocument document = NotebookFactory.CreateNew("3.11");
            document.Cells[0].Source = code;
            NotebookWriter.WriteFile(document, path);
            return path;
        }

        /// <summary>A realized shell with the notebook open — Show() because an unshown form composes nothing.</summary>
        private static NotebookTabPage Open(MainForm form, string path)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            form.Size = new System.Drawing.Size(1400, 900);
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
            form.OpenInEditor(path);
            TestMainForm.PumpUntilIdle(iterations: 20, delayMs: 20);
            return form._editorInnerTabControl.SelectedTab as NotebookTabPage;
        }

        private static bool PumpUntil(Func<bool> done, int seconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
                if (done()) return true;
            }
            return false;
        }

        private static bool HasInterpreter(NotebookTabPage tab)
        {
            string interpreter = tab.InterpreterResolver == null ? null : tab.InterpreterResolver();
            return !string.IsNullOrEmpty(interpreter) && File.Exists(interpreter);
        }

        /// <summary>
        /// The baseline the whole feature rests on: the reported cell runs, and quickly. If this
        /// fails the notebook cannot execute at all and the two below say nothing useful.
        /// </summary>
        [TestMethod]
        public void TheReportedCellRunsAndPrints()
        {
            string path = NewNotebook("import numpy as np\nprint(np.__version__)\n");
            using (MainForm form = TestMainForm.CreateComposed())
            {
                NotebookTabPage tab = Open(form, path);
                Assert.IsNotNull(tab, "the notebook did not open on the notebook surface");
                if (!HasInterpreter(tab)) { Assert.Inconclusive("no Python interpreter on this machine"); return; }

                tab.RunCell(0);
                bool finished = PumpUntil(() => !tab.Document.Cells[0].IsRunning
                                             && tab.Document.Cells[0].Outputs.Count > 0, 60);

                Assert.IsTrue(finished, "the cell never produced output");
                Assert.IsFalse(tab.StallWasReported, "a cell that ran normally must not be called stalled");
                StringAssert.Contains(tab.Document.Cells[0].Outputs[0].PlainText(), ".",
                    "the printed version should be there");
                tab.ShutdownKernel();
            }
        }

        /// <summary>
        /// The state the bug leaves behind — the IDE believes a cell is running, the host is idle —
        /// staged directly, because it is a lost message and cannot be provoked on demand. Every
        /// line after the staging is production code: the watchdog pings a REAL host process, the
        /// real pong comes back, and the run is let go of.
        /// </summary>
        [TestMethod]
        public void ARunTheRuntimeIsNotPerforming_IsDetectedAndReleased()
        {
            string path = NewNotebook("x = 1\n");
            using (MainForm form = TestMainForm.CreateComposed())
            {
                NotebookTabPage tab = Open(form, path);
                Assert.IsNotNull(tab);
                if (!HasInterpreter(tab)) { Assert.Inconclusive("no Python interpreter on this machine"); return; }

                // Start a real kernel and let it settle, so the ping below goes to a live host.
                tab.RunCell(0);
                Assert.IsTrue(PumpUntil(() => !tab.Document.Cells[0].IsRunning, 60), "the kernel never started");

                // Stage the symptom: a cell marked running that the host was never asked to run.
                tab.Document.Cells[0].IsRunning = true;
                tab.Document.Cells[0].RunStartedUtc = DateTime.UtcNow;
                SetField(tab, "_runningCell", 0);
                SetField(tab, "_runSentUtc", DateTime.UtcNow - TimeSpan.FromMinutes(1));
                SetField(tab, "_stallReported", false);
                ((Timer)GetField(tab, "_runWatchdog")).Enabled = true;

                bool released = PumpUntil(() => tab.StallWasReported, 30);

                Assert.IsTrue(released,
                    "the runtime answered a ping while a cell was 'running' — that must not be ignored");
                Assert.IsFalse(tab.Document.Cells[0].IsRunning,
                    "the spinner must stop; a cell nothing is executing is not running");
                Assert.AreEqual(-1, (int)GetField(tab, "_runningCell"),
                    "the queue must be released or the next Run is silently dropped");
                tab.ShutdownKernel();
            }
        }

        /// <summary>
        /// The false positive that would make the watchdog worse than useless. A cell that sleeps
        /// past the suspicion threshold is doing exactly what it was told to; the host is busy with
        /// it and cannot answer the ping, which is the whole reason the ping is the right question.
        /// A training loop that prints nothing for a minute must not be declared broken.
        /// </summary>
        [TestMethod]
        public void ASlowCellIsNotCalledStalled()
        {
            string path = NewNotebook("import time\ntime.sleep(16)\nprint('done')\n");
            using (MainForm form = TestMainForm.CreateComposed())
            {
                NotebookTabPage tab = Open(form, path);
                Assert.IsNotNull(tab);
                if (!HasInterpreter(tab)) { Assert.Inconclusive("no Python interpreter on this machine"); return; }

                tab.RunCell(0);
                bool finished = PumpUntil(() => !tab.Document.Cells[0].IsRunning
                                             && tab.Document.Cells[0].Outputs.Count > 0, 90);

                Assert.IsTrue(finished, "the slow cell never finished");
                Assert.IsFalse(tab.StallWasReported,
                    "a cell that was genuinely running for 16s was reported as a stall");
                StringAssert.Contains(tab.Document.Cells[0].Outputs[0].PlainText(), "done");
                tab.ShutdownKernel();
            }
        }
    }
}
