using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 290: the Editor preferences tab let you pick a font + size but showed no sample, so you
    /// had to Apply to see the result. It now has a live preview label rendered in the chosen font.
    /// Guards that the preview tracks the selected font name and size.
    /// </summary>
    [TestClass]
    public class PreferencesFontPreviewTests
    {
        [TestMethod]
        public void FontPreview_TracksSelectedFontAndSize()
        {
            // The DI ctor builds the Editor tab (font/size/preview); the parameterless one doesn't.
            using (var dlg = new PreferencesDialog(new UserSettingsManager(), null))
            {
                var combo = Field<ComboBox>(dlg, "_fontNameCombo");
                var size = Field<NumericUpDown>(dlg, "_fontSizeInput");
                var preview = Field<Label>(dlg, "_fontPreview");

                Assert.IsTrue(combo.Items.Count > 0, "the font list should offer at least one monospace font");
                string fontName = combo.Items[0] as string;

                combo.SelectedIndex = 0;
                size.Value = 14m;
                typeof(PreferencesDialog)
                    .GetMethod("UpdateFontPreview", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(dlg, null);

                Assert.AreEqual(fontName, preview.Font.Name, "preview must use the selected font");
                Assert.AreEqual(14f, preview.Font.Size, 0.01f, "preview must use the selected size");
            }
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }
    }
}
