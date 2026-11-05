using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Debugger;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// How the debug data tip opens. Reported against a real stop: the tip came up as a
    /// single-row strip of text, which is not a view of an object — the members ARE the answer to
    /// "what is this right now", and they were all behind a click.
    ///
    /// Two rules, and they pull in opposite directions, which is why both are pinned here:
    ///  * a value WITH members opens already expanded and at least five rows tall;
    ///  * a leaf value stays one row — a five-row box of whitespace around `n = 42` would be worse
    ///    than the strip that started this.
    /// </summary>
    [TestClass]
    public class DebugDataTipWindowTests
    {
        private const int MinRows = 5;

        private static DebugVariable Var(string name, string value, string type, int childRef)
        {
            return new DebugVariable { Name = name, Value = value, Type = type, VariablesReference = childRef };
        }

        private static TreeView Tree(DebugDataTipWindow tip)
        {
            FieldInfo f = typeof(DebugDataTipWindow).GetField("_tree",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_tree expected");
            return (TreeView)f.GetValue(tip);
        }

        private static IList<DebugVariable> ThreeMembers(int reference)
        {
            return new List<DebugVariable>
            {
                Var("n_checks", "24", "int", 0),
                Var("n_components", "12", "int", 0),
                Var("rows", "('a', 'b')", "tuple", reference + 1),
            };
        }

        [TestMethod]
        public void AnExpandableValue_OpensAlreadyExpanded_WithItsMembersLoaded()
        {
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("coverage_table", Var("coverage_table", "CoverageMap(...)", "CoverageMap", 7),
                            new Point(200, 200), 16, ThreeMembers, null);

                TreeView tree = Tree(tip);
                Assert.AreEqual(1, tree.Nodes.Count);
                Assert.IsTrue(tree.Nodes[0].IsExpanded,
                    "the members are the answer; the user must not have to click for them");
                Assert.AreEqual(3, tree.Nodes[0].Nodes.Count, "the members are really there, not a placeholder");
                Assert.IsTrue(tree.Nodes[0].Nodes[0].Text.StartsWith("n_checks = 24"),
                    "each member row reads name = value");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void AnExpandableValue_OpensAtLeastFiveRowsTall()
        {
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("coverage_table", Var("coverage_table", "CoverageMap(...)", "CoverageMap", 7),
                            new Point(200, 200), 16, ThreeMembers, null);

                TreeView tree = Tree(tip);
                Assert.IsTrue(tip.Height >= MinRows * tree.ItemHeight,
                    "an object's tip opens with room for several members, not as a one-row strip " +
                    "(was " + tip.Height + "px, need >= " + (MinRows * tree.ItemHeight) + ")");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void ALeafValue_StaysOneRow()
        {
            // The floor is for objects. A scalar in a five-row box would be mostly empty space.
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("n_checks", Var("n_checks", "24", "int", 0),
                            new Point(200, 200), 16, r => new List<DebugVariable>(), null);

                TreeView tree = Tree(tip);
                Assert.IsFalse(tree.Nodes[0].IsExpanded);
                Assert.IsTrue(tip.Height < MinRows * tree.ItemHeight,
                    "a value with no members must not open a tall empty box (was " + tip.Height + "px)");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void MembersAreFetchedOnce_EvenThoughTheRootIsPreExpanded()
        {
            // ShowFor pre-loads the root and then expands it; BeforeExpand must find the node
            // already loaded rather than issuing a second round trip to the paused process.
            int calls = 0;
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7), new Point(200, 200), 16,
                            r => { calls++; return ThreeMembers(r); }, null);

                TreeView tree = Tree(tip);
                tree.Nodes[0].Collapse();
                tree.Nodes[0].Expand();

                Assert.AreEqual(1, calls, "the members are fetched once, not on every expand");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void AMultiLineValue_IsFlattenedIntoOneLegibleRow()
        {
            // A debugger returns repr() text; a numpy array arrives with embedded newlines and runs
            // to thousands of characters. Put straight into a TreeNode label it produced a wide,
            // blank-looking box with nothing readable in it (measured on a real 12x12 array).
            string numpyish = "array([[0, 1, 1],\n       [1, 0, 0],\n       [0, 0, 1]])" + new string('x', 500);
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("table", Var("table", numpyish, "ndarray", 0),
                            new Point(200, 200), 16, r => new List<DebugVariable>(), null);

                string label = Tree(tip).Nodes[0].Text;
                Assert.IsFalse(label.Contains("\n"), "a tree row is one line; newlines must be flattened");
                Assert.IsTrue(label.Length < 260, "a long value is elided to a summary (was " + label.Length + ")");
                Assert.IsTrue(label.StartsWith("table = array([[0, 1, 1], [1, 0, 0], [0, 0, 1]])"),
                    "flattening must keep the value readable, not mangle it: " + label);
                tip.HideTip();
            }
        }

        // ---------------------------------------------------------------- the resize grip

        private static Control Grip(DebugDataTipWindow tip)
        {
            FieldInfo f = typeof(DebugDataTipWindow).GetField("_grip",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_grip expected");
            return (Control)f.GetValue(tip);
        }

        private static void RaiseGrip(DebugDataTipWindow tip, string handler, int x, int y, MouseButtons button)
        {
            MethodInfo m = typeof(DebugDataTipWindow).GetMethod(handler,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, handler + " expected");
            m.Invoke(tip, new object[] { Grip(tip), new MouseEventArgs(button, 1, x, y, 0) });
        }

        /// <summary>Drags the corner by (dx, dy) the way the mouse would.</summary>
        private static void DragGrip(DebugDataTipWindow tip, int dx, int dy)
        {
            RaiseGrip(tip, "OnGripMouseDown", 0, 0, MouseButtons.Left);
            RaiseGrip(tip, "OnGripMouseMove", dx, dy, MouseButtons.Left);
            RaiseGrip(tip, "OnGripMouseUp", dx, dy, MouseButtons.Left);
        }

        [TestMethod]
        public void ThereIsADragCornerInTheBottomRight_OnTopOfTheTree()
        {
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7), new Point(200, 200), 16,
                            ThreeMembers, null);

                Control grip = Grip(tip);
                Assert.AreEqual(tip.ClientSize.Width, grip.Right, "the grip sits on the right edge");
                Assert.AreEqual(tip.ClientSize.Height, grip.Bottom, "and on the bottom edge");
                Assert.AreEqual(Cursors.SizeNWSE, grip.Cursor, "and says so with the resize cursor");
                Assert.AreEqual(0, tip.Controls.GetChildIndex(grip),
                    "index 0 is topmost in WinForms — a grip behind the Dock=Fill tree is unclickable");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void DraggingTheCorner_MakesTheTipBigger()
        {
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7), new Point(200, 200), 16,
                            ThreeMembers, null);
                Size before = tip.Size;

                DragGrip(tip, 120, 90);

                Assert.AreEqual(before.Width + 120, tip.Width);
                Assert.AreEqual(before.Height + 90, tip.Height);
                tip.HideTip();
            }
        }

        [TestMethod]
        public void ADraggedSizeSurvivesTheNextHover()
        {
            // Re-measuring the content on the next hover and quietly undoing the drag is what makes
            // a resizable popup feel like it ignored you.
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7), new Point(200, 200), 16,
                            ThreeMembers, null);
                DragGrip(tip, 100, 80);
                Size dragged = tip.Size;
                tip.HideTip();

                tip.ShowFor("other", Var("other", "42", "int", 0), new Point(300, 300), 16,
                            r => new List<DebugVariable>(), null);

                Assert.AreEqual(dragged, tip.Size, "the size the user chose outlives the value shown in it");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void ADraggedSizeIsNotUndoneByExpandingANode()
        {
            // FitToContent runs on every expand/collapse; it must not fight a size set by hand.
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7), new Point(200, 200), 16,
                            ThreeMembers, null);
                DragGrip(tip, 150, 100);
                Size dragged = tip.Size;

                TreeView tree = Tree(tip);
                tree.Nodes[0].Collapse();
                tree.Nodes[0].Expand();

                Assert.AreEqual(dragged, tip.Size, "expanding must not snap the tip back to its measured size");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void TheTipStaysOpenWhileTheCornerIsBeingDragged()
        {
            // The dismiss timer fires on the mouse leaving; a tip that closed mid-drag could never
            // be made bigger.
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7), new Point(200, 200), 16,
                            ThreeMembers, null);

                RaiseGrip(tip, "OnGripMouseDown", 0, 0, MouseButtons.Left);
                Assert.IsTrue(tip.IsResizing);
                Assert.IsTrue(tip.IsInteracting, "the coordinator's dismiss timer consults this");

                RaiseGrip(tip, "OnGripMouseUp", 50, 50, MouseButtons.Left);
                Assert.IsFalse(tip.IsResizing);
                tip.HideTip();
            }
        }

        [TestMethod]
        public void TheDragCannotShrinkTheTipToNothing()
        {
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7), new Point(200, 200), 16,
                            ThreeMembers, null);

                DragGrip(tip, -5000, -5000);

                Assert.IsTrue(tip.Width >= tip.MinimumSize.Width, "width floor");
                Assert.IsTrue(tip.Height >= tip.MinimumSize.Height, "height floor");
                tip.HideTip();
            }
        }

        [TestMethod]
        public void TheTipIsPulledBackOntoTheScreen()
        {
            // Anchored blindly under the hovered word, a tip near the bottom-right edge shows the
            // user nothing at all.
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            using (var tip = TestWindow.Transparent(new DebugDataTipWindow()))
            {
                tip.ShowFor("obj", Var("obj", "Thing(...)", "Thing", 7),
                            new Point(work.Right - 5, work.Bottom - 5), 16, ThreeMembers, null);

                Assert.IsTrue(tip.Right <= work.Right, "the tip must not run off the right edge");
                Assert.IsTrue(tip.Bottom <= work.Bottom, "nor under the taskbar");
                Assert.IsTrue(tip.Left >= work.Left && tip.Top >= work.Top);
                tip.HideTip();
            }
        }
    }
}
