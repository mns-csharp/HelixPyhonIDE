using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixTextEditorNET;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §3.2, "strictly Allman": the virtual '{'/'}' must occupy a genuinely separate
    /// visual row (a real Scintilla-style annotation row that shifts every line after it
    /// down), not just be repositioned on the header/last-body-line's own row. These tests
    /// run a real PythonEditorControl end to end and check actual pixel Y positions —
    /// proving the row is real, not just that a field got set.
    /// </summary>
    [TestClass]
    public class BlockScopeAllmanAnnotationTests
    {
        private static PythonEditorControl CreateHostedEditor(Form form, string text)
        {
            var editor = new PythonEditorControl { Dock = DockStyle.Fill };
            form.Controls.Add(editor);
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
            editor.CreateControl();
            editor.Text = text;
            Application.DoEvents();
            ForceBlockScopeRecompute(editor);
            Application.DoEvents();
            return editor;
        }

        // The renderer recomputes blocks + annotations on a 400ms debounce after
        // TextChanged; force it synchronously so the test is deterministic, exercising
        // the real Recompute()/ApplyAnnotations() code path directly rather than sleeping.
        private static void ForceBlockScopeRecompute(PythonEditorControl editor)
        {
            FieldInfo f = typeof(PythonEditorControl).GetField("_blockScope", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_blockScope field expected");
            object renderer = f.GetValue(editor);
            Assert.IsNotNull(renderer, "_blockScope must be constructed");
            MethodInfo m = renderer.GetType().GetMethod("Recompute", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "Recompute method expected");
            m.Invoke(renderer, null);
        }

        [TestMethod]
        public void HeaderLine_ReservesOneAnnotationRow_ForTheOpeningBrace()
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(600, 400), ShowInTaskbar = false })
            using (var editor = CreateHostedEditor(form, "def f():\n    pass\n"))
            {
                Assert.IsTrue(editor.VirtualBracesEnabled, "precondition: Allman braces are the active mode");
                Assert.AreEqual(1, editor.AnnotationGetLines(0), "the header line ('def f():') must reserve exactly one row for '{'");
            }
        }

        [TestMethod]
        public void LastBodyLine_ReservesOneAnnotationRow_ForTheClosingBrace()
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(600, 400), ShowInTaskbar = false })
            using (var editor = CreateHostedEditor(form, "def f():\n    pass\n"))
            {
                Assert.AreEqual(1, editor.AnnotationGetLines(1), "the last body line ('pass') must reserve exactly one row for '}'");
            }
        }

        [TestMethod]
        public void AnnotationRow_ActuallyPushesTheNextRealLineDown_ByAFullLineHeight()
        {
            // Two back-to-back single-line defs: doc line 0 = "def f():" (reserves 1 row for
            // '{'), doc line 1 = "    pass" (reserves 1 row for '}'), doc line 2 = "def g():".
            // If annotations were fake (not real layout rows), line 2 would sit exactly
            // 2 * LineHeightPixels below line 0's top (one row per real line). Since each of
            // lines 0 and 1 also reserves an annotation row, line 2 must actually sit
            // 4 * LineHeightPixels below line 0's top: its own row (1) + line 1's own row (1)
            // + line 0's '{' row (1) + line 1's '}' row (1).
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(600, 400), ShowInTaskbar = false })
            using (var editor = CreateHostedEditor(form, "def f():\n    pass\ndef g():\n    pass\n"))
            {
                Assert.AreEqual(1, editor.AnnotationGetLines(0), "line 0 reserves a row for '{'");
                Assert.AreEqual(1, editor.AnnotationGetLines(1), "line 1 reserves a row for '}'");

                int y0 = editor.PointYFromPosition(editor.Lines[0].Position);
                int y2 = editor.PointYFromPosition(editor.Lines[2].Position);

                int rowHeight = editor.PointYFromAnnotationRow(0, 0) - y0; // one row's pixel height
                Assert.IsTrue(rowHeight > 0, "an annotation row must have positive height");

                Assert.AreEqual(4 * rowHeight, y2 - y0,
                    "doc line 2 must be pushed down by 4 real rows (its own + line 1's own + " +
                    "the '{' row on line 0 + the '}' row on line 1), proving annotations are " +
                    "genuine layout rows, not an inline repositioning trick");
            }
        }

        [TestMethod]
        public void NestedBlocksClosingOnTheSameLine_StackDeepestFirst()
        {
            // The innermost block's last content line is also the outermost's last content
            // line, so that one doc line must reserve 2 rows (one closer per nesting level),
            // and the deeper (more-indented) block's closer must occupy row 0 (closest to the
            // code), the shallower one row 1 (below it) — the order a human would type them.
            string src = "if a:\n    if b:\n        x = 1\n";
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(600, 400), ShowInTaskbar = false })
            using (var editor = CreateHostedEditor(form, src))
            {
                Assert.AreEqual(2, editor.AnnotationGetLines(2),
                    "'x = 1' closes both the inner 'if b:' and outer 'if a:' blocks — 2 rows");

                int yRow0 = editor.PointYFromAnnotationRow(2, 0);
                int yRow1 = editor.PointYFromAnnotationRow(2, 1);
                Assert.IsTrue(yRow1 > yRow0, "row 1 (outer closer) must render below row 0 (inner closer)");
            }
        }

        [TestMethod]
        public void SwitchingToBezierMode_ClearsAllReservedAnnotationRows()
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(600, 400), ShowInTaskbar = false })
            using (var editor = CreateHostedEditor(form, "def f():\n    pass\n"))
            {
                Assert.AreEqual(1, editor.AnnotationGetLines(0), "precondition: Allman mode reserves a row");

                editor.VirtualBracesEnabled = false; // -> Bézier connectors (§3.4)
                Application.DoEvents();

                Assert.AreEqual(0, editor.AnnotationGetLines(0), "Bézier mode draws in-place; no reserved rows should remain");
            }
        }
    }
}
