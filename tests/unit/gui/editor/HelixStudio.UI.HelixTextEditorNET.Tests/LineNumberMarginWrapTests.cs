using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixTextEditorNET;

namespace HelixPythonNamespace.UI.HelixTextEditorNET.Tests
{
    /// <summary>
    /// The line-number margin under word wrap.
    ///
    /// A wrapped document line occupies several display rows but is still ONE line, so exactly one
    /// number belongs to it — on its first row. The margin loop used to advance one display row per
    /// DOCUMENT line while the text area advanced one per WRAP row, so a line wrapped into three
    /// rows was labelled 7, 8, 9 down its own rows and every number below it sat against the wrong
    /// text.
    ///
    /// Asserted on real painted pixels rather than on an internal counter: the defect was visual,
    /// and a count that agrees with the renderer's own bookkeeping would have "passed" throughout.
    /// </summary>
    [TestClass]
    public class LineNumberMarginWrapTests
    {
        private const int MarginWidth = 40;

        private const string LongLine =
            "run-args = \"../../pipeline_output_fullschema/training_data/culled_id30.tsv "
            + "../../pipeline_output_ide/models/sample_project --epochs 2\"";

        private static Scintilla NewEditor(string text, int width = 380)
        {
            var editor = new Scintilla { Width = width, Height = 300 };
            var _ = editor.Handle;              // realise so measurement and scrollbars are live
            editor.Margins[0].Type = MarginType.Number;
            editor.Margins[0].Width = MarginWidth;
            editor.Text = text;
            return editor;
        }

        /// <summary>
        /// Display rows whose line-number margin has any ink on it — i.e. the rows that carry a
        /// number. Paints through the control's own OnPaint so this is the same output the user
        /// sees, not a re-implementation of the layout.
        /// </summary>
        private static List<int> RowsCarryingANumber(Scintilla editor)
        {
            var rows = new List<int>();
            int lineHeight = editor.LineHeightPixels;
            int rowsOnScreen = editor.LinesOnScreen;

            using (var bmp = new Bitmap(editor.Width, editor.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    MethodInfo onPaint = typeof(Scintilla).GetMethod(
                        "OnPaint", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(onPaint, "Scintilla.OnPaint expected");
                    onPaint.Invoke(editor, new object[] {
                        new PaintEventArgs(g, new Rectangle(0, 0, editor.Width, editor.Height)) });
                }

                // Numbers are right-aligned in the margin, so the top-left pixel is always bare
                // margin background — the reference colour for "no ink here".
                Color background = bmp.GetPixel(1, 1);

                for (int row = 0; row < rowsOnScreen; row++)
                {
                    bool inked = false;
                    for (int y = row * lineHeight; y < (row + 1) * lineHeight && !inked; y++)
                    {
                        if (y < 0 || y >= bmp.Height) break;
                        for (int x = 0; x < MarginWidth && !inked; x++)
                            if (bmp.GetPixel(x, y) != background) inked = true;
                    }
                    if (inked) rows.Add(row);
                }
            }
            return rows;
        }

        [TestMethod]
        public void AWrappedLines_ContinuationRows_CarryNoLineNumber()
        {
            // The line must be followed by another one: a lone wrapped line looks correct even
            // when the margin is broken, because there is no next number to march up its rows.
            using (var editor = NewEditor(LongLine + "\nnext = 2\n"))
            {
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                int lineHeight = editor.LineHeightPixels;
                int firstRowOfLine2 = editor.PointYFromPosition(editor.Text.IndexOf("next = 2")) / lineHeight;
                Assert.IsTrue(firstRowOfLine2 >= 2,
                    "precondition: line 1 must actually occupy several rows; it occupied "
                    + firstRowOfLine2);

                List<int> numbered = RowsCarryingANumber(editor);
                Assert.AreEqual(0, numbered[0], "line 1's number belongs on its first row");
                foreach (int row in numbered)
                {
                    Assert.IsTrue(row == 0 || row >= firstRowOfLine2,
                        "row " + row + " is a continuation row of the wrapped line 1 and must carry "
                        + "no number, but the margin painted one there — numbered rows were ["
                        + string.Join(",", numbered.ConvertAll(r => r.ToString()).ToArray())
                        + "] and line 2 starts at row " + firstRowOfLine2);
                }
            }
        }

        [TestMethod]
        public void NumbersAfterAWrappedLine_StayLevelWithTheirOwnText()
        {
            // A short line, the long wrapping one, then a short line: the third number must skip
            // past every row the middle line occupies, not sit one row below it.
            using (var editor = NewEditor("first = 1\n" + LongLine + "\nlast = 3\n"))
            {
                editor.WrapMode = WrapMode.Word;
                Application.DoEvents();

                List<int> numbered = RowsCarryingANumber(editor);
                Assert.AreEqual(4, numbered.Count,
                    "four document lines (the trailing newline makes a fourth), four numbers");
                Assert.AreEqual(0, numbered[0]);

                // The renderer's own answer for where line 3's text is drawn. Margin and text come
                // from one row walk now, so the number must land on exactly that row.
                int lastLineStart = editor.Text.IndexOf("last = 3");
                int textRow = editor.PointYFromPosition(lastLineStart) / editor.LineHeightPixels;
                Assert.AreEqual(textRow, numbered[2],
                    "the number for `last = 3` must be level with the row its text is painted on");
                Assert.IsTrue(numbered[2] > 2,
                    "the wrapped line must have pushed it down by more than one row; got row "
                    + numbered[2]);
            }
        }

        [TestMethod]
        public void UnwrappedText_IsUnaffected()
        {
            using (var editor = NewEditor("a = 1\nb = 2\nc = 3"))
            {
                CollectionAssert.AreEqual(new List<int> { 0, 1, 2 }, RowsCarryingANumber(editor),
                    "with wrap off, one number per row, exactly as before");
            }
        }
    }
}
