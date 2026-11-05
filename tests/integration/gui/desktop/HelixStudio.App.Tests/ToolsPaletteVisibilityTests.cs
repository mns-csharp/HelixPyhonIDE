using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Why the Class Designer Canvas sometimes had its Select/Pan/I/A/C buttons and sometimes had none.
    ///
    /// The palette is shown and hidden from the main TabControl's SelectedIndexChanged, on the rule
    /// "visible only while the Class Designer Canvas tab is the active one". A TabControl raises that event
    /// while a page is being REMOVED, at a moment when SelectedTab is null — and startup removes
    /// pages, closing the Welcome tab and whatever the solution gate hides. The handler read the
    /// null as "the user is on some other tab" and hid the palette; the control then settled on a
    /// real page WITHOUT raising the event again, so nothing ever brought it back, and the canvas
    /// spent the rest of the session with an empty toolbar.
    ///
    /// Intermittent because it depended on whether startup happened to remove a tab at all: a
    /// session that kept its Welcome tab got one last real selection event and kept the palette.
    /// </summary>
    [TestClass]
    public class ToolsPaletteVisibilityTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object Get(MainForm form, string field)
        { return typeof(MainForm).GetField(field, Private).GetValue(form); }

        private static void Call(MainForm form, string method)
        { typeof(MainForm).GetMethod(method, Private).Invoke(form, null); }

        /// <summary>
        /// The palette's OWN visibility bit — the one <see cref="Control.Hide"/> clears.
        ///
        /// Not <c>Visible</c>: that property is the whole ancestor chain, and a headless test never
        /// shows the IDE window, so it reads false for every control on the form no matter what.
        /// Asserting on it would pass whatever the code did, which is how a test ends up proving
        /// nothing at all.
        /// </summary>
        private static bool SelfVisible(Control control)
        {
            MethodInfo getState = typeof(Control).GetMethod("GetState", Private);
            Assert.IsNotNull(getState, "Control.GetState is gone; this reader needs rewriting");
            const int StateVisible = 0x00000002;
            return (bool)getState.Invoke(control, new object[] { StateVisible });
        }

        private static void WithShell(Action<MainForm, TabControl, TabPage> check)
        {
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                var tabs = (TabControl)Get(form, "_mainTabControl");
                var canvasTab = (TabPage)Get(form, "_graphicalDesignerTab");
                Assert.IsNotNull(tabs);
                Assert.IsNotNull(canvasTab);
                check(form, tabs, canvasTab);
            }
            finally { form.Dispose(); }
        }

        private static Form EnabledPalette(MainForm form, TabControl tabs, TabPage canvasTab)
        {
            if (!tabs.TabPages.Contains(canvasTab)) tabs.TabPages.Add(canvasTab);
            tabs.SelectedTab = canvasTab;
            Assert.AreSame(canvasTab, tabs.SelectedTab,
                "precondition: the Class Designer Canvas tab is the selected one");
            Call(form, "ShowToolsPalette");
            var palette = (Form)Get(form, "_toolsPalette");
            Assert.IsNotNull(palette, "precondition: enabling the palette on the canvas tab creates it");
            Assert.IsTrue(SelfVisible(palette), "precondition: it is showing before anything is removed");
            return palette;
        }

        /// <summary>
        /// The reported symptom, reduced to its cause: a TabControl with nothing selected.
        /// </summary>
        [TestMethod]
        public void ATabControlWithNothingSelectedDoesNotTakeThePaletteAway()
        {
            WithShell((form, tabs, canvasTab) =>
            {
                Form palette = EnabledPalette(form, tabs, canvasTab);

                // Exactly the state a page removal passes through, and the state the running IDE
                // was logged in: SelectedTab null, SelectedIndexChanged raised.
                tabs.SelectedIndex = -1;
                Assert.IsNull(tabs.SelectedTab, "precondition: the control really has no selection");
                Call(form, "UpdateToolsPaletteVisibility");

                Assert.IsTrue(SelfVisible(palette),
                    "the palette was hidden while the tab strip was mid-change, and nothing raises "
                    + "the event again to bring it back — this is the empty toolbar the user saw");
            });
        }

        /// <summary>
        /// The control that stops the above from being satisfied by a handler that simply never
        /// hides anything: on a REAL other tab, the palette still goes away.
        /// </summary>
        [TestMethod]
        public void SelectingAnotherTabStillHidesThePalette()
        {
            WithShell((form, tabs, canvasTab) =>
            {
                Form palette = EnabledPalette(form, tabs, canvasTab);

                TabPage other = tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p != canvasTab);
                Assert.IsNotNull(other, "the shell needs a second tab for this control to mean anything");
                tabs.SelectedTab = other;
                Call(form, "UpdateToolsPaletteVisibility");

                Assert.IsFalse(SelfVisible(palette),
                    "the palette belongs to the canvas and must not sit on another tab's toolbar");

                // And coming back brings it back, which is what makes it a tool and not a one-shot.
                tabs.SelectedTab = canvasTab;
                Call(form, "UpdateToolsPaletteVisibility");
                Assert.IsTrue(SelfVisible(palette), "returning to the canvas did not restore the palette");
            });
        }

        /// <summary>
        /// The same trap one level down: during Form.Load the INNER tab control has no selection
        /// yet, because its handle does not exist. Read as "not the class diagram", that hid the
        /// palette before it was ever shown.
        /// </summary>
        [TestMethod]
        public void AnUnselectedInnerTabControlDoesNotTakeThePaletteAway()
        {
            WithShell((form, tabs, canvasTab) =>
            {
                Form palette = EnabledPalette(form, tabs, canvasTab);

                var inner = (TabControl)Get(form, "_designerInnerTabControl");
                Assert.IsNotNull(inner);
                inner.SelectedIndex = -1;
                Call(form, "UpdateToolsPaletteVisibility");

                Assert.IsTrue(SelfVisible(palette),
                    "an inner tab control that has not chosen a page yet is not a user on another surface");
            });
        }

        /// <summary>
        /// Closing the palette with its X is a decision, not a transient, and must still stick —
        /// the guard must not have made the palette impossible to put away.
        /// </summary>
        [TestMethod]
        public void TurningThePaletteOffStillTurnsItOff()
        {
            WithShell((form, tabs, canvasTab) =>
            {
                Form palette = EnabledPalette(form, tabs, canvasTab);

                Call(form, "HideToolsPalette");
                Assert.IsFalse(SelfVisible(palette), "the palette could not be turned off");

                // And it stays off across the tab churn that used to turn it off by accident.
                tabs.SelectedIndex = -1;
                Call(form, "UpdateToolsPaletteVisibility");
                tabs.SelectedTab = canvasTab;
                Call(form, "UpdateToolsPaletteVisibility");
                Assert.IsFalse(SelfVisible(palette), "the palette came back after the user closed it");
            });
        }
    }
}
