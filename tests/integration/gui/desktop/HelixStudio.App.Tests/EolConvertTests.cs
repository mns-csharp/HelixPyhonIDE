using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 477 (status-bar): the CRLF/LF indicator is now clickable to convert the document's line
    /// endings (VS Code parity). This pins the wiring (the click handler + convert helper exist and the
    /// menu offers CRLF/LF); the conversion itself is Scintilla's ConvertEols, exercised by the editor.
    /// The DetectLineEnding round-trips a converted document, which the convert path relies on.
    /// </summary>
    [TestClass]
    public class EolConvertTests
    {
        private static string Detect(string text)
        {
            MethodInfo m = typeof(MainForm).GetMethod("DetectLineEnding", BindingFlags.Static | BindingFlags.NonPublic);
            return (string)m.Invoke(null, new object[] { text });
        }

        [TestMethod]
        public void MainForm_HasTheEolClickAndConvertMembers()
        {
            Assert.IsNotNull(typeof(MainForm).GetMethod("OnEolLabelClick", BindingFlags.Instance | BindingFlags.NonPublic),
                "OnEolLabelClick handler expected");
            Assert.IsNotNull(typeof(MainForm).GetMethod("ConvertActiveEditorEol", BindingFlags.Instance | BindingFlags.NonPublic),
                "ConvertActiveEditorEol helper expected");
        }

        [TestMethod]
        public void DetectRoundTrips_ConvertedContent()
        {
            // After a CRLF->LF conversion the whole text uses '\n'; DetectLineEnding must report LF.
            string crlf = "a\r\nb\r\nc\r\n";
            string lf = crlf.Replace("\r\n", "\n");
            Assert.AreEqual("CRLF", Detect(crlf));
            Assert.AreEqual("LF", Detect(lf));
        }

        [TestMethod]
        public void EolLabelIsWiredToClickConvert()
        {
            string src = File.ReadAllText(SourcePath(@"src\gui\desktop\HelixStudio.App\MainForm.cs"));
            StringAssert.Contains(src, "_eolLabel.Click += OnEolLabelClick", "the EOL label must be clickable");
            StringAssert.Contains(src, "ConvertEols(eol)", "the convert path must call Scintilla ConvertEols");
            StringAssert.Contains(src, "Windows (CRLF)", "the menu should offer CRLF");
            StringAssert.Contains(src, "Unix (LF)", "the menu should offer LF");
        }

        private static string SourcePath(string relative)
        {
            string dir = Path.GetDirectoryName(typeof(EolConvertTests).Assembly.Location);
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
