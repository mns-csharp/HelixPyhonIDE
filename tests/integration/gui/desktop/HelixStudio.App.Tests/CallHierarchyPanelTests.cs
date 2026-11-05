using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Workspace.Services;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 368: Call Hierarchy (incoming calls) now renders in a top-level tab (CallHierarchyPanel),
    /// the sibling of the References tab, instead of a modeless dialog. The panel lists
    /// Caller/File/Line/Col/Code and raises NavigateRequested(path,line,column) so the host lands the
    /// caret on the call site's exact column.
    /// </summary>
    [TestClass]
    public class CallHierarchyPanelTests
    {
        [TestMethod]
        public void ShowCallers_PopulatesRowsWithCallerLineAndColumn()
        {
            using (var panel = new CallHierarchyPanel())
            {
                panel.ShowCallers("helper", Callers());
                ListView lv = ListOf(panel);

                Assert.AreEqual(2, lv.Items.Count);
                Assert.AreEqual("alpha", lv.Items[0].SubItems[0].Text, "Caller label");
                Assert.AreEqual("mod.py", lv.Items[0].SubItems[1].Text, "File leaf name");
                Assert.AreEqual("12", lv.Items[0].SubItems[2].Text, "Line");
                Assert.AreEqual("5", lv.Items[0].SubItems[3].Text, "Col");
            }
        }

        [TestMethod]
        public void ActivatingARow_RaisesNavigateWithPathLineColumn()
        {
            using (var form = new Form())
            using (var panel = new CallHierarchyPanel { Dock = DockStyle.Fill })
            {
                form.ShowInTaskbar = false;
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                panel.ShowCallers("helper", Callers());
                ListView lv = ListOf(panel);
                Application.DoEvents();

                string gotPath = null; int gotLine = -1, gotCol = -1;
                panel.NavigateRequested += (p, l, c) => { gotPath = p; gotLine = l; gotCol = c; };

                lv.SelectedItems.Clear();
                lv.Items[1].Selected = true;
                Application.DoEvents();
                typeof(CallHierarchyPanel).GetMethod("ActivateSelected", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(panel, null);

                Assert.AreEqual(@"D:\repo\mod.py", gotPath);
                Assert.AreEqual(30, gotLine);
                Assert.AreEqual(9, gotCol, "the call site's column travels to the navigation handler");
                form.Close();
            }
        }

        private static List<CallerHit> Callers()
        {
            return new List<CallerHit>
            {
                new CallerHit { CallerLabel = "alpha",       Path = @"D:\repo\mod.py", Line = 12, Column = 5, Preview = "helper()" },
                new CallerHit { CallerLabel = "Service.run", Path = @"D:\repo\mod.py", Line = 30, Column = 9, Preview = "self.helper()" },
            };
        }

        private static ListView ListOf(CallHierarchyPanel panel)
        {
            FieldInfo f = typeof(CallHierarchyPanel).GetField("_listView", BindingFlags.Instance | BindingFlags.NonPublic);
            return (ListView)f.GetValue(panel);
        }
    }
}
