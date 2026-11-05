using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Preferences ▸ General toggle that decides whether the IDE accepts procedural
    /// (non-object-oriented) Python — rule PY8002.
    ///
    /// The setting, the virtual-compiler rule and the Run/Debug gate all existed; the CONTROL did
    /// not reach the screen. PreferencesDialog's General/Canvas/Build Tools TabPages are built by
    /// the designer, and the hand-written <c>BuildGeneralTab()</c> that created the theme picker
    /// and this checkbox returned a brand-new TabPage that nothing ever called or added — so the
    /// fields stayed null, every read of them was null-guarded into a no-op, and the setting was
    /// unreachable from the running IDE.
    /// </summary>
    [TestClass]
    public class PreferencesSolutionPolicyTests
    {
        private static T Field<T>(PreferencesDialog dlg, string name) where T : class
        {
            FieldInfo f = typeof(PreferencesDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "Field " + name + " expected.");
            return f.GetValue(dlg) as T;
        }

        private static TabPage GeneralTab(PreferencesDialog dlg)
        {
            var tabs = Field<TabControl>(dlg, "tabs");
            Assert.IsNotNull(tabs, "Preferences must have a TabControl.");
            TabPage general = tabs.TabPages.Cast<TabPage>().FirstOrDefault(t => t.Text == "General");
            Assert.IsNotNull(general, "Preferences must have a General tab.");
            return general;
        }

        [TestMethod]
        public void GeneralTab_ShowsTheNonObjectOrientedToggle()
        {
            var settings = new UserSettingsManager();
            using (var dlg = new PreferencesDialog(settings, null))
            {
                var check = Field<CheckBox>(dlg, "_allowProceduralCheck");
                Assert.IsNotNull(check, "the solution-policy toggle must be constructed");
                Assert.AreSame(GeneralTab(dlg), check.Parent,
                    "and it must live on the General tab the dialog actually shows");
                StringAssert.Contains(check.Text, "non-object-oriented");
            }
        }

        [TestMethod]
        public void GeneralTab_AlsoShowsTheThemePicker()
        {
            // Same dead-code bug hid the theme picker; it is on the same tab and the same fix.
            var settings = new UserSettingsManager();
            using (var dlg = new PreferencesDialog(settings, null))
            {
                var combo = Field<ComboBox>(dlg, "_themeCombo");
                Assert.IsNotNull(combo, "the theme picker must be constructed");
                Assert.AreSame(GeneralTab(dlg), combo.Parent, "and shown on the General tab");
                Assert.IsTrue(combo.Items.Count >= 3, "Blue / Green / Crimson");
                Assert.IsTrue(combo.SelectedIndex >= 0, "the persisted theme must be pre-selected");
            }
        }

        [TestMethod]
        public void Toggle_ReflectsThePersistedSetting_InBothStates()
        {
            var off = new UserSettingsManager();
            off.Settings.AllowNonObjectOrientedCode = false;
            using (var dlg = new PreferencesDialog(off, null))
                Assert.IsFalse(Field<CheckBox>(dlg, "_allowProceduralCheck").Checked,
                    "PY8002 enforced is the default and must show as unchecked");

            var on = new UserSettingsManager();
            on.Settings.AllowNonObjectOrientedCode = true;
            using (var dlg = new PreferencesDialog(on, null))
                Assert.IsTrue(Field<CheckBox>(dlg, "_allowProceduralCheck").Checked,
                    "a solution opened with the rule relaxed must come back up relaxed");
        }

        /// <summary>
        /// The other end of the wire: the setting the checkbox writes is what the solution policy
        /// the virtual compiler and the Run/Debug preflight are handed actually enforces.
        /// </summary>
        [TestMethod]
        public void TheSetting_DrivesTheSolutionPolicyHandedToTheCompiler()
        {
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                bool original = form._settingsManager.Settings.AllowNonObjectOrientedCode;
                try
                {
                    form._settingsManager.Settings.AllowNonObjectOrientedCode = false;
                    Assert.IsTrue(form.BuildSolutionPolicy(null).EnforceObjectOrientation,
                        "off by default: every module-level def is PY8002 and blocks Run/Debug");

                    form._settingsManager.Settings.AllowNonObjectOrientedCode = true;
                    Assert.IsFalse(form.BuildSolutionPolicy(null).EnforceObjectOrientation,
                        "checked: PY8002 is relaxed to nothing");
                }
                finally
                {
                    form._settingsManager.Settings.AllowNonObjectOrientedCode = original;
                }
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }
    }
}
