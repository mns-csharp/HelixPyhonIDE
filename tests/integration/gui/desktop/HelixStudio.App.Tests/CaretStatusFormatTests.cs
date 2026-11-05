using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 291: the status-bar caret readout showed "Ln L, Col C" but no selection size. It now
    /// appends "(N selected)" while a selection is active (VS / VS Code parity). Guards the format.
    /// </summary>
    [TestClass]
    public class CaretStatusFormatTests
    {
        private static string Format(int line, int col, int sel)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "FormatCaretText", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "MainForm.FormatCaretText(int,int,int) expected");
            return (string)m.Invoke(null, new object[] { line, col, sel });
        }

        [TestMethod]
        public void NoSelection_ShowsLineAndColumnOnly()
        {
            Assert.AreEqual("Ln 5, Col 12", Format(5, 12, 0));
        }

        [TestMethod]
        public void WithSelection_AppendsSelectedCount()
        {
            Assert.AreEqual("Ln 5, Col 12 (48 selected)", Format(5, 12, 48));
        }

        /// <summary>
        /// Iter 349: the readout was fed only by caret MOVES and tab-change events, so a freshly
        /// opened document showed a blank Ln/Col until the user happened to click in the text —
        /// caught in a screenshot of SymPy's codegen/ast.py. Opening a document must seed it.
        ///
        /// Asserting the call site rather than driving a real Scintilla open: the editor control
        /// needs its native DLL, so the wiring is what a test can honestly pin here. The behaviour
        /// itself is proven by screenshot.
        /// </summary>
        [TestMethod]
        public void OpeningADocument_SeedsTheReadout()
        {
            string source = System.IO.File.ReadAllText(SourcePath(
                @"src\gui\desktop\HelixStudio.App\Coordinators\EditorCoordinator.cs"));

            int open = source.IndexOf("public void OpenInEditor(", System.StringComparison.Ordinal);
            Assert.IsTrue(open > 0, "OpenInEditor not found — this test needs updating");

            // The next method starts at the following "public"/"private" at method indentation.
            int end = source.IndexOf("\n        public ", open + 10, System.StringComparison.Ordinal);
            if (end < 0) end = source.Length;
            string body = source.Substring(open, end - open);

            StringAssert.Contains(body, "UpdateCaretStatus()",
                "OpenInEditor must seed the Ln/Col readout, or a freshly opened document shows blank");
        }

        // Walks up from the test assembly to the repository root, which holds the product .sln.
        private static string SourcePath(string relative)
        {
            string dir = System.IO.Path.GetDirectoryName(typeof(CaretStatusFormatTests).Assembly.Location);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (System.IO.Directory.GetFiles(dir, "*.sln").Length > 0)
                    return System.IO.Path.Combine(dir, relative);
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            Assert.Fail("could not locate the repository root from " + typeof(CaretStatusFormatTests).Assembly.Location);
            return null;
        }
    }
}
