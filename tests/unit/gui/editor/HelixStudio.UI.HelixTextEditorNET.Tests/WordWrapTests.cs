using System;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixTextEditorNET;

namespace HelixPythonNamespace.UI.HelixTextEditorNET.Tests
{
    /// <summary>
    /// Word wrap in the managed Scintilla port.
    ///
    /// <c>WrapMode</c> was previously an API-compatibility stub that nothing read, so setting it
    /// looked like it worked and changed nothing on screen. Wrapping now drives three things that
    /// must agree: painting, hit-testing, and the caret's x. If they disagree, clicking lands on
    /// the wrong character — worse than having no wrap at all — so the round-trip is what these
    /// tests actually assert.
    /// </summary>
    [TestClass]
    public class WordWrapTests
    {
        private const string LongLine =
            "run-args = \"../../pipeline_output_fullschema/training_data/culled_id30.tsv "
            + "../../pipeline_output_ide/models/sample_project --epochs 2\"";

        private static Scintilla NewEditor(string text, int width = 380)
        {
            var editor = new Scintilla { Width = width, Height = 300 };
            var _ = editor.Handle;              // realise so measurement and scrollbars are live
            editor.Text = text;
            return editor;
        }

        [TestMethod]
        public void WrapModeIsOffByDefault()
        {
            using (var editor = NewEditor(LongLine))
                Assert.AreEqual(WrapMode.None, editor.WrapMode);
        }

        [TestMethod]
        public void WrappingALongLine_ProducesMoreDisplayRowsThanDocumentLines()
        {
            using (var editor = NewEditor(LongLine))
            {
                Assert.AreEqual(1, editor.Lines.Count, "precondition: one document line");

                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                Assert.AreEqual(WrapMode.Word, editor.WrapMode);
                Assert.IsTrue(editor.VScrollMaximumForTests >= 1,
                    "a long line must occupy several display rows once wrapped; "
                    + "got " + editor.VScrollMaximumForTests);
            }
        }

        [TestMethod]
        public void WrappingRemovesTheHorizontalScrollRange()
        {
            using (var editor = NewEditor(LongLine))
            {
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                Assert.AreEqual(0, editor.HScrollMaximumForTests,
                    "wrapped text has no horizontal extent; leaving the scrollbar live is what "
                    + "makes wrap look like it did nothing");
            }
        }

        [TestMethod]
        public void ShortLines_AreUnaffectedByWrapping()
        {
            using (var editor = NewEditor("a = 1\nb = 2\nc = 3\n"))
            {
                int before = editor.VScrollMaximumForTests;
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                Assert.AreEqual(before, editor.VScrollMaximumForTests,
                    "lines that already fit must not gain rows");
            }
        }

        [TestMethod]
        public void CaretPositionRoundTripsThroughTheWrappedLayout()
        {
            using (var editor = NewEditor(LongLine))
            {
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                // Painting, hit-testing and x-mapping must agree: the point computed for a
                // position must map back to that position.
                foreach (int pos in new[] { 0, 12, 40, 75, LongLine.Length - 1 })
                {
                    int x = editor.PointXFromPosition(pos);
                    int y = editor.PointYFromPosition(pos);
                    int roundTrip = editor.CharPositionFromPoint(x + 1, y + 2);
                    Assert.IsTrue(Math.Abs(roundTrip - pos) <= 1,
                        $"position {pos} mapped to ({x},{y}) came back as {roundTrip}; "
                        + "hit-testing disagrees with painting, so clicks would land wrong");
                }
            }
        }

        [TestMethod]
        public void TurningWrapOffRestoresTheHorizontalRange()
        {
            using (var editor = NewEditor(LongLine))
            {
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();
                editor.WrapMode = WrapMode.None;
                Application.DoEvents();

                Assert.AreEqual(WrapMode.None, editor.WrapMode);
                Assert.IsTrue(editor.HScrollMaximumForTests > 0,
                    "unwrapping must give the horizontal scrollbar its range back");
            }
        }
    }
}
