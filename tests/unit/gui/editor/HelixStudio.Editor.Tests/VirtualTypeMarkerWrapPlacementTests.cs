using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixTextEditorNET;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS solution_explorer_rules §3.3: the inferred-type inlay marker is drawn after the END of a
    /// line's text — including when word wrap has split that line across several display rows.
    ///
    /// The bug this pins (reported against a real file, in word-wrap view): the marker's
    /// X came from the line's END position while its Y came from the line's START position. Both
    /// coordinates are wrap-relative — <c>PointXFromPosition</c> gives X within the position's own
    /// wrapped row, <c>PointYFromPosition</c> gives that row's Y — so on a wrapped line the pair was
    /// the LAST row's (short) X at the FIRST row's Y, and the marker was painted straight through
    /// the source text. With wrap off a line is one row and both positions land on it, which is why
    /// the defect only ever showed with wrapping on.
    /// </summary>
    [TestClass]
    public class VirtualTypeMarkerWrapPlacementTests
    {
        // A single assignment long enough to wrap in a narrow editor, plus a trailing comment —
        // the exact shape from the report (`x = Something.call()  # a long trailing comment ...`).
        private const string WrappingAssignment =
            "coverage_table = 42  # the table, built from the flowchart and the declarations, " +
            "long enough that this one statement has to be broken across several display rows\n";

        private static Form NewHost()
        {
            return new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                Size = new Size(420, 300),
                ShowInTaskbar = false,
            };
        }

        private static PythonEditorControl ShowEditor(Form host, string text, WrapMode wrap)
        {
            var editor = new PythonEditorControl { Dock = DockStyle.Fill };
            host.Controls.Add(editor);
            TestWindow.ShowOffScreen(host);
            Application.DoEvents();
            editor.CreateControl();
            editor.WrapMode = wrap;
            editor.Text = text;
            Application.DoEvents();
            return editor;
        }

        /// <summary>The line's end position with the terminator trimmed — where the marker belongs.</summary>
        private static int EndOfText(Scintilla editor, int docLine)
        {
            var line = editor.Lines[docLine];
            int endPos = line.EndPosition;
            while (endPos > line.Position)
            {
                int c = editor.GetCharAt(endPos - 1);
                if (c == '\n' || c == '\r') endPos--;
                else break;
            }
            return endPos;
        }

        [TestMethod]
        public void OnAWrappedLine_TheMarkerSitsOnTheLastRow_NotThroughTheFirst()
        {
            using (Form host = NewHost())
            {
                PythonEditorControl editor = ShowEditor(host, WrappingAssignment, WrapMode.Word);

                int endPos = EndOfText(editor, 0);
                int firstRowY = editor.PointYFromPosition(editor.Lines[0].Position);
                int lastRowY = editor.PointYFromPosition(endPos);

                // If this line did not actually wrap the test proves nothing, so make that a failure
                // rather than a silent pass.
                Assert.IsTrue(lastRowY > firstRowY,
                    "the fixture line must actually wrap onto a later row for this test to mean anything");

                Point at;
                Assert.IsTrue(VirtualTypeMarkerRenderer.TryGetMarkerPoint(editor, 0, out at),
                    "the marker point must be resolvable for a normal visible line");

                Assert.AreEqual(lastRowY, at.Y,
                    "the marker must be drawn on the row that holds the end of the line's text — " +
                    "drawing it at the FIRST row's Y is what painted it over the source");
                Assert.AreNotEqual(firstRowY, at.Y,
                    "the first row still holds source text; the marker must not land on it");

                host.Close();
            }
        }

        [TestMethod]
        public void TheMarkersXAndYDescribeTheSamePlace_SoItLandsPastTheEndOfTheText()
        {
            // The invariant the bug broke, stated without reference to the implementation: hit-test
            // the point the marker is painted at and you must land at the end of the line's text.
            // Under the defect the round trip landed in the middle of the first row instead.
            using (Form host = NewHost())
            {
                PythonEditorControl editor = ShowEditor(host, WrappingAssignment, WrapMode.Word);

                int endPos = EndOfText(editor, 0);
                Point at;
                Assert.IsTrue(VirtualTypeMarkerRenderer.TryGetMarkerPoint(editor, 0, out at));

                Assert.AreEqual(endPos, editor.CharPositionFromPoint(at.X, at.Y),
                    "the painted point must hit-test to the end of the line's text");

                host.Close();
            }
        }

        [TestMethod]
        public void WithWrapOff_ThePlacementIsUnchanged()
        {
            // The common case must not move: one row, so the marker's Y is that row's Y and the
            // point still hit-tests to the end of the text.
            using (Form host = NewHost())
            {
                PythonEditorControl editor = ShowEditor(host, WrappingAssignment, WrapMode.None);

                int endPos = EndOfText(editor, 0);
                Assert.AreEqual(editor.PointYFromPosition(editor.Lines[0].Position),
                                editor.PointYFromPosition(endPos),
                    "with wrap off the whole line is one display row (precondition)");

                Point at;
                Assert.IsTrue(VirtualTypeMarkerRenderer.TryGetMarkerPoint(editor, 0, out at));
                Assert.AreEqual(editor.PointYFromPosition(endPos), at.Y);
                Assert.AreEqual(endPos, editor.CharPositionFromPoint(at.X, at.Y));

                host.Close();
            }
        }

        [TestMethod]
        public void AnOutOfRangeLineIsRefused_RatherThanThrowingIntoThePaintPath()
        {
            // DrawOverlay runs inside WM_PAINT; a throw there would take the editor down.
            using (Form host = NewHost())
            {
                PythonEditorControl editor = ShowEditor(host, "a = 1\n", WrapMode.Word);

                Point at;
                Assert.IsFalse(VirtualTypeMarkerRenderer.TryGetMarkerPoint(editor, -1, out at));
                Assert.IsFalse(VirtualTypeMarkerRenderer.TryGetMarkerPoint(editor, 9999, out at));
                Assert.IsFalse(VirtualTypeMarkerRenderer.TryGetMarkerPoint(null, 0, out at));

                host.Close();
            }
        }
    }
}
