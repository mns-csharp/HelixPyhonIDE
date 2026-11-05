using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.GuiDesigner.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The GUI Designer Canvas opens as a document tab.
    ///
    /// It used to depend on the argument list. Tools ▸ GUI Designer Canvas passes the active document's
    /// path, and with no file open that path is null, which took the sample-form branch and put a
    /// floating window over the IDE; so did any caller that passed a dialog, or nothing, as the
    /// owner. A designer in a window cannot be read beside the source it writes, which is the main
    /// thing anyone does with one. The host is a property of the running application, so it is
    /// looked up rather than required.
    /// </summary>
    [TestClass]
    public class GuiDesignerOpensAsTabTests
    {
        private static TabPage[] DesignerTabs(MainForm form)
        {
            if (form.AddIn<GuiDesignerAddIn>()._guiDesignerTabs == null) return new TabPage[0];
            return form.AddIn<GuiDesignerAddIn>()._guiDesignerTabs.TabPages.Cast<TabPage>()
                .Where(t => t.Controls.Count > 0 && t.Controls[0] is GuiDesignerForm).ToArray();
        }

        private static GuiDesignerForm[] FloatingDesigners()
        {
            return Application.OpenForms.Cast<Form>().OfType<GuiDesignerForm>()
                .Where(d => d.TopLevel).ToArray();
        }

        /// <summary>
        /// The designer is its OWN top-level tab, not a page inside the Class Designer Canvas.
        ///
        /// Reported from the running IDE: a form design filed under "Class Designer Canvas" reads as a
        /// projection of the class model, which is what that tab is — a view OF code, not a
        /// document the user authors. Hiding it also took every open form design with it.
        /// </summary>
        [TestMethod]
        public void TheDesignerHasItsOwnTopLevelTabRatherThanAPageInsideTheGraphicCanvas()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);

                form.AddIn<GuiDesignerAddIn>().ShowForSource("login_form.py");
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.IsNotNull(form.AddIn<GuiDesignerAddIn>()._guiDesignerTab, "no GUI Designer Canvas tab was created");
                Assert.AreEqual("GUI Designer Canvas", form.AddIn<GuiDesignerAddIn>()._guiDesignerTab.Text);
                Assert.IsTrue(form._mainTabControl.TabPages.Contains(form.AddIn<GuiDesignerAddIn>()._guiDesignerTab),
                    "the GUI Designer Canvas tab is not on the workspace strip beside the Editor");
                Assert.AreSame(form.AddIn<GuiDesignerAddIn>()._guiDesignerTab, form._mainTabControl.SelectedTab,
                    "opening a designer should bring its own tab forward");

                Assert.AreEqual(0, form._designerInnerTabControl.TabPages.Cast<TabPage>()
                        .Count(t => t.Controls.Count > 0 && t.Controls[0] is GuiDesignerForm),
                    "a form design is still filed under the Class Designer Canvas");

                form.AddIn<GuiDesignerAddIn>().CloseGuiDesignerDocuments();
                form.Close();
            }
        }

        /// <summary>The route the Tools menu takes when no document is open.</summary>
        [TestMethod]
        public void ToolsMenuWithNoActiveDocumentOpensATabNotAWindow()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);

                form.AddIn<GuiDesignerAddIn>().ShowForSource(null);
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.AreEqual(1, DesignerTabs(form).Length,
                    "Tools ▸ GUI Designer Canvas with nothing open did not add a designer tab");
                Assert.AreEqual(0, FloatingDesigners().Length,
                    "a GUI Designer Canvas window opened over the IDE instead");

                form.AddIn<GuiDesignerAddIn>().CloseGuiDesignerDocuments();
                form.Close();
            }
        }

        /// <summary>The route the Solution Explorer takes for a real form class.</summary>
        [TestMethod]
        public void OpeningASourceFileOpensATabNotAWindow()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);

                form.AddIn<GuiDesignerAddIn>().ShowForSource("login_form.py");
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                TabPage[] tabs = DesignerTabs(form);
                Assert.AreEqual(1, tabs.Length, "no designer tab was added for the source file");
                StringAssert.Contains(tabs[0].Text, "login_form",
                    "the tab should name the file it is designing: " + tabs[0].Text);
                Assert.AreEqual(0, FloatingDesigners().Length, "a designer window opened as well");

                form.AddIn<GuiDesignerAddIn>().CloseGuiDesignerDocuments();
                form.Close();
            }
        }

        /// <summary>
        /// The owner argument is not what decides this. A menu handler on a dialog, or a caller that
        /// passes null, is still running inside the IDE and still wants a tab.
        ///
        /// Since spec 004 (Task B) there is no owner argument at all: the GUI Designer Canvas is an
        /// add-in, every route opens through its host, and the floating designer window is gone.
        /// The guarantee is the same: a form handed over with no source file opens as a tab.
        /// </summary>
        [TestMethod]
        public void AnOwnerThatIsNotTheShellStillGetsATab()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);

                form.AddIn<GuiDesignerAddIn>().OpenGuiDesignerDocument(new GuiForm { FormClassName = "Dialog1" }, null);
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.AreEqual(1, DesignerTabs(form).Length,
                    "a call that did not name the shell as its owner fell back to a window");
                Assert.AreEqual(0, FloatingDesigners().Length, "a designer window opened over the IDE");

                form.AddIn<GuiDesignerAddIn>().CloseGuiDesignerDocuments();
                form.Close();
            }
        }

        /// <summary>
        /// SRS solution_explorer_rules 9 — closing a solution closes every graphical designer. That
        /// used to be carried entirely by closing the singleton WINDOW; with designers as tabs, the
        /// window branch never fires and the tabs are the whole of the rule.
        /// </summary>
        [TestMethod]
        public void ClosingTheSolutionClosesTheDesignerTabs()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                FixtureSolution.RealizeHeadless(form);

                form.AddIn<GuiDesignerAddIn>().ShowForSource("login_form.py");
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);
                Assert.AreEqual(1, DesignerTabs(form).Length, "precondition: a designer tab is open");

                typeof(MainForm).GetMethod("NewEmptyWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.AreEqual(0, DesignerTabs(form).Length,
                    "a GUI Designer Canvas outlived the solution whose file it was syncing to");

                form.Close();
            }
        }
    }
}
