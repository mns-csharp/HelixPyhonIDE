using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 229: the Keybindings dialog now reflects the active preset in its combo when it opens,
    /// so the user can see which preset they're on (previously nothing was selected).
    /// </summary>
    [TestClass]
    public class KeyBindingsDialogTests
    {
        [TestMethod]
        public void PresetCombo_ReflectsTheActivePreset()
        {
            var mgr = new KeybindingManager(new UserSettingsManager());
            using (var dlg = new KeyBindingsDialog(mgr))
            {
                FieldInfo f = typeof(KeyBindingsDialog).GetField(
                    "_presetCombo", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(f, "_presetCombo field expected.");
                var combo = (ComboBox)f.GetValue(dlg);

                Assert.IsTrue(combo.SelectedIndex >= 0, "A preset must be pre-selected when the dialog opens.");
                int expected =
                    mgr.CurrentPreset == KeybindingPreset.MacStyle ? 1 :
                    mgr.CurrentPreset == KeybindingPreset.LinuxStyle ? 2 : 0;
                Assert.AreEqual(expected, combo.SelectedIndex, "Combo must match the manager's current preset.");
            }
        }
    }
}
