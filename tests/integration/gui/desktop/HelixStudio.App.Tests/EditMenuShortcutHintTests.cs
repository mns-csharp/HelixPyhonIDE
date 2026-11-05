using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 215: the Edit menu (Undo/Redo/Cut/Copy/Paste/Delete) showed no shortcut hints, hurting
    /// discoverability. The keys are routed by focus (canvas keys in IdleState/ProcessCmdKey; the
    /// editor keeps Scintilla's native Ctrl+Z/X/C/V), so the hints are DISPLAY-ONLY
    /// (ShortcutKeyDisplayString) — setting real ShortcutKeys would dispatch globally and hijack the
    /// editor. This verifies the hints are present AND that no global ShortcutKeys were set.
    /// </summary>
    [TestClass]
    public class EditMenuShortcutHintTests
    {
        [TestMethod]
        public void EditItems_ShowShortcutHints_WithoutGlobalDispatch()
        {
            var expected = new Dictionary<string, string>
            {
                { "undoMenuItem", "Ctrl+Z" },
                { "redoMenuItem", "Ctrl+Y" },
                { "cutMenuItem", "Ctrl+X" },
                { "copyMenuItem", "Ctrl+C" },
                { "pasteMenuItem", "Ctrl+V" },
                { "deleteMenuItem", "Del" },
                { "findTypeMenuItem", "Ctrl+F" },
            };

            using (var form = new MainForm())
            {
                foreach (var kv in expected)
                {
                    FieldInfo f = typeof(MainForm).GetField(kv.Key, BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(f, "Expected MainForm field " + kv.Key);
                    var item = (ToolStripMenuItem)f.GetValue(form);
                    Assert.IsNotNull(item, kv.Key + " was null.");

                    Assert.AreEqual(kv.Value, item.ShortcutKeyDisplayString, kv.Key + " hint");
                    // Must NOT be a real (dispatched) shortcut — that would hijack the editor's
                    // native Ctrl+Z/X/C/V when it has focus.
                    Assert.AreEqual(Keys.None, item.ShortcutKeys,
                        kv.Key + " must use a display-only hint, not a dispatched ShortcutKeys.");
                }
            }
        }
    }
}
