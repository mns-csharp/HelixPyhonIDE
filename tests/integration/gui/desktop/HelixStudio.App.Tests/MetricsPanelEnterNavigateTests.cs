using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 300: the Metrics panel's class/function lists navigated to source on double-click but not
    /// on Enter — the only results list missing Enter (Dead Code / Tests / Analysis / Build all have
    /// it). Enter now activates too. Guards that pressing Enter on a located row raises ItemActivated.
    /// </summary>
    [TestClass]
    public class MetricsPanelEnterNavigateTests
    {
        [TestMethod]
        public void EnterOnAClassRow_NavigatesToItsSource()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);       // on-screen so the ListView realises its handle
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var panel = new MetricsPanel { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                var classes = (ListView)Field(panel, "_classes");

                // Make the Classes tab active so the ListView realises its handle and selection sticks.
                Control c = classes;
                while (c != null && !(c is TabPage)) c = c.Parent;
                if (c is TabPage tp && tp.Parent is TabControl tc) tc.SelectedTab = tp;
                classes.CreateControl();
                Application.DoEvents();

                // Build a located row (Tag = Loc("f.py", 42)).
                Type locType = typeof(MetricsPanel).GetNestedType("Loc", BindingFlags.NonPublic);
                Assert.IsNotNull(locType, "Loc nested type expected");
                object loc = Activator.CreateInstance(locType, "f.py", 42);
                var item = new ListViewItem("MyClass") { Tag = loc };
                classes.Items.Add(item);
                item.Selected = true;
                classes.Select();
                Application.DoEvents();

                string gotFile = null; int gotLine = -1;
                panel.ItemActivated += (f, l) => { gotFile = f; gotLine = l; };

                // Raise Enter on the list — should route to the same navigation as double-click.
                var onKeyDown = typeof(ListView).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
                onKeyDown.Invoke(classes, new object[] { new KeyEventArgs(Keys.Enter) });

                Assert.AreEqual("f.py", gotFile, "Enter should navigate to the row's file");
                Assert.AreEqual(42, gotLine, "Enter should navigate to the row's line");

                form.Close();
            }
        }

        private static object Field(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return f.GetValue(obj);
        }
    }
}
