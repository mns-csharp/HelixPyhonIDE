using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixTextEditorNET;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 269: the editor Find bar gained a regex toggle (".*"). Guards that enabling it sets
    /// SearchFlags.Regex, and that it is mutually exclusive with Whole-Word (Scintilla rejects the combo).
    /// </summary>
    [TestClass]
    public class FindBarRegexTests
    {
        [TestMethod]
        public void RegexToggle_SetsRegexFlag_AndSupersedesWholeWord()
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

                try
                {
                    var regex = (CheckBox)Field(bar, "_regex");
                    var word = (CheckBox)Field(bar, "_wholeWord");
                    MethodInfo flags = typeof(FindReplaceBar).GetMethod("Flags", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(flags, "Flags() expected");

                    regex.Checked = true;
                    word.Checked = true;
                    var f1 = (SearchFlags)flags.Invoke(bar, null);
                    Assert.IsTrue((f1 & SearchFlags.Regex) != 0, "regex toggle must set SearchFlags.Regex");
                    Assert.IsTrue((f1 & SearchFlags.WholeWord) == 0, "whole-word must be suppressed while regex is on");
                    Assert.IsFalse(word.Enabled, "whole-word checkbox must be greyed out while regex is on");

                    regex.Checked = false;
                    var f2 = (SearchFlags)flags.Invoke(bar, null);
                    Assert.IsTrue((f2 & SearchFlags.Regex) == 0, "regex flag clears when toggle is off");
                    Assert.IsTrue((f2 & SearchFlags.WholeWord) != 0, "whole-word applies again once regex is off");
                    Assert.IsTrue(word.Enabled, "whole-word checkbox re-enables once regex is off");
                }
                finally { form.Close(); }
            }
        }

        private static object Field(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "field " + name + " expected");
            return f.GetValue(obj);
        }
    }
}
