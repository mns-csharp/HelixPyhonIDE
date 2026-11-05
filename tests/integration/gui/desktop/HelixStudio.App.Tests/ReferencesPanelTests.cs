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
    /// Iter 367: Find All References now renders in a top-level tab (ReferencesPanel) instead of a
    /// modeless dialog, per the user's request. The panel lists File/Line/Col/Code and, on activation,
    /// raises NavigateRequested with the exact path+line+column so the host lands the caret on the
    /// token (reusing the iter-366 column-aware navigation).
    /// </summary>
    [TestClass]
    public class ReferencesPanelTests
    {
        [TestMethod]
        public void ShowReferences_PopulatesRowsWithLineAndColumn()
        {
            using (var panel = new ReferencesPanel())
            {
                panel.ShowReferences("Point", Hits());
                ListView lv = ListOf(panel);

                Assert.AreEqual(2, lv.Items.Count);
                Assert.AreEqual("8", lv.Items[0].SubItems[1].Text, "Line column");
                Assert.AreEqual("7", lv.Items[0].SubItems[2].Text, "Col column");
                Assert.AreEqual("_vars_demo.py", lv.Items[0].SubItems[0].Text, "File is the leaf name");
            }
        }

        [TestMethod]
        public void ActivatingARow_RaisesNavigateWithPathLineColumn()
        {
            using (var form = new Form())
            using (var panel = new ReferencesPanel { Dock = DockStyle.Fill })
            {
                form.ShowInTaskbar = false;
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);               // realize handles so SelectedItems reflects selection
                Application.DoEvents();

                panel.ShowReferences("Point", Hits());
                ListView lv = ListOf(panel);
                Application.DoEvents();

                string gotPath = null; int gotLine = -1, gotCol = -1;
                panel.NavigateRequested += (p, l, c) => { gotPath = p; gotLine = l; gotCol = c; };

                lv.SelectedItems.Clear();
                lv.Items[1].Selected = true;
                Application.DoEvents();
                typeof(ReferencesPanel).GetMethod("ActivateSelected", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(panel, null);

                Assert.AreEqual(@"D:\repo\_vars_demo.py", gotPath);
                Assert.AreEqual(17, gotLine);
                Assert.AreEqual(9, gotCol, "the reference's column travels to the navigation handler");
                form.Close();
            }
        }

        [TestMethod]
        public void Clear_EmptiesTheList()
        {
            using (var panel = new ReferencesPanel())
            {
                panel.ShowReferences("Point", Hits());
                panel.Clear();
                Assert.AreEqual(0, ListOf(panel).Items.Count);
            }
        }

        private static List<ReferenceHit> Hits()
        {
            return new List<ReferenceHit>
            {
                new ReferenceHit { Path = @"D:\repo\_vars_demo.py", Line = 8,  Column = 7, Preview = "class Point:" },
                new ReferenceHit { Path = @"D:\repo\_vars_demo.py", Line = 17, Column = 9, Preview = "p = Point(3, 4)" },
            };
        }

        private static ListView ListOf(ReferencesPanel panel)
        {
            FieldInfo f = typeof(ReferencesPanel).GetField("_listView", BindingFlags.Instance | BindingFlags.NonPublic);
            return (ListView)f.GetValue(panel);
        }
    }
}
