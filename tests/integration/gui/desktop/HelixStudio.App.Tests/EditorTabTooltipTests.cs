using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// An editor sub-tab shows a bare file name; hovering it says where that file actually is.
    ///
    /// Two projects each with a main.py are indistinguishable on the strip without it, and the
    /// path was already being written to the tab's ToolTipText — the strip just never had tooltips
    /// switched on, so it was computed and thrown away.
    /// </summary>
    [TestClass]
    public class EditorTabTooltipTests
    {
        [TestMethod]
        public void AnEditorTabTipCarriesTheFullPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-tip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "main.py");
            File.WriteAllText(file, "class A:\n    def go(self):\n        pass\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    Assert.IsTrue(form._editorInnerTabControl.ShowToolTips,
                        "the editor strip does not show tooltips, so the path is never displayed");

                    form.OpenInEditor(file, 1);
                    TestMainForm.PumpUntilIdle(iterations: 8, delayMs: 25);

                    TabPage tab = null;
                    foreach (TabPage page in form._editorInnerTabControl.TabPages)
                        if (page.Text.StartsWith("main.py", StringComparison.Ordinal)) tab = page;
                    Assert.IsNotNull(tab, "main.py did not open in the editor");

                    Assert.AreEqual(Path.GetFullPath(file), tab.ToolTipText,
                        "the tab's tooltip is not the file's full path");
                    Assert.AreNotEqual(tab.Text, tab.ToolTipText,
                        "the tooltip only repeats the label, which tells the user nothing new");

                    form.Close();
                }
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
