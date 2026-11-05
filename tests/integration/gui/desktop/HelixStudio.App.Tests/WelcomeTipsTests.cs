using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Welcome screen omits the Quick Tips section.
    /// </summary>
    [TestClass]
    public class WelcomeTipsTests
    {
        private static string FindLabelContaining(Control root, string needle)
        {
            var label = root as Label;
            if (label != null && (label.Text ?? "").Contains(needle)) return label.Text;
            foreach (Control c in root.Controls)
            {
                string found = FindLabelContaining(c, needle);
                if (found != null) return found;
            }
            return null;
        }

        [TestMethod]
        public void QuickTips_AreNotShown_OnWelcomeScreen()
        {
            using (var panel = new WelcomePanel(new RecentFilesManager(), new UserSettingsManager()))
            {
                string[] removedTips =
                {
                    "Quick tips", "Ctrl+F", "Ctrl+S", "Floating Tools Palette",
                    "pyproject.toml", "Refresh Canvas", "PNG/SVG/PDF",
                };
                foreach (string tip in removedTips)
                    Assert.IsNull(FindLabelContaining(panel, tip),
                        "Welcome screen tip text must be absent: " + tip);
            }
        }
    }
}
