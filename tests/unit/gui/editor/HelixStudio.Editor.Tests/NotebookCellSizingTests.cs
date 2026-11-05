using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// How tall a code box is, who decides, and whether clicking in one changes it.
    ///
    /// <para>
    /// Four reported problems, all about the same rectangle: a new cell was one line tall, there
    /// was no way to make it bigger, a size the user chose was forgotten on reload, and clicking
    /// into a cell changed its shape — the embedded editor brought three margins and two permanent
    /// scrollbars with it, so the box grew a grey block and a scroll stripe and its text jumped
    /// sideways under the caret.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookCellSizingTests
    {
        private const int Width = 1000;
        private const int Height = 700;

        private static NotebookViewControl Realized(NotebookDocument document)
        {
            var view = new NotebookViewControl { Bounds = new Rectangle(0, 0, Width, Height) };
            view.CreateControl();
            view.Document = document;
            return view;
        }

        private static NotebookDocument WithCells(params string[] sources)
        {
            var document = new NotebookDocument();
            foreach (string source in sources) document.InsertCell(document.Cells.Count, NotebookCellKind.Code, source);
            return document;
        }

        /// <summary>
        /// Raise the control's own mouse handlers. Reflection rather than a test-only shortcut
        /// method on the control: the point is to drive the SAME <c>OnMouseDown</c> /
        /// <c>OnMouseMove</c> / <c>OnMouseUp</c> a real click reaches, and a production hook added
        /// for tests would be a second path that can drift from the first.
        /// </summary>
        private static void Mouse(NotebookViewControl view, string handler, Point at)
        {
            var args = new MouseEventArgs(MouseButtons.Left, 1, at.X, at.Y, 0);
            typeof(NotebookViewControl)
                .GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(view, new object[] { args });
        }

        private static NotebookStyle Style(NotebookViewControl view)
        {
            return (NotebookStyle)typeof(NotebookViewControl)
                .GetField("_style", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(view);
        }

        // ---- 1. a new blank cell has room to type in -------------------------------------------

        [TestMethod]
        public void ANewBlankCodeCellIsAtLeastFiveLinesTall()
        {
            using (NotebookViewControl view = Realized(WithCells(string.Empty)))
            {
                Assert.AreEqual(Style(view).MinimumCodeLines, view.Layouts[0].DisplayLines,
                    "an empty cell one line tall is a target to aim at, not a place to write code");
                Assert.IsTrue(Style(view).MinimumCodeLines >= 5, "the reported minimum is 5 lines");
            }
        }

        /// <summary>The floor must not shrink a cell that has more code than that.</summary>
        [TestMethod]
        public void ACellWithMoreCodeThanTheMinimumIsAsTallAsItsCode()
        {
            string eight = string.Join("\n", new string[8]) + "\nx = 1";
            using (NotebookViewControl view = Realized(WithCells(eight)))
            {
                Assert.AreEqual(9, view.Layouts[0].DisplayLines);
            }
        }

        // ---- 2 & 3. drag to resize, and the size sticks ----------------------------------------

        /// <summary>
        /// The grip is in the card's bottom-right corner and dragging it down makes the box taller.
        /// Driven through the control's own mouse handlers, because a test that called a resize
        /// method directly would pass with the grip unreachable.
        /// </summary>
        [TestMethod]
        public void DraggingTheBottomRightCornerMakesTheCellTaller()
        {
            using (NotebookViewControl view = Realized(WithCells(string.Empty)))
            {
                int before = view.Layouts[0].DisplayLines;
                Rectangle grip = view.Layouts[0].ResizeGrip;
                Assert.IsFalse(grip.IsEmpty, "a code cell must have a resize grip");

                Point start = new Point(grip.Left + grip.Width / 2 + view.AutoScrollPosition.X,
                                        grip.Top + grip.Height / 2 + view.AutoScrollPosition.Y);
                int fourLines = Style(view).CodeLineHeight * 4;

                Mouse(view, "OnMouseDown", start);
                Mouse(view, "OnMouseMove", new Point(start.X, start.Y + fourLines));
                Mouse(view, "OnMouseUp", new Point(start.X, start.Y + fourLines));

                Assert.AreEqual(before + 4, view.Layouts[0].DisplayLines,
                    "four line-heights of drag should be four more lines");
            }
        }

        /// <summary>Dragging up cannot take a cell below the minimum, or the grip vanishes with it.</summary>
        [TestMethod]
        public void DraggingUpStopsAtTheMinimum()
        {
            using (NotebookViewControl view = Realized(WithCells(string.Empty)))
            {
                Rectangle grip = view.Layouts[0].ResizeGrip;
                Point start = new Point(grip.Left + grip.Width / 2 + view.AutoScrollPosition.X,
                                        grip.Top + grip.Height / 2 + view.AutoScrollPosition.Y);

                Mouse(view, "OnMouseDown", start);
                Mouse(view, "OnMouseMove", new Point(start.X, start.Y - Style(view).CodeLineHeight * 20));
                Mouse(view, "OnMouseUp", new Point(start.X, start.Y - Style(view).CodeLineHeight * 20));

                Assert.AreEqual(Style(view).MinimumCodeLines, view.Layouts[0].DisplayLines);
            }
        }

        /// <summary>
        /// The height a user chose survives a save and a reload — through the real writer and
        /// reader, because that is the only thing that proves it reaches the file. It rides in the
        /// cell's own metadata under a namespaced key, so Jupyter and Colab carry it through
        /// untouched rather than dropping it.
        /// </summary>
        [TestMethod]
        public void AResizedCellRemembersItsHeightAcrossSaveAndReload()
        {
            string directory = Path.Combine(Path.GetTempPath(), "helix-cellsize-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "Untitled.ipynb");
                NotebookDocument document = NotebookFactory.CreateNew("3.11");
                document.Cells[0].HeightInLines = 12;
                NotebookWriter.WriteFile(document, path);

                NotebookReadResult reopened = NotebookReader.ReadFile(path);
                Assert.IsTrue(reopened.Succeeded, reopened.Error);
                Assert.AreEqual(12, reopened.Document.Cells[0].HeightInLines);

                using (NotebookViewControl view = Realized(reopened.Document))
                    Assert.AreEqual(12, view.Layouts[0].DisplayLines, "the reopened box must be the size it was left");
            }
            finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
        }

        /// <summary>
        /// A cell nobody resized must gain NO metadata key. This is the byte-identical rule one
        /// level down: writing `height_lines: 5` into every cell of somebody's notebook because
        /// they opened it in this IDE would put a diff on every cell of a file they did not edit.
        /// </summary>
        [TestMethod]
        public void ACellNobodyResizedGainsNoHeightMetadata()
        {
            NotebookDocument document = NotebookFactory.CreateNew("3.11");
            Assert.AreEqual(0, document.Cells[0].HeightInLines, "0 means 'as tall as it needs'");
            Assert.IsFalse(NotebookWriter.Write(document).Contains("height_lines"));
        }

        [TestMethod]
        public void StandardOutputHasADistinctResultSurface()
        {
            NotebookDocument document = WithCells("print('hello')\n");
            document.Cells[0].Outputs.Add(new NotebookOutput
            {
                Kind = NotebookOutputKind.Stream,
                StreamName = "stdout",
                Text = "hello\n",
            });

            using (NotebookViewControl view = Realized(document))
            using (Bitmap image = Paint(view))
            {
                NotebookViewControl.CellLayout layout = view.Layouts[0];
                NotebookStyle style = Style(view);
                Point outputBackground = new Point(layout.OutputBounds.Right - 20,
                    layout.OutputBounds.Top + style.OutputPadding + 2);
                Point sourceBackground = new Point(layout.SourceBounds.Right - 20,
                    layout.SourceBounds.Bottom - 10);

                Assert.AreEqual(style.OutputBackground, image.GetPixel(outputBackground.X, outputBackground.Y),
                    "stdout must sit on a visibly separate result surface");
                Assert.AreNotEqual(image.GetPixel(sourceBackground.X, sourceBackground.Y),
                    image.GetPixel(outputBackground.X, outputBackground.Y),
                    "source and output must not read as one uninterrupted code block");
            }
        }

        /// <summary>Content still wins: a remembered height can never hide a line of code.</summary>
        [TestMethod]
        public void ARememberedHeightNeverHidesCode()
        {
            NotebookDocument document = WithCells(string.Join("\n", new string[20]));
            document.Cells[0].HeightInLines = 6;
            using (NotebookViewControl view = Realized(document))
                Assert.AreEqual(20, view.Layouts[0].DisplayLines);
        }

        // ---- 4 & 5. clicking in must not change the shape --------------------------------------

        /// <summary>
        /// The editor that moves into a cell on a click carries no chrome: no line-number margin
        /// (the cell paints its own), no breakpoint or folding margins, and no scrollbar unless
        /// there is something off screen. Those were 76px of grey plus two permanent bars, and they
        /// are what made the box change shape and the text jump sideways.
        /// </summary>
        [TestMethod]
        public void ClickingIntoACellAddsNoMarginsAndNoScrollbars()
        {
            using (NotebookViewControl view = Realized(WithCells("x = 1\n")))
            {
                view.BeginEdit(0);
                HelixPythonNamespace.Editor.PythonEditorControl editor = view.Editor;
                Assert.IsNotNull(editor, "BeginEdit must place the shared editor");

                Assert.IsTrue(editor.EmbeddedCellMode, "a cell editor is chrome-free");
                Assert.AreEqual(0, editor.Margins[0].Width, "the cell paints its own line numbers");
                Assert.AreEqual(0, editor.Margins[1].Width, "there is nowhere to put a breakpoint in a cell");
                Assert.AreEqual(0, editor.Margins[2].Width, "there is no folding to do in six lines");
                Assert.AreEqual(HelixTextEditorNET.ScrollBarVisibility.WhenNeeded, editor.HScrollBarVisibility);
                Assert.AreEqual(HelixTextEditorNET.ScrollBarVisibility.WhenNeeded, editor.VScrollBarVisibility);
                Assert.AreEqual(HelixTextEditorNET.EdgeMode.None, editor.EdgeMode,
                    "the 120-column guide is a ruler for a document window, a hairline in a cell");
                Assert.AreEqual(Style(view).CellBackground, editor.PageBackColor,
                    "a white block inset in a tinted card is still the box changing under the click");
            }
        }

        /// <summary>
        /// The editor lands exactly on the painted text area, so the first character sits where it
        /// was a moment ago. Any margin the editor drew would push it right; any change of bounds
        /// would move the box.
        /// </summary>
        [TestMethod]
        public void TheEditorLandsExactlyOnThePaintedTextArea()
        {
            using (NotebookViewControl view = Realized(WithCells("x = 1\n")))
            {
                Rectangle painted = view.Layouts[0].SourceBounds;
                view.BeginEdit(0);

                Rectangle editor = view.Editor.Bounds;
                Assert.AreEqual(painted.Left + view.AutoScrollPosition.X, editor.Left, "text must not shift sideways");
                Assert.AreEqual(painted.Top + view.AutoScrollPosition.Y, editor.Top);
                Assert.AreEqual(painted.Width, editor.Width);
            }
        }

        /// <summary>
        /// The line numbers stay on screen while the cell is being edited.
        ///
        /// <para>
        /// They live in their own strip to the LEFT of the text area, so the editor never covers
        /// them — but the painter used to skip the whole source block for the cell being edited,
        /// because the editor drew its own numbers in its own margin. Removing that margin (half of
        /// why a cell changed shape on a click) left an edited cell with no numbers at all. Caught
        /// in a screenshot, pinned here.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TheLineNumbersStayVisibleWhileTheCellIsEdited()
        {
            using (NotebookViewControl view = Realized(WithCells("x = 1\ny = 2\n")))
            {
                Rectangle numbers = NumberStrip(view);
                using (var idle = Paint(view))
                {
                    int inkIdle = Ink(idle, numbers);
                    Assert.IsTrue(inkIdle > 0, "no line numbers painted at rest");

                    view.BeginEdit(0);
                    using (var editing = Paint(view))
                        Assert.IsTrue(Ink(editing, numbers) > 0,
                            "clicking into the cell erased its line numbers");
                }
            }
        }

        [TestMethod]
        public void InactiveCellsKeepSyntaxColourAndTypeInlays()
        {
            using (NotebookViewControl view = Realized(WithCells(
                "value = 42\nprint(value)",
                "import pandas as pd\nframe = pd.DataFrame()")))
            {
                view.BeginEdit(1);

                Assert.AreEqual("int", view.Layouts[0].TypeMarkers[0],
                    "moving the shared editor to another cell must not remove this cell's inlay");

                char tripleQuote = '\0';
                var tokens = PythonPreviewColouriser.Colour("import pandas as pd", Style(view), ref tripleQuote);
                Assert.IsTrue(tokens.Exists(token => token.Start == 0 && token.Color == Style(view).Keyword),
                    "inactive cells must retain Python keyword colouring");

                Assert.AreEqual(view.Editor.Styles[HelixTextEditorNET.Style.Default].Size,
                    (int)Style(view).CodeFontSize,
                    "the painted cell and live editor must use the same code size");
                Assert.AreEqual(view.Editor.Styles[HelixTextEditorNET.Style.Python.Word].ForeColor,
                    Style(view).Keyword);
                Assert.AreEqual(view.Editor.Styles[HelixTextEditorNET.Style.Python.Number].ForeColor,
                    Style(view).Number);
                Assert.IsTrue(tokens.Exists(token => token.Start == 0 &&
                    token.FontStyle == FontStyle.Bold),
                    "keywords must keep the editor's bold weight when the cell loses focus");
            }
        }

        [TestMethod]
        public void ScrolledCodeTextStaysInsideItsCell()
        {
            string[] withText = new string[9];
            string[] withoutText = new string[9];
            for (int i = 0; i < withText.Length; i++)
            {
                withText[i] = i == 8 ? "WWWWWW" : string.Empty;
                withoutText[i] = string.Empty;
            }

            using (NotebookViewControl textView = Realized(WithCells(withText)))
            using (NotebookViewControl blankView = Realized(WithCells(withoutText)))
            {
                int scrollY = textView.Layouts[8].Bounds.Top - 40;
                textView.AutoScrollPosition = new Point(0, scrollY);
                blankView.AutoScrollPosition = new Point(0, scrollY);

                Rectangle expected = textView.Layouts[8].SourceBounds;
                expected.Offset(textView.AutoScrollPosition.X, textView.AutoScrollPosition.Y);
                expected.Height = Style(textView).CodeLineHeight;

                using (Bitmap paintedText = Paint(textView))
                using (Bitmap paintedBlank = Paint(blankView))
                {
                    int changed = 0;
                    for (int y = expected.Top; y < expected.Bottom; y++)
                        for (int x = expected.Left; x < expected.Right; x++)
                            if (paintedText.GetPixel(x, y) != paintedBlank.GetPixel(x, y)) changed++;

                    Assert.IsTrue(changed > 0,
                        "after scrolling, source text was not painted inside the visible cell");
                }
            }
        }

        /// <summary>The strip between the card's padding and the text area, where numbers go.</summary>
        private static Rectangle NumberStrip(NotebookViewControl view)
        {
            NotebookViewControl.CellLayout layout = view.Layouts[0];
            int width = Style(view).LineNumberWidth;
            return new Rectangle(layout.SourceBounds.Left - width + view.AutoScrollPosition.X,
                                 layout.SourceBounds.Top + view.AutoScrollPosition.Y,
                                 width, Style(view).CodeLineHeight * 2);
        }

        private static Bitmap Paint(NotebookViewControl view)
        {
            var canvas = new Bitmap(Width, Height);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                var clip = new Rectangle(0, 0, Width, Height);
                g.SetClip(clip);
                typeof(NotebookViewControl)
                    .GetMethod("OnPaint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(view, new object[] { new PaintEventArgs(g, clip) });
            }
            return canvas;
        }

        private static int Ink(Bitmap image, Rectangle region)
        {
            Color background = image.GetPixel(region.Left, region.Top);
            int ink = 0;
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                    if (image.GetPixel(x, y) != background) ink++;
            return ink;
        }

        /// <summary>
        /// And the card itself is the same rectangle before and after the click. This is the
        /// screenshot comparison the report was made from, as an assertion.
        /// </summary>
        [TestMethod]
        public void TheCardIsTheSameRectangleBeforeAndAfterClicking()
        {
            using (NotebookViewControl view = Realized(WithCells("x = 1\n")))
            {
                Rectangle before = view.Layouts[0].Bounds;
                view.BeginEdit(0);
                Assert.AreEqual(before, view.Layouts[0].Bounds, "clicking a cell must not resize it");
            }
        }
    }
}
