using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Rendering;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 288: pins the ZoomToFit contract — after fitting, the content's centre must land at the
    /// viewport's centre. Used to isolate whether the "sparse diagram sits top-left on load" symptom
    /// is a centring-math bug (it is not) versus a sequencing/stale-content problem upstream.
    /// </summary>
    [TestClass]
    public class ViewportZoomToFitCenteringTests
    {
        [TestMethod]
        public void ZoomToFit_CentresContentInTheViewport()
        {
            var vp = new ViewportTransform();
            vp.ViewportSize = new SizeF(1000f, 800f);

            var content = new RectangleF(100f, 100f, 200f, 120f);
            vp.ZoomToFit(content, 50f);

            var contentCentreWorld = new PointF(content.X + content.Width / 2f, content.Y + content.Height / 2f);
            PointF screen = vp.WorldToScreen(contentCentreWorld);

            Assert.AreEqual(500f, screen.X, 1f, "content centre X should map to viewport centre");
            Assert.AreEqual(400f, screen.Y, 1f, "content centre Y should map to viewport centre");
        }

        [TestMethod]
        public void ZoomToFit_MaxZoomCap_KeepsSparseContentNaturalSize_AndCentred()
        {
            var vp = new ViewportTransform();
            vp.ViewportSize = new SizeF(1000f, 800f);

            // A small node in a big viewport would otherwise zoom way in to fill it.
            var content = new RectangleF(100f, 100f, 100f, 80f);
            vp.ZoomToFit(content, 50f, 1.0f);

            Assert.IsTrue(vp.Zoom <= 1.0f + 1e-4f,
                "an on-load fit must not enlarge sparse content past natural size (100%)");

            // Still centred, just not enlarged.
            var centreWorld = new PointF(content.X + content.Width / 2f, content.Y + content.Height / 2f);
            PointF screen = vp.WorldToScreen(centreWorld);
            Assert.AreEqual(500f, screen.X, 1f, "capped fit must still centre X");
            Assert.AreEqual(400f, screen.Y, 1f, "capped fit must still centre Y");
        }

        [TestMethod]
        public void ZoomToFit_WithoutCap_StillEnlargesToFill()
        {
            // The manual "Zoom to Fit" (default overload) should still zoom IN to fill for small content.
            var vp = new ViewportTransform();
            vp.ViewportSize = new SizeF(1000f, 800f);
            vp.ZoomToFit(new RectangleF(100f, 100f, 100f, 80f), 50f);
            Assert.IsTrue(vp.Zoom > 1.0f, "uncapped fit should enlarge small content to fill the viewport");
        }

        /// <summary>
        /// Iter 356: drilling into a project with hundreds of namespace boxes fit-all'd them to ~11%
        /// zoom, where every label is unreadable. The automatic fit now floors the zoom and, because
        /// the content no longer fits, anchors the top-left corner instead of centring off-screen.
        /// </summary>
        [TestMethod]
        public void ZoomToFit_HugeGrid_StopsAtTheReadableFloor_AndAnchorsTopLeft()
        {
            var vp = new ViewportTransform();
            vp.ViewportSize = new SizeF(1000f, 800f);

            // Content far larger than the viewport: an un-floored fit would need ~0.07 zoom.
            var content = new RectangleF(0f, 0f, 12000f, 9000f);
            vp.ZoomToFit(content, 80f, 1.0f, 0.40f);

            Assert.AreEqual(0.40f, vp.Zoom, 1e-4f, "must stop at the readable floor, not shrink to fit-all");

            // The content's top-left corner sits at the padding offset (anchored), not centred.
            PointF topLeft = vp.WorldToScreen(new PointF(content.X, content.Y));
            Assert.AreEqual(80f, topLeft.X, 1f, "top-left X anchored at the padding");
            Assert.AreEqual(80f, topLeft.Y, 1f, "top-left Y anchored at the padding");
        }

        /// <summary>When the content DOES fit above the floor, the floor overload still centres.</summary>
        [TestMethod]
        public void ZoomToFit_WithFloor_ButContentFits_StillCentres()
        {
            var vp = new ViewportTransform();
            vp.ViewportSize = new SizeF(1000f, 800f);

            var content = new RectangleF(100f, 100f, 400f, 300f);
            vp.ZoomToFit(content, 50f, 1.0f, 0.40f);

            var centreWorld = new PointF(content.X + content.Width / 2f, content.Y + content.Height / 2f);
            PointF screen = vp.WorldToScreen(centreWorld);
            Assert.AreEqual(500f, screen.X, 1f, "content that fits is still centred X");
            Assert.AreEqual(400f, screen.Y, 1f, "content that fits is still centred Y");
        }
    }
}
