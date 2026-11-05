using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 463 (status-bar workflow): the "Ln x, Col y" readout is now a clickable link that opens
    /// Go to Line for the active editor (VS Code parity). The editor control needs its native DLL, so
    /// this pins the wiring — the behaviour itself is proven by screenshot.
    /// </summary>
    [TestClass]
    public class CaretLabelGoToLineTests
    {
        [TestMethod]
        public void MainForm_HasACaretLabelClickHandler()
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "OnCaretLabelClick", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.OnCaretLabelClick(object, EventArgs) expected");
        }

        [TestMethod]
        public void MainForm_HasTheActiveEditorHelper()
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "ActiveEditorTabOrNull", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.ActiveEditorTabOrNull() expected");
        }

        [TestMethod]
        public void CaretLabel_IsWiredAsAGoToLineLink()
        {
            string src = File.ReadAllText(SourcePath(@"src\gui\desktop\HelixStudio.App\MainForm.cs"));
            StringAssert.Contains(src, "_caretLabel.Click += OnCaretLabelClick",
                "the Ln/Col label must invoke Go to Line on click");
            StringAssert.Contains(src, "_caretLabel.ToolTipText = \"Go to Line",
                "the label should advertise its action via a tooltip");
        }

        [TestMethod]
        public void UpdateCaretStatus_TogglesTheLinkOnEditorPresence()
        {
            string src = File.ReadAllText(SourcePath(@"src\gui\desktop\HelixStudio.App\MainForm.cs"));
            int at = src.IndexOf("internal void UpdateCaretStatus()", StringComparison.Ordinal);
            Assert.IsTrue(at > 0, "UpdateCaretStatus not found — this test needs updating");
            int end = src.IndexOf("\n        private ", at, StringComparison.Ordinal);
            if (end < 0) end = src.Length;
            string body = src.Substring(at, end - at);
            StringAssert.Contains(body, "_caretLabel.IsLink = etp != null",
                "the readout should only read as a clickable link while an editor is active");
        }

        private static string SourcePath(string relative)
        {
            string dir = Path.GetDirectoryName(typeof(CaretLabelGoToLineTests).Assembly.Location);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (Directory.GetFiles(dir, "*.sln").Length > 0)
                    return Path.Combine(dir, relative);
                dir = Path.GetDirectoryName(dir);
            }
            Assert.Fail("could not locate the repository root");
            return null;
        }
    }
}
