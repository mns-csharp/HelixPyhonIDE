using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 284 (screenshot audit): the Welcome page's recent-solutions list showed raw absolute
    /// paths that truncate on the right with no way to read the whole path. Hovering a row shows its
    /// full path as a tooltip.
    ///
    /// Iter 335 rewrote the page as a GDI+ surface in the VS2022 start-page idiom, so the rows are
    /// painted regions rather than ListBox items and the tooltip targets the panel itself. The
    /// behaviour under test is unchanged: hovering a real row shows its path, hovering nothing
    /// clears it.
    /// </summary>
    [TestClass]
    public class WelcomeRecentTooltipTests
    {
        private const string LongPath =
            @"C:\Users\Someone\Documents\A very long project folder name\pyproject.toml";

        private static WelcomePanel BuildPanel(params string[] recent)
        {
            var panel = new WelcomePanel(new RecentFilesManager(), new UserSettingsManager());
            // Drive the painted model directly: RefreshRecentFiles reads the real recent-files
            // store, which a test must not mutate.
            var rows = Field<List<string>>(panel, "_recent");
            rows.Clear();
            rows.AddRange(recent);
            return panel;
        }

        [TestMethod]
        public void HoveringARecentRow_ShowsItsFullPath_AndClearsOnLeave()
        {
            using (WelcomePanel panel = BuildPanel(LongPath))
            {
                var tip = Field<ToolTip>(panel, "_recentTip");

                UpdateTooltip(panel, 0);
                Assert.AreEqual(LongPath, tip.GetToolTip(panel), "hovering a row must show its full path");

                UpdateTooltip(panel, -1);
                Assert.AreEqual(string.Empty, tip.GetToolTip(panel), "leaving the rows must clear the tooltip");
            }
        }

        [TestMethod]
        public void WithNoRecentSolutions_ThereIsNoTooltip()
        {
            using (WelcomePanel panel = BuildPanel())
            {
                var tip = Field<ToolTip>(panel, "_recentTip");

                UpdateTooltip(panel, 0);   // the empty state is painted text, not a row
                Assert.AreEqual(string.Empty, tip.GetToolTip(panel),
                    "the empty state is not a path, so it must not show a tooltip");
            }
        }

        [TestMethod]
        public void ARowIsLabelledByItsSolutionFolder_NotPyprojectToml()
        {
            // Every entry is a "pyproject.toml"; the folder is what the user actually recognises.
            Assert.AreEqual("A very long project folder name", WelcomePanel.SolutionDisplayName(LongPath));
            Assert.AreEqual(string.Empty, WelcomePanel.SolutionDisplayName(null));
        }

        private static void UpdateTooltip(WelcomePanel panel, int index)
        {
            MethodInfo m = typeof(WelcomePanel).GetMethod(
                "UpdateRecentTooltip", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "UpdateRecentTooltip(int) expected");
            m.Invoke(panel, new object[] { index });
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }
    }
}
