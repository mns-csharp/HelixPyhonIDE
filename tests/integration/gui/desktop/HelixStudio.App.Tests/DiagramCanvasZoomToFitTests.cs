using System.Drawing;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.UI.Rendering;
using HelixPythonNamespace.UI.Rendering.Styles;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 287: DiagramCanvas.ZoomToFit() computed its fit against whatever viewport size a prior
    /// OnResize happened to leave behind, so calling it before the canvas reached its final size
    /// centred against a stale viewport. ZoomToFit now refreshes the viewport size from the current
    /// client area first (matching OnResize and the CUDA designer), making it self-sufficient.
    /// </summary>
    [TestClass]
    public class DiagramCanvasZoomToFitTests
    {
        [TestMethod]
        public void ZoomToFit_RefreshesViewportSize_FromCurrentClientArea()
        {
            using (var canvas = new DiagramCanvas())
            {
                canvas.Size = new Size(800, 600);

                // Simulate a stale viewport (as if ZoomToFit ran before the real resize settled).
                canvas.Viewport.ViewportSize = new SizeF(10f, 10f);

                canvas.ZoomToFit();

                // ZoomToFit must have re-read the true client area (800x600 minus the scrollbars),
                // not kept the stale 10x10.
                Assert.IsTrue(canvas.Viewport.ViewportSize.Width > 100f,
                    "ZoomToFit should refresh the viewport width from the current client area");
                Assert.IsTrue(canvas.Viewport.ViewportSize.Height > 100f,
                    "ZoomToFit should refresh the viewport height from the current client area");
            }
        }

        // Iter 289: node bounds are only measured at first render, so a fit requested on freshly-built
        // content (load, drill-in navigation, category rebuilds) must DEFER to the first paint rather
        // than centre a zero-area rect and strand the diagram top-left.
        [TestMethod]
        public void ZoomToFit_DefersToFirstPaint_WhenNodesArentMeasuredYet()
        {
            using (var canvas = new DiagramCanvas())
            {
                canvas.Size = new Size(800, 600);
                // A node exists but its bounds are still zero-area (unmeasured).
                canvas.SceneGraph.AddNode(new NodeVisual("n1", new RectangleF(10f, 10f, 0f, 0f), new ConcreteClassNodeStyle()));

                canvas.ZoomToFit(80f);

                Assert.IsTrue(FitPending(canvas),
                    "unmeasured content must defer the fit to the first paint");
            }
        }

        [TestMethod]
        public void ZoomToFit_FitsImmediately_WhenContentIsMeasured()
        {
            using (var canvas = new DiagramCanvas())
            {
                canvas.Size = new Size(800, 600);
                canvas.SceneGraph.AddNode(new NodeVisual("n1", new RectangleF(100f, 100f, 200f, 120f), new ConcreteClassNodeStyle()));

                canvas.ZoomToFit(80f);

                Assert.IsFalse(FitPending(canvas),
                    "measured content should fit immediately, not defer");
            }
        }

        private static bool FitPending(DiagramCanvas canvas)
        {
            FieldInfo f = typeof(DiagramCanvas).GetField(
                "_fitOnFirstPaint", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_fitOnFirstPaint field expected");
            return (bool)f.GetValue(canvas);
        }
    }
}
