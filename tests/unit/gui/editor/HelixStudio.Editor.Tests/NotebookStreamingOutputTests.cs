using System;
using HelixPythonNamespace.Editor.Notebook;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class NotebookStreamingOutputTests
    {
        private static NotebookOutput Stream(string text)
        {
            return new NotebookOutput
            {
                Kind = NotebookOutputKind.Stream,
                StreamName = "stdout",
                Text = text,
            };
        }

        [TestMethod]
        public void SlowOutputPaintsWhileTheCellIsStillRunning()
        {
            using (var tab = new NotebookTabPage())
            {
                DateTime first = DateTime.UtcNow;
                Assert.IsTrue(tab.ShouldPaintStream(Stream("epoch 1"), first));
                Assert.IsTrue(tab.ShouldPaintStream(Stream("epoch 2"), first.AddMilliseconds(76)),
                    "a slow producer must not wait for execution completion before becoming visible");
            }
        }

        [TestMethod]
        public void BurstOutputWaitsForTheNextFrame()
        {
            using (var tab = new NotebookTabPage())
            {
                DateTime now = DateTime.UtcNow;
                Assert.IsTrue(tab.ShouldPaintStream(Stream("start"), now));
                for (int line = 1; line < 100; line++)
                    Assert.IsFalse(tab.ShouldPaintStream(Stream("line\n"), now.AddMilliseconds(1)),
                        "a burst should be coalesced instead of laying out the notebook per line");
                Assert.IsTrue(tab.ShouldPaintStream(Stream("line\n"), now.AddMilliseconds(76)),
                    "the accumulated burst must be forced to screen on the next frame");
            }
        }
    }
}
