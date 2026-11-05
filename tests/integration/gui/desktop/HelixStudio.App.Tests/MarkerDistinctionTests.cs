using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Regression for "is that red dot an error or a breakpoint?": the diagnostic error
    /// mark and the persistent breakpoint used to be IDENTICAL solid red circles in the
    /// margin, so one was routinely misread as the other. The error mark is now a bold
    /// red '✕' (SC_MARK_CHARACTER glyph); the breakpoint keeps the classic filled disc.
    /// </summary>
    [TestClass]
    public class MarkerDistinctionTests
    {
        [TestMethod]
        public void ErrorMark_AndBreakpointMark_AreVisuallyDistinct()
        {
            using (var ed = new HelixPythonNamespace.Editor.PythonEditorControl())
            {
                // Marker indices per PythonEditorControl: 1 = MARKER_ERROR, 3 = MARKER_BREAKPOINT.
                var err = ed.Markers[1].Symbol;
                var bp = ed.Markers[3].Symbol;

                Assert.AreNotEqual(bp, err,
                    "an error mark must never render the same glyph as a breakpoint");
                Assert.IsTrue((int)err >= (int)global::HelixTextEditorNET.MarkerSymbol.Character,
                    "the error mark is a character glyph ('✕'), not a disc");
                Assert.AreEqual((int)global::HelixTextEditorNET.MarkerSymbol.Character + '✕', (int)err,
                    "the error glyph is the red '✕'");
                Assert.AreEqual(global::HelixTextEditorNET.MarkerSymbol.Circle, bp,
                    "the breakpoint keeps the classic solid disc");
            }
        }
    }
}
