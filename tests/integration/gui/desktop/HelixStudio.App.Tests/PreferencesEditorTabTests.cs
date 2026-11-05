using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 231: the Preferences dialog now exposes an Editor tab to change the code font/size
    /// (EditorFontName/EditorFontSize existed in settings but had no UI). Verifies the tab is
    /// present and pre-populated from the persisted settings.
    /// </summary>
    [TestClass]
    public class PreferencesEditorTabTests
    {
        private static T Field<T>(PreferencesDialog dlg, string name) where T : class
        {
            FieldInfo f = typeof(PreferencesDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "Field " + name + " expected.");
            return f.GetValue(dlg) as T;
        }

        [TestMethod]
        public void EditorTab_IsPresent_AndReflectsPersistedFont()
        {
            var settings = new UserSettingsManager();
            settings.Settings.EditorFontName = "Consolas";
            settings.Settings.EditorFontSize = 13f;

            using (var dlg = new PreferencesDialog(settings, null))
            {
                var tabs = Field<TabControl>(dlg, "tabs");
                Assert.IsNotNull(tabs);
                Assert.IsTrue(tabs.TabPages.Cast<TabPage>().Any(t => t.Text == "Editor"),
                    "Preferences should have an Editor tab.");

                var combo = Field<ComboBox>(dlg, "_fontNameCombo");
                var size = Field<NumericUpDown>(dlg, "_fontSizeInput");
                Assert.IsTrue(combo.Items.Count > 0, "Font combo should be populated.");
                Assert.AreEqual("Consolas", combo.SelectedItem as string,
                    "The persisted font should be pre-selected.");
                Assert.AreEqual(13m, size.Value, "The persisted size should be pre-set.");
            }
        }
    }
}
