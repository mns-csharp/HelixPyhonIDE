using System;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.Editor;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.Editor.Tests
{
    /// <summary>
    /// A hover tooltip must never outlive the dwell that produced it.
    ///
    /// Reported symptom: the tooltip appeared and never went away, staying painted over the code
    /// while the user worked elsewhere. Two defects combined:
    ///
    /// 1. The hover query is asynchronous. If the mouse moved on before it returned, DwellEnd had
    ///    already fired and the completion handler then showed the tip anyway — with no remaining
    ///    path to hide it, since the next DwellEnd only fires after a NEW dwell begins.
    /// 2. Even a correctly shown tip had no dismissal on lost focus, mouse wheel, or typing.
    /// </summary>
    [TestClass]
    public class HoverTooltipDismissalTests
    {
        private static PythonEditorControl NewEditor(string text)
        {
            var editor = new PythonEditorControl();
            editor.Text = text;
            // Realise the handle so calltip show/cancel operate as they do in the app.
            var _ = editor.Handle;
            return editor;
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            MethodInfo m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(m, method + " expected on PythonEditorControl");
            m.Invoke(target, args);
        }

        private static void SetLastHoverPos(PythonEditorControl editor, int value)
        {
            FieldInfo f = typeof(PythonEditorControl).GetField("_lastHoverPos",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_lastHoverPos expected");
            f.SetValue(editor, value);
        }

        private static int GetLastHoverPos(PythonEditorControl editor)
        {
            return (int)typeof(PythonEditorControl)
                .GetField("_lastHoverPos", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(editor);
        }

        [TestMethod]
        public void DismissHoverTip_CancelsTheTipAndClearsTheAnchor()
        {
            using (var editor = NewEditor("class Service:\n    pass\n"))
            {
                editor.CallTipShow(3, "some hover text");
                SetLastHoverPos(editor, 3);
                Assert.IsTrue(editor.CallTipActive, "precondition: a tip is showing");

                Invoke(editor, "DismissHoverTip");

                Assert.IsFalse(editor.CallTipActive, "the tip must be cancelled");
                Assert.AreEqual(-1, GetLastHoverPos(editor),
                    "the anchor must be cleared, or a stale async result could still match it");
            }
        }

        [TestMethod]
        public void LosingFocus_DismissesTheTip()
        {
            using (var form = new Form())
            using (var editor = NewEditor("class Service:\n    pass\n"))
            {
                form.Controls.Add(editor);
                var other = new TextBox();
                form.Controls.Add(other);
                TestWindow.ShowOffScreen(form);
                editor.Focus();
                Application.DoEvents();

                editor.CallTipShow(3, "hover text");
                SetLastHoverPos(editor, 3);
                Assert.IsTrue(editor.CallTipActive);

                other.Focus();
                Application.DoEvents();

                Assert.IsFalse(editor.CallTipActive,
                    "Alt-Tab / clicking away left the tooltip painted over the code");
                form.Close();
            }
        }

        [TestMethod]
        public void DwellEnd_ResetsTheAnchorSoAStaleResultCannotShow()
        {
            using (var editor = NewEditor("class Service:\n    pass\n"))
            {
                SetLastHoverPos(editor, 7);
                Invoke(editor, "OnDwellEnd", null,
                    new HelixTextEditorNET.DwellEventArgs(7, 0, 0));

                Assert.AreEqual(-1, GetLastHoverPos(editor),
                    "a hover result arriving after the dwell ended is identified by this mismatch");
            }
        }

        [TestMethod]
        public void ShowHoverAt_OnEmptyDocument_DoesNotThrow()
        {
            using (var editor = NewEditor(string.Empty))
            {
                editor.ShowHoverAt(0);
                Assert.IsFalse(editor.CallTipActive, "nothing to describe in an empty document");
            }
        }
    }
}
