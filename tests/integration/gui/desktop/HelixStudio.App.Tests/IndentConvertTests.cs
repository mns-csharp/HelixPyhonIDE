using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 481 (status-bar): the indentation indicator is clickable to convert the document between
    /// spaces and tabs (VS Code parity). ConvertIndentation rewrites only LEADING whitespace, preserving
    /// the visual indent column, and leaves in-line whitespace (alignment, strings) alone.
    /// </summary>
    [TestClass]
    public class IndentConvertTests
    {
        private static string Convert(string text, bool toTabs, int tabWidth)
        {
            MethodInfo m = typeof(MainForm).GetMethod("ConvertIndentation", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.ConvertIndentation(string,bool,int) expected");
            return (string)m.Invoke(null, new object[] { text, toTabs, tabWidth });
        }

        [TestMethod]
        public void SpacesToTabs_ConvertsLeadingIndentOnly()
        {
            // 4-space indent -> one tab; the 8-space nested body -> two tabs.
            string src = "def f():\n    x = 1\n        y = 2\n";
            string expected = "def f():\n\tx = 1\n\t\ty = 2\n";
            Assert.AreEqual(expected, Convert(src, toTabs: true, tabWidth: 4));
        }

        [TestMethod]
        public void TabsToSpaces_ExpandsToTabWidth()
        {
            string src = "def f():\n\tx = 1\n\t\ty = 2\n";
            string expected = "def f():\n    x = 1\n        y = 2\n";
            Assert.AreEqual(expected, Convert(src, toTabs: false, tabWidth: 4));
        }

        [TestMethod]
        public void InlineWhitespace_IsUntouched()
        {
            // The spaces around '=' and inside the string must survive a spaces->tabs conversion.
            string src = "    a = \"two  spaces\"\n";
            string got = Convert(src, toTabs: true, tabWidth: 4);
            Assert.AreEqual("\ta = \"two  spaces\"\n", got);
        }

        [TestMethod]
        public void MixedLeadingWhitespace_NormalizesByVisualColumn()
        {
            // A tab (to col 4) + 2 spaces = col 6. To spaces -> 6 spaces.
            string src = "\t  x = 1\n";
            Assert.AreEqual("      x = 1\n", Convert(src, toTabs: false, tabWidth: 4));
        }

        [TestMethod]
        public void RoundTrips_SpacesTabsSpaces()
        {
            string spaces = "class C:\n    def m(self):\n        return 1\n";
            string tabs = Convert(spaces, toTabs: true, tabWidth: 4);
            Assert.AreEqual(spaces, Convert(tabs, toTabs: false, tabWidth: 4));
        }

        [TestMethod]
        public void MainForm_HasIndentClickAndConvertMembers()
        {
            Assert.IsNotNull(typeof(MainForm).GetMethod("OnIndentLabelClick", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.IsNotNull(typeof(MainForm).GetMethod("ConvertActiveEditorIndent", BindingFlags.Instance | BindingFlags.NonPublic));
            string src = File.ReadAllText(SourcePath(@"src\gui\desktop\HelixStudio.App\MainForm.cs"));
            StringAssert.Contains(src, "_indentLabel.Click += OnIndentLabelClick");
            StringAssert.Contains(src, "Convert Indentation to Tabs");
            StringAssert.Contains(src, "Convert Indentation to Spaces");
        }

        private static string SourcePath(string relative)
        {
            string dir = Path.GetDirectoryName(typeof(IndentConvertTests).Assembly.Location);
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
