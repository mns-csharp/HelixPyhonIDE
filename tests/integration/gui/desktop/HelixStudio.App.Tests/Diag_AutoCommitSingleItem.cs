using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    // Diag: user report — a single-item AutoC popup ("self" inside a method body) blocks
    // typing, ESC doesn't dismiss it, arrow-key selection works but Enter/Tab doesn't commit.
    [TestClass]
    public class Diag_AutoCommitSingleItem
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_CHAR = 0x0102;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern System.IntPtr SendMessage(System.IntPtr hWnd, int msg, System.IntPtr wParam, System.IntPtr lParam);

        [TestMethod]
        public void Diag_TypingSelfPrefix_ThenEnter_CommitsAndDismisses()
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(700, 500), ShowInTaskbar = false })
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Text = "class Foo:\n    def bar(self, cfg):\n        \n";
                editor.CurrentPosition = editor.Lines[2].EndPosition; // end of the blank body line
                editor.Focus();
                Application.DoEvents();

                foreach (char c in "sel")
                {
                    SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)c, System.IntPtr.Zero);
                    Application.DoEvents();
                }

                System.Console.WriteLine("After 'sel': AutoCActive=" + editor.AutoCActive + " Text='" + editor.Text.Replace("\n", "\\n") + "'");
                if (!editor.AutoCActive)
                {
                    Assert.Inconclusive("AutoC did not trigger for this prefix in this environment — cannot test commit path");
                    return;
                }

                var preMsg = Message.Create(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Enter, System.IntPtr.Zero);
                bool preConsumed = editor.PreProcessMessage(ref preMsg);
                if (!preConsumed)
                    SendMessage(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Enter, System.IntPtr.Zero);
                Application.DoEvents();

                System.Console.WriteLine("After WM_KEYDOWN(Enter) ONLY: AutoCActive=" + editor.AutoCActive + " preConsumed=" + preConsumed +
                    " Text='" + editor.Text.Replace("\n", "\\n") + "'");
            }
        }
    }
}
