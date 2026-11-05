using HelixPythonNamespace.AddIns.GuiDesigner;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// A GUI Designer Canvas lends its Toolbox and Properties to the Solution Explorer's dock while it is
    /// open, and must take them back when it goes away.
    ///
    /// Reported from a live session: closing the Class Designer Canvas tab left both panels sitting in the
    /// dock, Properties still showing the closed form's MainWindow. The release was hooked only on
    /// the designer's INNER tab strip, and closing the top-level tab never touches that strip — so
    /// the handler never ran.
    /// </summary>
    [TestClass]
    public class GuiDesignerPanelReleaseTests
    {
        private static T Field<T>(MainForm form, string name) where T : class
        {
            FieldInfo f = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected.");
            return (T)f.GetValue(form);
        }

        /// <summary>
        /// Reopening the SAME form after closing its tab must dock the panels again.
        ///
        /// Reported cycle: open a form in design view (panels dock correctly), close the Graphic
        /// Canvas tab, reopen the same form — panels come back un-tabbed inside the document. The
        /// close hands them to the designer, but the inner designer page survives the close, so the
        /// reopen takes OpenGuiDesigner's "already open" branch and returned before ever re-docking.
        /// It therefore worked exactly once per session, which is why a single-open test missed it.
        /// </summary>
        [TestMethod]
        public void ReopeningAClosedDesigner_ClaimsThePanelsAgain()
        {
            string source = AddInSource();
            int branch = source.IndexOf("documents.SelectedTab = existing;", System.StringComparison.Ordinal);
            Assert.IsTrue(branch > 0, "the already-open branch of OpenGuiDesigner was not found");

            int returnAt = source.IndexOf("return;", branch, System.StringComparison.Ordinal);
            Assert.IsTrue(returnAt > branch, "the already-open branch should return early");

            string branchBody = source.Substring(branch, returnAt - branch);
            Assert.IsTrue(branchBody.Contains("DockGuiDesignerPanels("),
                "reopening an already-built designer must re-claim its Toolbox and Properties; "
                + "without it the panels dock once per session and every reopen shows them un-tabbed");
        }

        /// <summary>The GUI Designer Canvas add-in's source: the code that was MainForm's moved
        /// there with the add-in (spec 004, Task B).</summary>
        private static string AddInSource()
        {
            var dir = new System.IO.DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "could not locate the repository root from the test output folder");
            string path = System.IO.Path.Combine(dir.FullName, @"src\gui\addins\HelixStudio.AddIns.GuiDesigner\GuiDesignerAddIn.cs");
            Assert.IsTrue(System.IO.File.Exists(path), "GuiDesignerAddIn.cs not found at " + path);
            return System.IO.File.ReadAllText(path);
        }

        /// <summary>
        /// The release must NOT hang off the top-level strip's ControlRemoved.
        ///
        /// That was the first attempt at this fix and it broke the opposite way: the solution gate
        /// and ShowTab remove and re-insert top-level pages purely to reorder them, so the handler
        /// fired during ordinary churn and snatched the panels back moments after the designer
        /// docked them — leaving Toolbox and Properties floating inside the document instead of
        /// tabbed beside the Solution Explorer. Only a deliberate close may release them.
        /// </summary>
        [TestMethod]
        public void VisibilityChurnOnTheTopLevelStrip_DoesNotReleaseTheDesignersPanels()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                var mainTabs = Field<TabControl>(form, "_mainTabControl");
                var designerTab = Field<TabPage>(form, "_graphicalDesignerTab");
                if (mainTabs == null || designerTab == null) Assert.Inconclusive("designer tab not composed");

                GuiDesignerAddIn gui = form.AddIn<GuiDesignerAddIn>();
                FieldInfo toolboxField = typeof(GuiDesignerAddIn).GetField("_guiToolbox", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo propsField = typeof(GuiDesignerAddIn).GetField("_guiProperties", BindingFlags.Instance | BindingFlags.NonPublic);
                toolboxField.SetValue(gui, new Panel());
                propsField.SetValue(gui, new Panel());

                // Exactly what ShowTab/ApplySolutionGate do to reorder the strip.
                if (mainTabs.TabPages.Contains(designerTab))
                {
                    int at = mainTabs.TabPages.IndexOf(designerTab);
                    mainTabs.TabPages.Remove(designerTab);
                    mainTabs.TabPages.Insert(at, designerTab);
                }

                Assert.IsNotNull(toolboxField.GetValue(gui),
                    "re-inserting a tab is not a close and must not take the Toolbox back");
                Assert.IsNotNull(propsField.GetValue(gui),
                    "re-inserting a tab is not a close and must not take Properties back");
            }
        }

        /// <summary>
        /// The behavioural half: with a designer holding the panels, removing the page that contains
        /// it from the top-level strip must hand them back and clear the dock's extra tabs.
        /// </summary>
        [TestMethod]
        public void RemovingTheGraphicCanvasPage_GivesTheBorrowedPanelsBack()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                var mainTabs = Field<TabControl>(form, "_mainTabControl");
                var designerTab = Field<TabPage>(form, "_graphicalDesignerTab");
                if (mainTabs == null || designerTab == null) Assert.Inconclusive("designer tab not composed");

                // Stand in for a docked designer: the release path keys off _guiDesignerWithDockedPanels
                // plus the two panel fields, and clears all three.
                GuiDesignerAddIn gui = form.AddIn<GuiDesignerAddIn>();
                FieldInfo toolboxField = typeof(GuiDesignerAddIn).GetField("_guiToolbox", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo propsField = typeof(GuiDesignerAddIn).GetField("_guiProperties", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(toolboxField);
                Assert.IsNotNull(propsField);

                MethodInfo undock = typeof(GuiDesignerAddIn).GetMethod("UndockGuiDesignerPanels", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(undock, "UndockGuiDesignerPanels is the release routine");

                toolboxField.SetValue(gui, new Panel());
                propsField.SetValue(gui, new Panel());
                undock.Invoke(gui, null);

                Assert.IsNull(toolboxField.GetValue(gui), "the Toolbox must not outlive its designer");
                Assert.IsNull(propsField.GetValue(gui), "Properties must not outlive its designer");
            }
        }
    }
}
