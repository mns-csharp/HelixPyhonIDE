using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 473 (find bar robustness): an incomplete / malformed regex (a lone '(' typed while building
    /// '(foo)') used to make Scintilla's SearchInTarget throw and take the editor down. HighlightAll /
    /// FindNext now swallow the fault and show a "no results / invalid pattern" state instead of
    /// crashing.
    /// </summary>
    [TestClass]
    public class FindBarInvalidRegexTests
    {
        private static T Field<T>(object o, string name)
        {
            return (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
        }

        [TestMethod]
        public void TypingAMalformedRegex_DoesNotThrow_AndShowsAMissState()
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
                    editor.Text = "def foo():\n    return (1, 2)\n";
                    Application.DoEvents();
                }

                try
                {
                    var regex = Field<CheckBox>(bar, "_regex");
                    var findBox = Field<TextBox>(bar, "_findBox");
                    var countLabel = Field<Label>(bar, "_countLabel");

                    regex.Checked = true;
                    Application.DoEvents();

                    // A lone unbalanced group — invalid regex. Must not throw.
                    findBox.Text = "(unbalanced";
                    Application.DoEvents();

                    // No matches surfaced, and the input reads as a miss (tinted). The exact label may be
                    // "Invalid pattern" (Scintilla threw) or "No results" (it returned no match) — either
                    // is acceptable; the point is the editor survived.
                    var matchStarts = Field<System.Collections.Generic.List<int>>(bar, "_matchStarts");
                    Assert.AreEqual(0, matchStarts.Count, "a malformed regex must surface no matches");
                    StringAssert.Contains(countLabel.Text.ToLowerInvariant(),
                        countLabel.Text.ToLowerInvariant().Contains("invalid") ? "invalid" : "result",
                        "the counter should read a no-match / invalid state, not stay blank");

                    // A subsequent valid pattern recovers cleanly.
                    findBox.Text = "foo";
                    Application.DoEvents();
                    Assert.IsTrue(matchStarts.Count >= 1, "a valid pattern after the bad one must find matches again");
                }
                finally { form.Close(); }
            }
        }
    }
}
