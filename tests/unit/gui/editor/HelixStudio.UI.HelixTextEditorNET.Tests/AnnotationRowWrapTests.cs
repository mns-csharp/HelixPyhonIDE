using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixTextEditorNET;

namespace HelixPythonNamespace.UI.HelixTextEditorNET.Tests
{
    /// <summary>
    /// Annotation rows (the reserved rows the editor draws virtual braces and type markers into)
    /// under word wrap.
    ///
    /// A wrapped line occupies several display rows, so the row reserved after it moves down by
    /// that many. PointYFromAnnotationRow used to assume one row per document line, which put every
    /// virtual brace below the first wrapped line against unrelated code — reported from the running
    /// IDE: the `}` closing __init__ appeared beside the wrapped call itself.
    /// </summary>
    [TestClass]
    public class AnnotationRowWrapTests
    {
        private const string LongLine =
            "        super().__init__(\"train-sample\", \"Launch sample workspace backbone training, "
            + "which is a deliberately long argument so that this line has to wrap.\")";

        private static Scintilla NewEditor(string text, int width = 380)
        {
            var editor = new Scintilla { Width = width, Height = 300 };
            var _ = editor.Handle;
            editor.Text = text;
            return editor;
        }

        [TestMethod]
        public void TheRowReservedAfterAWrappedLine_ClearsAllOfItsRows()
        {
            using (var editor = NewEditor(LongLine + "\n        return 1\n"))
            {
                editor.AnnotationSetLines(0, 1);       // one reserved row, as a virtual brace uses
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                int lh = editor.LineHeightPixels;
                int yLine1 = editor.PointYFromPosition(0);
                int yAnnotation = editor.PointYFromAnnotationRow(0, 0);
                int yLine2 = editor.PointYFromPosition(editor.Text.IndexOf("return 1"));

                Assert.IsTrue(yAnnotation >= yLine1 + 2 * lh,
                    "line 1 wraps onto several rows, so its reserved row must clear all of them — "
                    + "it sat at +" + ((yAnnotation - yLine1) / lh) + " rows, and the brace drawn "
                    + "there would land on the line's own wrapped text");
                Assert.AreEqual(yAnnotation + lh, yLine2,
                    "line 2's text must start on the row directly after the reserved row");
            }
        }

        /// <summary>
        /// Scrolled into a wrapped file, the document line reported for the top display row must be
        /// the one actually painted there. Overlay renderers (virtual braces, type markers) cull to
        /// the viewport with DocLineFromVisible, so when it drifts they stop drawing for lines that
        /// are on screen — reported from the running IDE as braces going missing after scrolling.
        /// </summary>
        [TestMethod]
        public void ScrolledIntoAWrappedFile_TheTopRowsDocumentLineIsReportedCorrectly()
        {
            using (var editor = NewEditor(LongLine + "\n" + LongLine + "\n        return 1\n        return 2\n"))
            {
                editor.AnnotationSetLines(0, 1);          // a virtual brace row after line 1
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                for (int row = 0; row <= 5; row++)
                {
                    editor.FirstVisibleLine = row;
                    Application.DoEvents();

                    int docLine = editor.DocLineFromVisible(editor.FirstVisibleLine);
                    int y = editor.PointYFromPosition(editor.Lines[docLine].Position);
                    Assert.IsTrue(y <= 0,
                        "row " + row + " reported line " + docLine + ", but that line is painted "
                        + y + "px BELOW the top edge — it is not the line at the top row");

                    if (docLine + 1 < editor.Lines.Count)
                    {
                        int nextY = editor.PointYFromPosition(editor.Lines[docLine + 1].Position);
                        Assert.IsTrue(nextY > 0,
                            "row " + row + " reported line " + docLine + ", but the NEXT line already "
                            + "starts at " + nextY + "px — the top row belongs to a later line");
                    }
                }
            }
        }

        [TestMethod]
        public void WithWrapOff_TheReservedRowIsStillTheNextRow()
        {
            using (var editor = NewEditor("a = 1\nb = 2\n"))
            {
                editor.AnnotationSetLines(0, 1);
                Application.DoEvents();

                int lh = editor.LineHeightPixels;
                Assert.AreEqual(editor.PointYFromPosition(0) + lh, editor.PointYFromAnnotationRow(0, 0),
                    "unwrapped behaviour is unchanged: the reserved row is the very next one");
            }
        }
    }
}
