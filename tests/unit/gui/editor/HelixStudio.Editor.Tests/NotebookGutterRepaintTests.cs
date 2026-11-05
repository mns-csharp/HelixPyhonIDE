using System;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The run button erasing itself twice a second while a cell runs — reported as "the execution
    /// buttons flicker".
    ///
    /// <para>
    /// <b>The defect.</b> <c>LayoutCell</c> sets <c>layout.Bounds</c> to the CARD, starting at
    /// <c>_contentLeft + GutterWidth</c>; the gutter — the <c>[*]</c>, the round run button and the
    /// live timer — sits entirely to its left. <c>OnPaint</c> then skips a cell whose
    /// <c>Bounds</c> does not intersect the clip, while the running-cell ticker invalidates exactly
    /// <c>[_contentLeft, _contentLeft + GutterWidth)</c>. <c>Rectangle.IntersectsWith</c> is
    /// exclusive on the right edge, so that rectangle never intersects the card it belongs to.
    /// Every tick therefore ran <c>g.Clear(background)</c> over the gutter and then painted
    /// nothing: the button was wiped and only came back on the next unrelated full repaint.
    /// </para>
    ///
    /// <para>
    /// These paint through the real <c>OnPaint</c> with a real clip rectangle rather than through
    /// <c>DrawToBitmap</c>, because <c>DrawToBitmap</c> always paints the whole control and would
    /// pass while the partial-clip path stayed broken — which is the only path that runs while a
    /// cell is executing.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookGutterRepaintTests
    {
        private const int Width = 1000;
        private const int Height = 600;

        private static NotebookDocument RunningNotebook()
        {
            NotebookDocument document = NotebookFactory.CreateNew("3.11");
            document.Cells[0].Source = "import numpy as np\nprint(np.__version__)\n";
            document.Cells[0].IsRunning = true;
            // RunStartedUtc deliberately left null, so TimingText draws nothing. The live counter
            // is real, moving content — two paints taken a moment apart legitimately differ by the
            // digits, and that noise would mask the thing under test, which is whether the BUTTON
            // survives a repaint. The counter has its own test below.
            return document;
        }

        private static NotebookViewControl Realized(NotebookDocument document)
        {
            var view = new NotebookViewControl { Bounds = new Rectangle(0, 0, Width, Height) };
            view.CreateControl();
            view.Document = document;
            return view;
        }

        /// <summary>Run the control's own OnPaint over <paramref name="clip"/>, onto <paramref name="canvas"/>.</summary>
        private static void PaintWithClip(NotebookViewControl view, Bitmap canvas, Rectangle clip)
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SetClip(clip);
                var args = new PaintEventArgs(g, clip);
                typeof(NotebookViewControl)
                    .GetMethod("OnPaint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(view, new object[] { args });
            }
        }

        /// <summary>The gutter strip the running-cell ticker invalidates, in client coordinates.</summary>
        private static Rectangle GutterStrip(NotebookViewControl view)
        {
            NotebookViewControl.CellLayout layout = view.Layouts[0];
            int contentLeft = (int)typeof(NotebookViewControl)
                .GetField("_contentLeft", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(view);
            int gutterWidth = layout.Bounds.Left - contentLeft;
            Assert.IsTrue(gutterWidth > 0, "the gutter should sit left of the card");
            return new Rectangle(contentLeft + view.AutoScrollPosition.X,
                                 layout.Bounds.Top + view.AutoScrollPosition.Y,
                                 gutterWidth, layout.Bounds.Height);
        }

        /// <summary>Pixels that are not the page background — the button's actual ink.</summary>
        private static int InkPixels(Bitmap image, Rectangle region)
        {
            Color background = image.GetPixel(region.Left, region.Top);
            int ink = 0;
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                    if (image.GetPixel(x, y) != background) ink++;
            return ink;
        }

        private static int DifferingPixels(Bitmap a, Bitmap b, Rectangle region)
        {
            int differing = 0;
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                    if (a.GetPixel(x, y) != b.GetPixel(x, y)) differing++;
            return differing;
        }

        /// <summary>
        /// The whole bug in one assertion: repaint just the gutter, and what is on screen there
        /// must not change. Before the fix the run button vanished — a clear to the page
        /// background and no cell painted over it.
        /// </summary>
        [TestMethod]
        public void RepaintingOnlyTheGutter_LeavesTheRunButtonOnScreen()
        {
            using (NotebookViewControl view = Realized(RunningNotebook()))
            using (var full = new Bitmap(Width, Height))
            using (var partial = new Bitmap(Width, Height))
            {
                Rectangle whole = new Rectangle(0, 0, Width, Height);
                PaintWithClip(view, full, whole);

                // What the screen holds before the tick, then the tick's own partial repaint over it.
                using (Graphics g = Graphics.FromImage(partial)) g.DrawImage(full, 0, 0);
                Rectangle gutter = GutterStrip(view);
                PaintWithClip(view, partial, gutter);

                int changed = DifferingPixels(full, partial, gutter);
                Assert.AreEqual(0, changed,
                    changed + " of " + (gutter.Width * gutter.Height) + " gutter pixels changed when "
                    + "nothing about the cell did — that is the button being erased twice a second");
            }
        }

        /// <summary>
        /// The same claim stated positively, so the test above cannot pass by painting nothing at
        /// all: the gutter really does contain the run button, and the partial repaint really does
        /// draw it. Compared against an empty canvas, both passes must put ink in the same place.
        /// </summary>
        [TestMethod]
        public void ThePartialRepaintActuallyDrawsTheButton()
        {
            using (NotebookViewControl view = Realized(RunningNotebook()))
            using (var full = new Bitmap(Width, Height))
            using (var partial = new Bitmap(Width, Height))
            {
                PaintWithClip(view, full, new Rectangle(0, 0, Width, Height));
                Rectangle gutter = GutterStrip(view);
                PaintWithClip(view, partial, gutter);

                NotebookViewControl.CellLayout layout = view.Layouts[0];
                Assert.IsFalse(layout.RunButton.IsEmpty, "a code cell must have a run button");

                Rectangle ring = layout.RunButton;
                ring.Offset(view.AutoScrollPosition.X, view.AutoScrollPosition.Y);
                Assert.IsTrue(gutter.Contains(ring), "the run button should be inside the invalidated strip");

                // Ink, not merely "different from an empty bitmap": g.Clear alone would satisfy
                // that, and g.Clear with nothing painted after it IS the bug.
                Assert.IsTrue(InkPixels(full, ring) > 0, "the full paint drew no button");
                Assert.AreEqual(InkPixels(full, ring), InkPixels(partial, ring),
                    "the gutter-only paint must draw the same button the full paint does");
            }
        }

        /// <summary>
        /// The ticker must also stop asking for repaints once the value it shows stops changing:
        /// <c>TimingText</c> rounds to whole seconds, so at 2 Hz half the repaints redrew an
        /// identical gutter. Two ticks inside the same second must produce one invalidation.
        /// </summary>
        [TestMethod]
        public void TwoTicksInsideOneSecond_OnlyRepaintOnce()
        {
            NotebookDocument document = RunningNotebook();
            // Five seconds in: TimingText rounds to whole seconds from 1s onwards, so two ticks
            // half a second apart render the same string. Under 1s it shows milliseconds, which
            // genuinely do change every tick — that window is not what this is about.
            document.Cells[0].RunStartedUtc = DateTime.UtcNow - TimeSpan.FromSeconds(5);

            using (NotebookViewControl view = Realized(document))
            {
                var invalidate = typeof(NotebookViewControl).GetMethod("InvalidateRunningCells",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(invalidate);

                int painted = 0;
                view.Invalidated += delegate { painted++; };

                invalidate.Invoke(view, null);
                int afterFirst = painted;
                invalidate.Invoke(view, null);

                Assert.IsTrue(afterFirst > 0, "the first tick must repaint — the counter just started");
                Assert.AreEqual(afterFirst, painted,
                    "a second tick in the same second redraws an identical gutter");
            }
        }
    }
}
