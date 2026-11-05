using System;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class DiagramCanvasDrillUpButtonTests
    {
        private sealed class TestCanvas : DiagramCanvas
        {
            public void ClickAt(int x, int y)
            {
                OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
                OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
            }
        }

        [TestMethod]
        public void VisibleDrillUpButton_RaisesRequest_WhenClicked()
        {
            using (var canvas = new TestCanvas())
            {
                int requests = 0;
                canvas.DrillUpRequested += delegate { requests++; };
                canvas.ShowDrillUpButton = true;

                canvas.ClickAt(20, 20);

                Assert.AreEqual(1, requests);
            }
        }

        [TestMethod]
        public void HiddenDrillUpButton_DoesNotCaptureSameCanvasPoint()
        {
            using (var canvas = new TestCanvas())
            {
                int requests = 0;
                canvas.DrillUpRequested += delegate { requests++; };
                canvas.ShowDrillUpButton = false;

                canvas.ClickAt(20, 20);

                Assert.AreEqual(0, requests);
            }
        }
    }
}
