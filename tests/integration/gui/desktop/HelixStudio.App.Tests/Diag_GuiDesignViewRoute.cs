using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class Diag_GuiDesignViewRoute
    {
        [TestMethod]
        public void GuiFormType_MenuRoute_CreatesAndSelectsDesignerTab()
        {
            string fixtureRoot = FindFixtureRoot();
            Assert.IsNotNull(fixtureRoot, "GUI fixture not found");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                TestWindow.ShowOffScreen(form);
                form.OpenSolutionAsync(System.IO.Path.Combine(fixtureRoot, "pyproject.toml"));
                TestMainForm.PumpUntilIdle();

                // `MainWindow`, which is what app_gui/main.py declares and has declared since the
                // fixture was committed. This asked for "MainWindowwww" — a name the fixture has
                // never had in its source, so the lookup could only ever return null and the test
                // could only ever fail. (The layout sidecar carries a stale `main.MainWindowwww`
                // node entry beside the live `main.MainWindow` one, which is where the spelling
                // came from; an orphaned layout entry binds to nothing and is inert.)
                TypeModel guiType = form._currentSolution.FindType("MainWindow");
                Assert.IsNotNull(guiType, "GUI form type must be loaded");
                Assert.IsTrue(GuiFormBridge.IsGuiFormType(guiType), "GUI form type must be recognized");

                FieldInfo mainTabsField = typeof(MainForm).GetField("_mainTabControl", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo graphicalTabField = typeof(MainForm).GetField("_graphicalDesignerTab", BindingFlags.Instance | BindingFlags.NonPublic);
                TabControl mainTabs = (TabControl)mainTabsField.GetValue(form);
                TabPage graphicalTab = (TabPage)graphicalTabField.GetValue(form);
                FieldInfo paletteField = typeof(MainForm).GetField("_toolsPalette", BindingFlags.Instance | BindingFlags.NonPublic);
                // The tab may not be on the strip at all: VisibleTopLevelTabs is persisted to the
                // user's settings file, which every test in this host shares, so a suite that ran
                // earlier and hid the canvas tab leaves it hidden for this one. Selecting a page
                // that is not in the collection silently does nothing, and the palette then never
                // gets built -- which looks exactly like a palette bug and is a test-isolation one.
                if (!mainTabs.TabPages.Contains(graphicalTab)) mainTabs.TabPages.Add(graphicalTab);
                mainTabs.SelectedTab = graphicalTab;
                Assert.AreSame(graphicalTab, mainTabs.SelectedTab,
                    "precondition: the Class Designer Canvas tab is the selected one");
                TestMainForm.PumpUntilIdle();
                MethodInfo showPalette = typeof(MainForm).GetMethod("ShowToolsPalette", BindingFlags.Instance | BindingFlags.NonPublic);
                showPalette.Invoke(form, null);
                Form palette = (Form)paletteField.GetValue(form);
                Assert.IsNotNull(palette, "precondition: Tools palette should be created");
                Assert.IsTrue(((LunaForm)palette).EmbeddedSurface, "Pinned palette must use embedded-surface hosting");
                Assert.AreEqual(DockStyle.None, palette.Dock, "Pinned toolbox uses fixed host coordinates");
                Assert.AreEqual(122, palette.Width, "Compact designer toolbar width");
                Assert.AreEqual(24, palette.Height, "Compact designer toolbar height");
                Assert.AreEqual("designerFilterBar", palette.Parent.Name,
                    "Designer toolbar must be hosted beside the Show combo");
                Assert.AreEqual(160, palette.Left, "Designer toolbar must start immediately after the Show combo");
                Assert.AreEqual(8, form._canvas.DrillUpButtonLeft,
                    "Drill Up can return to the canvas left edge once the toolbar leaves the canvas");
                Assert.AreEqual(FormBorderStyle.None, palette.FormBorderStyle, "Pinned palette must have no movable window chrome");

                form.CloseTab(graphicalTab); // reproduces a user-hidden Class Designer Canvas tab
                Assert.IsFalse(mainTabs.TabPages.Contains(graphicalTab), "precondition: Class Designer Canvas is hidden");

                // The Solution Explorer's own item: "GUI Design View" is a type command the GUI
                // Designer Canvas add-in registers (spec 004, Task B), so it is clicked on the menu
                // the tree builds for the form's node rather than reached through a shell method.
                using (ContextMenuStrip menu = form._projectTreePanel.BuildContextMenuForNode(new TreeNode(guiType.Name) { Tag = guiType }))
                {
                    ToolStripItem view = null;
                    foreach (ToolStripItem item in menu.Items) if (item.Text == "GUI Design View") view = item;
                    Assert.IsNotNull(view, "the form's node has no GUI Design View");
                    Assert.IsTrue(view.Enabled, "GUI Design View is disabled on a recognised form");
                    view.PerformClick();
                }
                TestMainForm.PumpUntilIdle();
                GuiDesignerAddIn gui = form.AddIn<GuiDesignerAddIn>();

                // The design surface has its OWN top-level tab now. Filing it under the Graphic
                // Canvas — the class diagram, a view OF code — made a form design read as a
                // projection of the class model, and hiding that tab took every form design away
                // with it. What this route must still guarantee is unchanged: the design page is
                // created, selected, and its host tab is brought back and forward.
                TabControl tabs = gui._guiDesignerTabs;
                Assert.IsNotNull(tabs, "GUI design tab must be created");
                Assert.IsTrue(tabs.TabPages.Count >= 1, "GUI design tab must be created");
                Assert.IsNotNull(tabs.SelectedTab, "GUI design tab must be selected");
                Assert.IsTrue(tabs.SelectedTab.Text.IndexOf("[Design]", StringComparison.OrdinalIgnoreCase) >= 0,
                    "selected tab must be the GUI design surface");
                Assert.IsTrue(mainTabs.TabPages.Contains(gui._guiDesignerTab), "GUI Design View must re-show its host tab");
                Assert.AreSame(gui._guiDesignerTab, mainTabs.SelectedTab, "GUI Design View must select its host tab");
                Assert.IsFalse(palette.Visible, "Tools palette must be hidden over the GUI/Form Designer");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }

        private static string FindFixtureRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = System.IO.Path.Combine(dir, "tests", "fixtures", "python", "python-test-sample-oop");
                if (System.IO.Directory.Exists(candidate)) return candidate;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
