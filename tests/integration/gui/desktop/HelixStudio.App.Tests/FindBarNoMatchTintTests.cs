using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 471 (find bar polish): a non-empty search that finds nothing tints the find input (VS Code
    /// parity) so a miss is felt without reading the counter. A match — or an empty box — restores the
    /// normal background.
    /// </summary>
    [TestClass]
    public class FindBarNoMatchTintTests
    {
        private static TextBox FindBox(FindReplaceBar bar)
        {
            return (TextBox)typeof(FindReplaceBar).GetField("_findBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bar);
        }

        private static Color NoMatch()
        {
            return (Color)typeof(FindReplaceBar).GetField("FindBoxNoMatch", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        private static Color Normal()
        {
            return (Color)typeof(FindReplaceBar).GetField("FindBoxNormal", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        [TestMethod]
        public void NoMatchTintsTheInput_MatchAndEmptyRestoreIt()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 300);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                var bar = new FindReplaceBar(editor);
                form.Controls.Add(editor);
                form.Controls.Add(bar);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                if ((editor.Text ?? string.Empty).Length == 0)
                {
                    editor.Text = "the quick brown fox";
                    Application.DoEvents();
                }

                try
                {
                    TextBox box = FindBox(bar);

                    // A term that is not present → tint.
                    box.Text = "zzznotpresent";
                    Application.DoEvents();
                    Assert.AreEqual(NoMatch(), box.BackColor, "a no-match search should tint the find input");

                    // A term that IS present → normal.
                    box.Text = "quick";
                    Application.DoEvents();
                    Assert.AreEqual(Normal(), box.BackColor, "a matching search restores the normal background");

                    // Emptying the box is not a 'no match' → normal.
                    box.Text = "zzznotpresent";
                    Application.DoEvents();
                    box.Text = string.Empty;
                    Application.DoEvents();
                    Assert.AreEqual(Normal(), box.BackColor, "an empty search must not read as a no-match");
                }
                finally { form.Close(); }
            }
        }
    }
}
