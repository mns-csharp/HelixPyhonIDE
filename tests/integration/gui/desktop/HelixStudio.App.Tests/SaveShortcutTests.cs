using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 212: Ctrl+S previously saved the window LAYOUT — inverting the strongest convention
    /// in desktop software (Ctrl+S saves the current file). The File menu now has a dedicated
    /// "Save" item ahead of "Save Layout". (The effective Ctrl+S / Ctrl+Shift+S chord mapping is
    /// asserted in HelixPythonNamespace.UI.Tests KeybindingPresetsTests; the KeybindingManager stamps those
    /// onto the menu items only in the full runtime construction path, not this bare designer form.)
    /// </summary>
    [TestClass]
    public class SaveShortcutTests
    {
        private static ToolStripMenuItem MenuItem(MainForm form, string field)
        {
            FieldInfo f = typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "Expected MainForm field " + field);
            var item = (ToolStripMenuItem)f.GetValue(form);
            Assert.IsNotNull(item, field + " was null.");
            return item;
        }

        [TestMethod]
        public void FileMenu_HasDedicatedSaveItem_AheadOfSaveLayout()
        {
            using (var form = new MainForm())
            {
                var fileMenu = MenuItem(form, "fileMenuItem");
                var save = MenuItem(form, "saveFileMenuItem");
                var layout = MenuItem(form, "saveLayoutMenuItem");

                // A distinct file-save action separate from the layout action.
                Assert.AreNotSame(save, layout);
                StringAssert.Contains(save.Text ?? "", "Save");
                Assert.IsFalse((save.Text ?? "").Contains("Layout"),
                    "The Save item must be the file-save action, not Save Layout.");

                int saveIdx = fileMenu.DropDownItems.IndexOf(save);
                int layoutIdx = fileMenu.DropDownItems.IndexOf(layout);
                Assert.IsTrue(saveIdx >= 0, "Save item must be in the File menu.");
                Assert.IsTrue(layoutIdx >= 0, "Save Layout item must be in the File menu.");
                Assert.IsTrue(saveIdx < layoutIdx, "Save should appear before Save Layout.");

                // Mnemonic moved off S so Save (Alt+S) and Save Layout (Alt+L) don't collide.
                StringAssert.Contains(layout.Text ?? "", "&L");
            }
        }
    }
}
