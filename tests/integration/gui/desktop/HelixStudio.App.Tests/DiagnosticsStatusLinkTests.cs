using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 465 (status-bar workflow): the "N errors, M warnings" summary is now a clickable link that
    /// jumps to the Compile panel (VS Code parity), and reads as a link only while there is a count.
    /// The status strip needs the full form, so this pins the wiring; the behaviour is proven by
    /// screenshot.
    /// </summary>
    [TestClass]
    public class DiagnosticsStatusLinkTests
    {
        private static string MainFormSource()
        {
            return File.ReadAllText(SourcePath(@"src\gui\desktop\HelixStudio.App\MainForm.cs"));
        }

        [TestMethod]
        public void DiagnosticsLabel_ClicksThroughToTheCompilePanel()
        {
            string src = MainFormSource();
            StringAssert.Contains(src, "_diagnosticsStatusLabel.Click += delegate { ShowTab(\"problems\"); }",
                "clicking the diagnostics summary must show the Compile panel");
            StringAssert.Contains(src, "_diagnosticsStatusLabel.ToolTipText = \"Show the Compile panel\"",
                "the summary should advertise its action via a tooltip");
        }

        [TestMethod]
        public void DiagnosticsLabel_IsALinkOnlyWhenThereIsACount()
        {
            string src = MainFormSource();
            int at = src.IndexOf("private void OnDiagnosticsChanged(", StringComparison.Ordinal);
            Assert.IsTrue(at > 0, "OnDiagnosticsChanged not found — this test needs updating");
            int end = src.IndexOf("\n        // The Compile tab caption", at, StringComparison.Ordinal);
            if (end < 0) end = src.Length;
            string body = src.Substring(at, end - at);

            StringAssert.Contains(body, "_diagnosticsStatusLabel.IsLink = false",
                "with no diagnostics the summary must not read as a clickable link");
            StringAssert.Contains(body, "_diagnosticsStatusLabel.IsLink = true",
                "with a count the summary becomes a clickable link");
            StringAssert.Contains(body, "_diagnosticsStatusLabel.LinkColor = sev",
                "the link colour must track the severity colour so it isn't washed out");
        }

        private static string SourcePath(string relative)
        {
            string dir = Path.GetDirectoryName(typeof(DiagnosticsStatusLinkTests).Assembly.Location);
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
