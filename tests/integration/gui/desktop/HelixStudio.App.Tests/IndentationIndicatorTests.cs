using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 479 (status-bar): the status bar shows the active file's indentation ("Spaces: N" / "Tabs"),
    /// VS-Code style, detected from the content. The reported width is the SMALLEST positive leading-
    /// space count (the indent unit), and a tab-indented file reads "Tabs".
    /// </summary>
    [TestClass]
    public class IndentationIndicatorTests
    {
        private static string Detect(string text)
        {
            MethodInfo m = typeof(MainForm).GetMethod("DetectIndentation", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.DetectIndentation(string) expected");
            return (string)m.Invoke(null, new object[] { text });
        }

        [TestMethod]
        public void FourSpaceIndent_IsReported()
        {
            Assert.AreEqual("Spaces: 4", Detect("def f():\n    return 1\n"));
        }

        [TestMethod]
        public void TwoSpaceIndent_IsReported()
        {
            Assert.AreEqual("Spaces: 2", Detect("if x:\n  y = 1\n"));
        }

        [TestMethod]
        public void NestedIndent_ReportsTheUnitNotTheDeepest()
        {
            // First indented line is 4 spaces, nested body is 8; the unit is 4.
            Assert.AreEqual("Spaces: 4", Detect("def f():\n    if x:\n        y = 1\n"));
        }

        [TestMethod]
        public void TabIndent_IsReportedAsTabs()
        {
            Assert.AreEqual("Tabs", Detect("def f():\n\treturn 1\n"));
        }

        [TestMethod]
        public void NoIndentation_IsBlank()
        {
            Assert.AreEqual(string.Empty, Detect("a = 1\nb = 2\n"));
            Assert.AreEqual(string.Empty, Detect(string.Empty));
            Assert.AreEqual(string.Empty, Detect(null));
        }

        [TestMethod]
        public void BlankIndentedLines_AreIgnored()
        {
            // A line of only spaces is not real indentation; the 4-space body is the unit.
            Assert.AreEqual("Spaces: 4", Detect("def f():\n  \n    return 1\n"));
        }

        [TestMethod]
        public void StatusBarWiresTheIndentLabel()
        {
            string src = File.ReadAllText(SourcePath(@"src\gui\desktop\HelixStudio.App\MainForm.cs"));
            StringAssert.Contains(src, "_indentLabel = new ToolStripStatusLabel", "the status bar should create an indent label");
            StringAssert.Contains(src, "DetectIndentation(text)", "UpdateEolStatus should populate it");
        }

        private static string SourcePath(string relative)
        {
            string dir = Path.GetDirectoryName(typeof(IndentationIndicatorTests).Assembly.Location);
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
