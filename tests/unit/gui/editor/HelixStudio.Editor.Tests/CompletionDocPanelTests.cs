using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CompletionDocPanelTests
    {
        [TestMethod]
        public void PlacementNearRightEdge_DoesNotCoverAutocompleteList()
        {
            var workArea = new Rectangle(0, 0, 1280, 960);
            var list = new Rectangle(680, 176, 160, 48);
            var docSize = new Size(600, 400);

            Point location = CompletionDocPanel.CalculateLocation(
                workArea, docSize, new Point(980, 200), list);

            var doc = new Rectangle(location, docSize);
            Assert.IsTrue(workArea.Contains(doc));
            Assert.IsFalse(doc.IntersectsWith(list),
                "documentation must never obscure the autocomplete candidates");
        }

        [TestMethod]
        public void PlacementUsesRightSide_WhenThereIsRoom()
        {
            var workArea = new Rectangle(0, 0, 1600, 1000);
            var list = new Rectangle(300, 200, 180, 100);
            var docSize = new Size(600, 400);

            Point location = CompletionDocPanel.CalculateLocation(
                workArea, docSize, new Point(300, 220), list);

            Assert.IsTrue(location.X > list.Right);
            Assert.IsFalse(new Rectangle(location, docSize).IntersectsWith(list));
        }

        [TestMethod]
        public void ShortDocumentation_UsesCompactPanelInsteadOfMaximumCanvas()
        {
            Rectangle workArea = new Rectangle(0, 0, 1280, 960);
            Size size = CompletionDocPanel.CalculateDesiredSize(
                "__init__", "__init__()", SystemFonts.MessageBoxFont,
                SystemFonts.MessageBoxFont, workArea);

            Assert.IsTrue(size.Width <= 260, "short documentation should remain compact");
            Assert.IsTrue(size.Height <= 100, "short documentation should not create a tall blank panel");
        }

        [TestMethod]
        public void LongDocumentation_IsCappedToWorkingAreaLimits()
        {
            Rectangle workArea = new Rectangle(0, 0, 1280, 960);
            string body = new string('x', 5000);
            Size size = CompletionDocPanel.CalculateDesiredSize(
                "large_item", body, SystemFonts.MessageBoxFont,
                SystemFonts.MessageBoxFont, workArea);

            Assert.IsTrue(size.Width <= 600);
            Assert.IsTrue(size.Height <= 400);
        }
    }
}
