using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 475 (status-bar): the status bar now shows the active file's line ending (CRLF / LF), the
    /// way VS Code does. DetectLineEnding decides it from the first newline (files are consistent), so
    /// the scan stays O(first line). Empty / single-line text has no line ending to report.
    /// </summary>
    [TestClass]
    public class LineEndingIndicatorTests
    {
        private static string Detect(string text)
        {
            MethodInfo m = typeof(MainForm).GetMethod("DetectLineEnding", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.DetectLineEnding(string) expected");
            return (string)m.Invoke(null, new object[] { text });
        }

        [TestMethod]
        public void Crlf_IsReported() => Assert.AreEqual("CRLF", Detect("a\r\nb\r\n"));

        [TestMethod]
        public void Lf_IsReported() => Assert.AreEqual("LF", Detect("a\nb\n"));

        [TestMethod]
        public void Cr_IsReported() => Assert.AreEqual("CR", Detect("a\rb\r"));

        [TestMethod]
        public void FirstNewlineWins_OnMixedContent() => Assert.AreEqual("LF", Detect("a\nb\r\nc"));

        [TestMethod]
        public void NoNewline_IsBlank()
        {
            Assert.AreEqual(string.Empty, Detect("single line"));
            Assert.AreEqual(string.Empty, Detect(string.Empty));
            Assert.AreEqual(string.Empty, Detect(null));
        }

        [TestMethod]
        public void StatusBarWiresTheEolLabel()
        {
            string src = File.ReadAllText(SourcePath(@"src\gui\desktop\HelixStudio.App\MainForm.cs"));
            StringAssert.Contains(src, "_eolLabel = new ToolStripStatusLabel", "the status bar should create an EOL label");
            StringAssert.Contains(src, "internal void UpdateEolStatus()", "UpdateEolStatus() should refresh it");
        }

        private static string SourcePath(string relative)
        {
            string dir = Path.GetDirectoryName(typeof(LineEndingIndicatorTests).Assembly.Location);
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
