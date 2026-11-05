using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// FR-X-01: command-history recall for the integrated terminal input line (iter 210).
    /// Mirrors cmd/PowerShell/bash Up/Down behaviour so the panel gains a table-stakes feature
    /// without any UI dependency.
    /// </summary>
    [TestClass]
    public class TerminalHistoryTests
    {
        [TestMethod]
        public void Prev_OnEmptyHistory_ReturnsNull_LeavingInputUntouched()
        {
            var h = new TerminalHistory();
            Assert.IsNull(h.Prev());
            Assert.IsNull(h.Next());
            Assert.AreEqual(0, h.Count);
        }

        [TestMethod]
        public void Up_WalksFromNewestToOldest_AndStopsAtOldest()
        {
            var h = new TerminalHistory();
            h.Add("one");
            h.Add("two");
            h.Add("three");

            Assert.AreEqual("three", h.Prev());
            Assert.AreEqual("two", h.Prev());
            Assert.AreEqual("one", h.Prev());
            // Already at the oldest entry — stays there rather than wrapping.
            Assert.AreEqual("one", h.Prev());
        }

        [TestMethod]
        public void Down_WalksBackTowardEmptyNewLine()
        {
            var h = new TerminalHistory();
            h.Add("a");
            h.Add("b");

            Assert.AreEqual("b", h.Prev());
            Assert.AreEqual("a", h.Prev());
            Assert.AreEqual("b", h.Next());
            // Past the newest entry -> fresh empty line.
            Assert.AreEqual("", h.Next());
            // Staying at the bottom keeps returning the empty line.
            Assert.AreEqual("", h.Next());
        }

        [TestMethod]
        public void Add_IgnoresBlanks_ButResetsCursorToBottom()
        {
            var h = new TerminalHistory();
            h.Add("real");
            h.Prev();                 // cursor now on "real"
            h.Add("   ");             // blank ignored, but cursor resets to bottom
            Assert.AreEqual(1, h.Count);
            // A fresh Up should start again from the newest entry.
            Assert.AreEqual("real", h.Prev());
        }

        [TestMethod]
        public void Add_CollapsesConsecutiveDuplicates()
        {
            var h = new TerminalHistory();
            h.Add("ls");
            h.Add("ls");
            h.Add("cd ..");
            h.Add("cd ..");
            Assert.AreEqual(2, h.Count);
            Assert.AreEqual("cd ..", h.Prev());
            Assert.AreEqual("ls", h.Prev());
        }
    }
}
