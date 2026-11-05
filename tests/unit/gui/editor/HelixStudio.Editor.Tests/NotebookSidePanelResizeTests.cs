using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The boundary between the side pane and the notebook page has to be draggable.
    ///
    /// <para>
    /// It was not. The workspace docked a <see cref="Splitter"/> for this and it landed on the
    /// wrong side: measured on a realized form, the rail took 0–40, the splitter 40–44 and the pane
    /// 44–344, so the splitter sat between the RAIL and the pane and its target was the 40 px icon
    /// strip. The edge the user points at had nothing on it. These tests drive the real mouse
    /// handlers a drag reaches, and the first two fail against the old arrangement.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookSidePanelResizeTests
    {
        private const int Width = 1000;
        private const int Height = 700;

        private static T Field<T>(object instance, string name) where T : class
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name + " is gone — the test is out of date, not the layout");
            return field.GetValue(instance) as T;
        }

        private sealed class Harness : IDisposable
        {
            public readonly Form Form;
            public readonly NotebookWorkspace Workspace;
            public readonly NotebookViewControl Page;
            public readonly Panel Resizer;
            public readonly Panel Pane;

            public Harness()
            {
                Page = new NotebookViewControl();
                Workspace = new NotebookWorkspace(Page);
                Workspace.AddPane("modules", "+", new NotebookModulesPanel());

                Form = new Form { ClientSize = new Size(Width, Height) };
                Form.Controls.Add(Workspace);
                TestWindow.ShowOffScreen(Form);
                Workspace.ShowPane("modules");
                Application.DoEvents();

                Pane = Field<Panel>(Workspace, "_sidePanel");
                Resizer = Field<Panel>(Workspace, "_resizer");
            }

            public void Dispose() { Form.Dispose(); }
        }

        /// <summary>
        /// Drive the resizer's own handlers. Reflection rather than a test-only entry point, the
        /// same rule <c>NotebookCellSizingTests</c> follows: the point is to run the very
        /// <c>OnMouseDown</c> / <c>OnMouseMove</c> / <c>OnMouseUp</c> a real drag reaches.
        /// </summary>
        private static void Drag(Control resizer, int deltaX)
        {
            int startClientX = resizer.Width / 2;
            int targetScreenX = resizer.PointToScreen(new Point(startClientX, 8)).X + deltaX;

            Mouse(resizer, "OnMouseDown", startClientX);

            // Re-base against the resizer's CURRENT bounds before each later event. The resizer
            // travels with the pane as it widens, so the same screen position is a different client
            // X after every step — which is exactly what Windows does when it builds the next
            // MouseEventArgs, and a test that skipped it would be reporting a drag nobody performs.
            Mouse(resizer, "OnMouseMove", targetScreenX - resizer.PointToScreen(Point.Empty).X);
            Application.DoEvents();
            Mouse(resizer, "OnMouseUp", targetScreenX - resizer.PointToScreen(Point.Empty).X);
            Application.DoEvents();
        }

        private static void Mouse(Control target, string handler, int x)
        {
            MethodInfo method = null;
            for (Type type = target.GetType(); type != null && method == null; type = type.BaseType)
                method = type.GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic,
                                        null, new[] { typeof(MouseEventArgs) }, null);
            Assert.IsNotNull(method, handler + " not found");
            method.Invoke(target, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, 8, 0) });
        }

        // ------------------------------------------------------------------ where it is

        [TestMethod]
        public void TheResizerSitsOnTheBoundaryBetweenThePaneAndThePage()
        {
            using (var harness = new Harness())
            {
                Rectangle onScreen = harness.Workspace.RectangleToClient(
                    harness.Resizer.RectangleToScreen(harness.Resizer.ClientRectangle));

                Assert.AreEqual(harness.Pane.Right, onScreen.Right,
                    "the resizer must be ON the pane's right edge, not somewhere else in the row");
                Assert.IsTrue(onScreen.Left > harness.Workspace.RailWidth + 100,
                    "it landed at x=" + onScreen.Left + ", which is back at the icon rail — the old bug");
            }
        }

        [TestMethod]
        public void TheBoundaryIsWideEnoughToGrabAndSaysSoWithTheCursor()
        {
            using (var harness = new Harness())
            {
                Assert.IsTrue(harness.Resizer.Width >= 6,
                    "a hit target of " + harness.Resizer.Width + "px is a pixel-hunt");
                Assert.AreEqual(Cursors.VSplit, harness.Resizer.Cursor,
                    "the cursor is the only thing that tells a user the edge is draggable");
            }
        }

        // ------------------------------------------------------------------ what it does

        [TestMethod]
        public void DraggingTheBoundaryRightWidensTheSidePane()
        {
            using (var harness = new Harness())
            {
                int before = harness.Pane.Width;
                Drag(harness.Resizer, 120);

                Assert.AreEqual(before + 120, harness.Pane.Width,
                    "the pane should follow the pointer exactly; it went from "
                    + before + " to " + harness.Pane.Width);
            }
        }

        [TestMethod]
        public void DraggingTheBoundaryLeftNarrowsItAgain()
        {
            using (var harness = new Harness())
            {
                Drag(harness.Resizer, 120);
                int widened = harness.Pane.Width;
                Drag(harness.Resizer, -90);

                Assert.AreEqual(widened - 90, harness.Pane.Width);
            }
        }

        [TestMethod]
        public void ThePageGivesUpExactlyWhatThePaneGains()
        {
            using (var harness = new Harness())
            {
                int pageBefore = harness.Page.Width;
                Drag(harness.Resizer, 100);

                Assert.AreEqual(pageBefore - 100, harness.Page.Width,
                    "the two share one row; whatever one takes the other has to give");
            }
        }

        // ------------------------------------------------------------------ what it refuses

        /// <summary>
        /// A pane dragged over the whole window leaves no notebook and no visible edge to drag
        /// back with — the only way out would be closing the pane, which is not an undo.
        /// </summary>
        [TestMethod]
        public void TheNotebookPageCannotBeDraggedAwayCompletely()
        {
            using (var harness = new Harness())
            {
                Drag(harness.Resizer, 5000);

                Assert.IsTrue(harness.Page.Width >= 200,
                    "the page kept only " + harness.Page.Width + "px; there is nothing left to work in");
                Assert.IsTrue(harness.Pane.Width < Width,
                    "the pane swallowed the window");
            }
        }

        [TestMethod]
        public void TheSidePaneCannotBeDraggedDownToNothing()
        {
            using (var harness = new Harness())
            {
                Drag(harness.Resizer, -5000);

                Assert.IsTrue(harness.Pane.Width >= 160,
                    "the pane collapsed to " + harness.Pane.Width + "px, which reads as a fault");
            }
        }

        // ------------------------------------------------------------------ and remembers

        [TestMethod]
        public void AChosenWidthSurvivesClosingAndReopeningThePane()
        {
            using (var harness = new Harness())
            {
                Drag(harness.Resizer, 140);
                int chosen = harness.Pane.Width;
                Assert.AreNotEqual(300, chosen, "the control: the drag has to have done something");

                harness.Workspace.ClosePane();
                Application.DoEvents();
                harness.Workspace.ShowPane("modules");
                Application.DoEvents();

                Assert.AreEqual(chosen, harness.Pane.Width,
                    "a width the user chose must outlive switching panes, or choosing it was pointless");
            }
        }

        [TestMethod]
        public void TheResizerGoesAwayWithThePane()
        {
            using (var harness = new Harness())
            {
                harness.Workspace.ClosePane();
                Application.DoEvents();

                Assert.IsFalse(harness.Pane.Visible);
                Assert.IsFalse(harness.Resizer.Visible,
                    "a draggable edge with nothing behind it is an edge that resizes nothing");
            }
        }
    }
}
