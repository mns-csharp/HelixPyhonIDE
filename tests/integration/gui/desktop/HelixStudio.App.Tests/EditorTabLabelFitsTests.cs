using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Editor sub-tab labels must not run under the close (X) icon.
    ///
    /// The tabs are owner-drawn: the painter insets the label from the left and reserves a strip on
    /// the right for the icon, but the tab's WIDTH comes from WinForms, which under
    /// TabSizeMode.Normal measures the text alone and knows nothing about either reservation. The
    /// tab therefore has to be padded to cover them — otherwise every label is drawn into a
    /// rectangle narrower than itself and its last characters are clipped behind the (X)
    /// ("main.py" showing as "main.p", "pyproject.toml" as "pyproject.tor").
    /// </summary>
    [TestClass]
    public class EditorTabLabelFitsTests
    {
        [TestMethod]
        public void EveryTabIsWideEnoughForItsLabelPlusTheCloseIcon()
        {
            // Realised on a hidden form: tab widths now come from WinForms per tab, and an
            // unparented control reports every rect as 0 wide.
            using (var host = new Form
            {
                ClientSize = new Size(900, 200),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
            })
            using (var tabs = new TabControl { Dock = DockStyle.Fill })
            {
                // The production configuration itself — not a copy of it that can drift.
                EditorCoordinator.ConfigureEditorTabStrip(tabs);

                tabs.TabPages.Add(new TabPage("main.py"));
                tabs.TabPages.Add(new TabPage("pyproject.toml"));
                tabs.TabPages.Add(new TabPage("a_rather_long_module_name.py"));
                tabs.TabPages.Add(new TabPage("m.py *"));          // modified suffix
                host.Controls.Add(tabs);
                TestWindow.ShowOffScreen(host);
                EditorCoordinator.ApplyEditorTabSizing(tabs);      // the production sizing
                Application.DoEvents();

                using (Graphics g = tabs.CreateGraphics())
                {
                    for (int i = 0; i < tabs.TabPages.Count; i++)
                    {
                        string text = tabs.TabPages[i].Text;
                        int actual = tabs.GetTabRect(i).Width;
                        int required = EditorCoordinator.RequiredEditorTabWidth(g, text, tabs.Font);

                        Assert.IsTrue(actual >= required,
                            "tab \"" + text + "\" is " + actual + "px wide but needs " + required
                            + "px for its label plus the close icon — the last characters would be "
                            + "drawn under the (X)");
                    }
                }
            }
        }
    }
}
