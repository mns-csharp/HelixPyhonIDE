using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 311: the Edit menu had no DropDownOpening handler, so Undo/Redo were permanently enabled
    /// even with an empty command stack. They now grey out when nothing can be undone/redone. Guards
    /// that opening the Edit menu with an empty (or absent) command stack disables both.
    /// </summary>
    [TestClass]
    public class EditMenuUndoStateTests
    {
        [TestMethod]
        public void EditMenuOpening_DisablesCommands_WhenNothingApplies()
        {
            using (var form = new MainForm())
            {
                var names = new[] { "undoMenuItem", "redoMenuItem", "cutMenuItem", "copyMenuItem", "pasteMenuItem", "deleteMenuItem" };
                var items = new System.Collections.Generic.Dictionary<string, ToolStripMenuItem>();
                foreach (var n in names) { var it = Field<ToolStripMenuItem>(form, n); it.Enabled = true; items[n] = it; }

                MethodInfo m = typeof(MainForm).GetMethod(
                    "OnEditMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(m, "OnEditMenuOpening expected");
                m.Invoke(form, new object[] { form, EventArgs.Empty });

                // A bare form has no command stack, no canvas selection, and no active designer tab, so
                // every state-dependent Edit command should grey out.
                foreach (var n in names)
                    Assert.IsFalse(items[n].Enabled, n + " should be disabled when nothing applies");
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
