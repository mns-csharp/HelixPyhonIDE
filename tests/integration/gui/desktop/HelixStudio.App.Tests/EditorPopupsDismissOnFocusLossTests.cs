using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    // Task #16 root cause: AutoC/CallTip/the quick-info doc panel are all real, if
    // WS_EX_NOACTIVATE-styled, top-level popup Forms. None of the editor's Escape-handling
    // code runs unless the EDITOR has focus — but nothing dismissed these popups when focus
    // genuinely moved elsewhere (switching tabs, clicking another panel), so they became
    // permanently orphaned: still visible, but Escape now goes to whatever control actually
    // has focus and never reaches the editor that owns the popup. Fixed by dismissing on
    // Control.Leave (fires only for a genuine focus move to a sibling control) rather than
    // OnLostFocus. At the time this was written, OnLostFocus also fired — spuriously — the
    // instant AutoCShow/CallTipShow showed their own popup Form (it was TopMost with no Owner),
    // which would have self-cancelled the very popup being shown; since that popup was changed
    // to be Owner-parented instead of globally TopMost, showing it no longer blurs the editor at
    // all, so that particular spurious-LostFocus case is now moot — but Leave remains the
    // correct signal regardless, since it's what real focus-moves-elsewhere scenarios raise.
    [TestClass]
    public class EditorPopupsDismissOnFocusLossTests
    {
        private const int WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern System.IntPtr SendMessage(System.IntPtr hWnd, int msg, System.IntPtr wParam, System.IntPtr lParam);

        private static Form NewOffscreenForm()
        {
            return new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(600, 400), ShowInTaskbar = false };
        }

        [TestMethod]
        public void ShowingAutoC_DoesNotSpuriouslyFireLeave_OnItsOwnFocusBlip()
        {
            // Regression guard for the fix itself. Originally (see class comment) AutoCShow's
            // popup was a real Form.Show() call with TopMost=true and no Owner, which DID
            // transiently blur the editor at the WinForms-bookkeeping level even with
            // WS_EX_NOACTIVATE — LostFocus fired despite the editor never really losing keyboard
            // focus. Since AutoCListWindow/CallTipWindow were changed to be Owner-parented
            // instead of globally TopMost (fixing a separate bug: TopMost floated the popup above
            // every window on the desktop, not just the IDE), that blip no longer happens at all —
            // LostFocus doesn't fire here anymore either. That's a strict improvement (one less
            // spurious event to filter), so this test now asserts the stronger "no blur
            // whatsoever" outcome instead of the old "blurs, but Leave still doesn't fire" one.
            // The two invariants that actually matter — no spurious Leave, popup stays open — are
            // unchanged and still asserted below.
            using (var form = NewOffscreenForm())
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                int leaveCount = 0;
                int lostFocusCount = 0;
                editor.Leave += (s, e) => leaveCount++;
                editor.LostFocus += (s, e) => lostFocusCount++;

                editor.AutoCShow(0, "foo bar baz");
                Application.DoEvents();

                Assert.AreEqual(0, lostFocusCount, "showing an owned, non-TopMost popup should not blur the editor at all anymore");
                Assert.AreEqual(0, leaveCount, "Leave must NOT fire from the popup's own show");
                Assert.IsTrue(editor.AutoCActive, "the popup must still be showing — it must not have self-cancelled");

                form.Close();
            }
        }

        [TestMethod]
        public void AutoC_DismissedWhenFocusGenuinelyMovesToAnotherControl()
        {
            using (var form = NewOffscreenForm())
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Top, Height = 200 };
                var otherBox = new TextBox { Dock = DockStyle.Bottom, Height = 30 };
                form.Controls.Add(editor);
                form.Controls.Add(otherBox);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                otherBox.CreateControl();
                editor.Focus();
                Application.DoEvents();

                editor.AutoCShow(0, "foo bar baz");
                Application.DoEvents();
                Assert.IsTrue(editor.AutoCActive, "precondition: AutoC popup must be showing");

                // Models switching tabs / clicking another panel: focus genuinely leaves
                // the editor for a sibling control, without the user touching Escape.
                otherBox.Focus();
                Application.DoEvents();

                Assert.IsTrue(otherBox.Focused, "precondition: focus must have genuinely moved");
                Assert.IsFalse(editor.AutoCActive,
                    "AutoC must be dismissed the moment focus genuinely leaves the editor — otherwise it's " +
                    "permanently orphaned, since Escape now goes to otherBox and never reaches the editor");
            }
        }

        [TestMethod]
        public void CallTip_DismissedWhenFocusGenuinelyMovesToAnotherControl()
        {
            using (var form = NewOffscreenForm())
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Top, Height = 200 };
                var otherBox = new TextBox { Dock = DockStyle.Bottom, Height = 30 };
                form.Controls.Add(editor);
                form.Controls.Add(otherBox);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                otherBox.CreateControl();
                editor.Text = "def foo(a, b):\n    pass\n";
                editor.CurrentPosition = editor.TextLength;
                editor.Focus();
                Application.DoEvents();

                editor.CallTipShow(0, "foo(a, b)");
                Application.DoEvents();
                Assert.IsTrue(editor.CallTipActive, "precondition: CallTip must be showing");

                otherBox.Focus();
                Application.DoEvents();

                Assert.IsFalse(editor.CallTipActive, "CallTip must be dismissed when focus genuinely leaves the editor");
            }
        }

        [TestMethod]
        public void DocPanel_DismissedWhenFocusGenuinelyMovesToAnotherControl()
        {
            using (var form = NewOffscreenForm())
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Top, Height = 200 };
                var otherBox = new TextBox { Dock = DockStyle.Bottom, Height = 30 };
                form.Controls.Add(editor);
                form.Controls.Add(otherBox);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                otherBox.CreateControl();
                editor.Text = "print\n";
                editor.CurrentPosition = 3;
                editor.Focus();
                Application.DoEvents();

                object completion = GetField(editor, "_completion");
                InvokeMethod(completion, "ShowQuickInfoAtCaret");
                Application.DoEvents();
                object docPanel = GetField(completion, "_doc");
                Form docHost = (Form)GetField(docPanel, "_host");
                Assert.IsTrue(docHost.Visible, "precondition: doc panel must be showing");

                otherBox.Focus();
                Application.DoEvents();

                Assert.IsFalse(docHost.Visible, "the doc panel must be dismissed when focus genuinely leaves the editor");
            }
        }

        // End-to-end: confirms the originally-reported symptom is gone — with the fix, the
        // popup no longer even survives long enough to need an Escape at all once focus moves.
        [TestMethod]
        public void FullScenario_FocusMoveThenEscapeToTheNewControl_LeavesNoOrphanedPopup()
        {
            using (var form = NewOffscreenForm())
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Top, Height = 200 };
                var otherBox = new TextBox { Dock = DockStyle.Bottom, Height = 30 };
                form.Controls.Add(editor);
                form.Controls.Add(otherBox);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                otherBox.CreateControl();
                editor.Text = "print\n";
                editor.CurrentPosition = 3;
                editor.Focus();
                Application.DoEvents();

                editor.AutoCShow(0, "print foo bar");
                Application.DoEvents();
                Assert.IsTrue(editor.AutoCActive, "precondition: AutoC popup must be showing");

                otherBox.Focus();
                Application.DoEvents();

                SendMessage(otherBox.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsFalse(editor.AutoCActive,
                    "no popup should remain, whether it was dismissed proactively on focus-out or would " +
                    "otherwise still need an Escape that could never reach it");
            }
        }

        private static object GetField(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(f, name + " field expected on " + obj.GetType().FullName);
            return f.GetValue(obj);
        }

        private static void InvokeMethod(object obj, string name)
        {
            MethodInfo m = obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(m, name + " method expected on " + obj.GetType().FullName);
            m.Invoke(obj, null);
        }
    }
}
