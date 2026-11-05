using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Editor.Tests
{
    /// <summary>
    /// Ctrl+MouseWheel font zoom in the code editor.
    ///
    /// Reported symptom: the gesture did nothing at all. The wheel handling, the clamp and the
    /// "tell the host to persist it" event were all present and correct — but
    /// <c>ApplyFontSettings</c> pushed the new size into STYLE_DEFAULT and then finished by
    /// calling <c>ConfigureSyntaxHighlighting</c>, which re-applied a hardcoded "Consolas"/11 on
    /// top. So <c>EditorFontSize</c> reported the zoomed value while every glyph on screen stayed
    /// at 11 — for the wheel gesture and for the persisted Preferences font alike. These tests
    /// assert against the STYLE the document is painted with, not just the tracked number.
    /// </summary>
    [TestClass]
    public class EditorFontZoomTests
    {
        /// <summary>
        /// Drives the real <c>OnMouseWheel</c> override with the zoom modifier reported as held.
        /// WinForms carries no modifier state in MouseEventArgs, so the modifier check is a
        /// virtual method rather than something a synthetic WM_MOUSEWHEEL could influence.
        /// </summary>
        private sealed class ZoomProbeEditor : PythonEditorControl
        {
            public bool CtrlHeld;
            protected override bool IsZoomModifierPressed() { return CtrlHeld; }
            public void Wheel(int delta)
            {
                MethodInfo m = typeof(PythonEditorControl).GetMethod("OnMouseWheel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(m, "PythonEditorControl.OnMouseWheel expected");
                m.Invoke(this, new object[] { new MouseEventArgs(MouseButtons.None, 0, 10, 10, delta) });
            }
        }

        private static ZoomProbeEditor NewEditor(float sizePoints)
        {
            var editor = new ZoomProbeEditor();
            var _ = editor.Handle;   // realise the handle, as the app does
            editor.Text = "class Sample:\n    # a comment\n    def area(self, r):\n        return r * r\n";
            editor.ApplyFontSettings("Consolas", sizePoints);
            return editor;
        }

        private static int RenderedSize(PythonEditorControl editor, int styleIndex)
        {
            return editor.Styles[styleIndex].Size;
        }

        [TestMethod]
        public void CtrlWheelUp_GrowsTheFont_AndCtrlWheelDownShrinksIt()
        {
            using (ZoomProbeEditor editor = NewEditor(11f))
            {
                var raised = new List<float>();
                editor.EditorFontSizeChanged += (s, size) => raised.Add(size);

                editor.CtrlHeld = true;
                editor.Wheel(120);
                Assert.AreEqual(12f, editor.EditorFontSize, "one notch up = one point");
                editor.Wheel(-120);
                editor.Wheel(-120);
                Assert.AreEqual(10f, editor.EditorFontSize, "two notches down from 12");

                CollectionAssert.AreEqual(new[] { 12f, 11f, 10f }, raised,
                    "every size change must be reported so the host can persist it");
            }
        }

        [TestMethod]
        public void WheelWithoutCtrl_DoesNotChangeTheFont()
        {
            using (ZoomProbeEditor editor = NewEditor(11f))
            {
                bool raised = false;
                editor.EditorFontSizeChanged += (s, size) => raised = true;

                editor.CtrlHeld = false;
                editor.Wheel(120);

                Assert.AreEqual(11f, editor.EditorFontSize, "a plain wheel scrolls, it does not zoom");
                Assert.AreEqual(11, RenderedSize(editor, HelixTextEditorNET.Style.Default));
                Assert.IsFalse(raised);
            }
        }

        [TestMethod]
        public void ZoomIsClampedAndDoesNotReportUnchangedSizes()
        {
            using (ZoomProbeEditor editor = NewEditor(PythonEditorControl.MaxEditorFontSize))
            {
                int raised = 0;
                editor.EditorFontSizeChanged += (s, size) => raised++;

                editor.CtrlHeld = true;
                editor.Wheel(120);

                Assert.AreEqual(PythonEditorControl.MaxEditorFontSize, editor.EditorFontSize);
                Assert.AreEqual(0, raised, "no event at the clamp boundary — nothing changed to persist");
            }
        }

        // ---------------------------------------------------------------- the reported bug

        [TestMethod]
        public void ZoomedFont_ReachesTheStyleTheDocumentIsPaintedWith()
        {
            using (ZoomProbeEditor editor = NewEditor(11f))
            {
                Assert.AreEqual(11, RenderedSize(editor, HelixTextEditorNET.Style.Default));

                editor.CtrlHeld = true;
                editor.Wheel(120);
                editor.Wheel(120);

                Assert.AreEqual(13f, editor.EditorFontSize, "two notches up from 11");
                Assert.AreEqual(13, RenderedSize(editor, HelixTextEditorNET.Style.Default),
                    "the rendered default style must carry the zoomed size");
                Assert.AreEqual(13, RenderedSize(editor, HelixTextEditorNET.Style.Python.CommentLine),
                    "every syntax style inherits the zoomed size via StyleClearAll");
            }
        }

        [TestMethod]
        public void ZoomingDown_AlsoReachesTheRenderedStyle()
        {
            using (ZoomProbeEditor editor = NewEditor(11f))
            {
                editor.CtrlHeld = true;
                editor.Wheel(-120);
                editor.Wheel(-120);

                Assert.AreEqual(9f, editor.EditorFontSize);
                Assert.AreEqual(9, RenderedSize(editor, HelixTextEditorNET.Style.Default));
            }
        }

        /// <summary>
        /// The next-session half: a size restored from user-settings.json is applied the same way,
        /// through the same method the host calls when it opens a document.
        /// </summary>
        [TestMethod]
        public void PersistedFontSize_IsAppliedToTheRenderedStyleOnOpen()
        {
            using (ZoomProbeEditor editor = NewEditor(18f))
            {
                Assert.AreEqual(18f, editor.EditorFontSize);
                Assert.AreEqual("Consolas", editor.EditorFontName);
                Assert.AreEqual(18, RenderedSize(editor, HelixTextEditorNET.Style.Default),
                    "the size restored from user-settings.json is the size that gets painted");
                Assert.AreEqual(18, RenderedSize(editor, HelixTextEditorNET.Style.Python.Word),
                    "keyword styling included");
            }
        }

        /// <summary>A zoom must survive a re-lex/re-style of the same document.</summary>
        [TestMethod]
        public void ZoomedSize_SurvivesAReStyleOfTheDocument()
        {
            using (ZoomProbeEditor editor = NewEditor(11f))
            {
                editor.CtrlHeld = true;
                editor.Wheel(120);
                Assert.AreEqual(12, RenderedSize(editor, HelixTextEditorNET.Style.Default));

                // Typing re-runs the styling path over the buffer.
                editor.Text = editor.Text + "\n# more text\n";

                Assert.AreEqual(12f, editor.EditorFontSize);
                Assert.AreEqual(12, RenderedSize(editor, HelixTextEditorNET.Style.Default),
                    "editing the document must not silently reset the zoom");
            }
        }
    }
}
