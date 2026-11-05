using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 273: the Keybindings dialog gained a search/filter box (40+ commands were only scrollable).
    /// Typing filters the grid by command name or current shortcut. Guards that filtering narrows the
    /// list, that a non-matching term empties it, and that an empty term restores everything.
    /// </summary>
    [TestClass]
    public class KeyBindingsFilterTests
    {
        [TestMethod]
        public void Filter_NarrowsByCommandOrShortcut_AndRestoresWhenCleared()
        {
            var mgr = new KeybindingManager(new UserSettingsManager());
            // A fresh manager is empty (defaults are seeded during MainForm init); seed a few here.
            mgr.RegisterDefault("edit.delete", "Delete", Keys.Delete);
            mgr.RegisterDefault("edit.rename", "Rename", Keys.F2);
            mgr.RegisterDefault("file.save", "Save", Keys.Control | Keys.S);
            mgr.RegisterDefault("file.open", "Open Solution", Keys.Control | Keys.O);
            mgr.RegisterDefault("edit.find", "Find Type", Keys.Control | Keys.F);
            mgr.RegisterDefault("run.startup", "Run Startup App", Keys.Control | Keys.F5);
            mgr.RegisterDefault("view.zoomIn", "Zoom In", Keys.Control | Keys.Oemplus);
            using (var dlg = new KeyBindingsDialog(mgr))
            {
                var filterBox = (TextBox)Field(dlg, "_filterBox");
                var grid = (DataGridView)Field(dlg, "_grid");

                int total = grid.Rows.Count;
                Assert.IsTrue(total > 5, "expected many bindings, got " + total);

                // A non-matching term empties the grid.
                filterBox.Text = "qzxjw_nomatch";
                Assert.AreEqual(0, grid.Rows.Count, "no rows should match a nonsense filter");

                // A common substring narrows but keeps some, and every visible row matches it.
                filterBox.Text = "e";
                Assert.IsTrue(grid.Rows.Count > 0 && grid.Rows.Count <= total, "'e' should match some rows");
                foreach (DataGridViewRow r in grid.Rows)
                {
                    string cmd = (r.Cells[0].Value ?? "").ToString();
                    string sc = (r.Cells[1].Value ?? "").ToString();
                    Assert.IsTrue(
                        cmd.IndexOf("e", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sc.IndexOf("e", StringComparison.OrdinalIgnoreCase) >= 0,
                        "visible row must match the filter: " + cmd + " / " + sc);
                }

                // Clearing restores the full list.
                filterBox.Text = "";
                Assert.AreEqual(total, grid.Rows.Count, "clearing the filter restores all bindings");
            }
        }

        private static object Field(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "field " + name + " expected");
            return f.GetValue(obj);
        }
    }
}
