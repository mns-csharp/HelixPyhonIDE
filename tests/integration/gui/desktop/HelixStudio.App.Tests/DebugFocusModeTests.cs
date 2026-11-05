using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Debug focus mode: while a session is live the side panels stand down so the source and the
    /// Debug panel get the full window, and leaving the session gives the layout straight back.
    ///
    /// The two things that are easy to get wrong, and are what these tests hold:
    ///  * hiding the docked panel is NOT enough — it lives in a SplitContainer panel, so a hidden
    ///    child still reserves the splitter's column and the user gets a blank gutter instead of
    ///    the width. Panel1Collapsed is the assertion that means "the space was actually freed".
    ///  * the exit path must restore what was captured on ENTRY, never a hardcoded "visible". A
    ///    user who had already closed the Solution Explorer must not find debugging reopened it.
    /// </summary>
    [TestClass]
    public class DebugFocusModeTests
    {
        /// <summary>
        /// Composes the shell AND shows it off-screen. Realizing it is not optional here:
        /// <c>Control.Visible</c> reports EFFECTIVE visibility, so on an unshown form every control
        /// reads false and a `Visible` assertion would pass or fail for reasons that have nothing to
        /// do with debug focus mode. Parked at (-4000,-4000) like the rest of this suite so it never
        /// takes focus.
        /// </summary>
        private static MainForm ShownForm()
        {
            MainForm form = TestMainForm.CreateComposed();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            form.ShowInTaskbar = false;
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
            return form;
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static SplitContainer LeftSplitter(MainForm form)
        {
            FieldInfo f = typeof(MainForm).GetField("_leftSplitter",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(f, "_leftSplitter expected");
            return (SplitContainer)f.GetValue(form);
        }

        private static Control LeftDock(MainForm form)
        {
            FieldInfo f = typeof(MainForm).GetField("_leftDockPanel",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(f, "_leftDockPanel expected");
            return (Control)f.GetValue(form);
        }

        [TestMethod]
        public void StartingASession_CollapsesTheExplorerColumn()
        {
            using (MainForm form = ShownForm())
            {
                SplitContainer split = LeftSplitter(form);
                Assert.IsFalse(split.Panel1Collapsed, "the explorer column is open to start with");

                form.SetDebugFocusMode(true);

                Assert.IsTrue(form.IsDebugFocusMode);
                Assert.IsTrue(split.Panel1Collapsed,
                    "collapsing the splitter panel is what actually gives the width to the editor — " +
                    "merely hiding the docked panel leaves its column reserved and blank");
                Assert.IsFalse(LeftDock(form).Visible, "and the panel itself is hidden");
            }
        }

        [TestMethod]
        public void EndingTheSession_GivesTheLayoutBack()
        {
            using (MainForm form = ShownForm())
            {
                SplitContainer split = LeftSplitter(form);
                int widthBefore = split.SplitterDistance;

                form.SetDebugFocusMode(true);
                form.SetDebugFocusMode(false);

                Assert.IsFalse(form.IsDebugFocusMode);
                Assert.IsFalse(split.Panel1Collapsed, "the explorer column comes back");
                Assert.IsTrue(LeftDock(form).Visible);
                Assert.AreEqual(widthBefore, split.SplitterDistance,
                    "and at the width the user had, not a default");
            }
        }

        [TestMethod]
        public void AnExplorerTheUserHadAlreadyClosed_StaysClosed()
        {
            // Debugging borrows the space; it does not get to decide the user's layout.
            using (MainForm form = ShownForm())
            {
                SplitContainer split = LeftSplitter(form);
                LeftDock(form).Visible = false;
                split.Panel1Collapsed = true;

                form.SetDebugFocusMode(true);
                form.SetDebugFocusMode(false);

                Assert.IsTrue(split.Panel1Collapsed,
                    "the explorer was closed before the session and must not be reopened by it");
                Assert.IsFalse(LeftDock(form).Visible);
            }
        }

        [TestMethod]
        public void TheToggleIsIdempotent_SoEveryTransitionCanDriveIt()
        {
            // DebugCoordinator.UpdateDebugToolbar calls this on every state change (stop, step,
            // resume, end). Repeated calls in the same direction must not re-capture the collapsed
            // state as if it were the user's — that is how the restore would lose the layout.
            using (MainForm form = ShownForm())
            {
                SplitContainer split = LeftSplitter(form);
                int widthBefore = split.SplitterDistance;

                form.SetDebugFocusMode(true);
                form.SetDebugFocusMode(true);
                form.SetDebugFocusMode(true);
                form.SetDebugFocusMode(false);
                form.SetDebugFocusMode(false);

                Assert.IsFalse(split.Panel1Collapsed);
                Assert.IsTrue(LeftDock(form).Visible);
                Assert.AreEqual(widthBefore, split.SplitterDistance);
            }
        }

        [TestMethod]
        public void ItIsNotPersisted_SoADebugSessionCannotRewriteTheLayoutPreference()
        {
            // The user did not close the Solution Explorer; the IDE borrowed its space. Writing
            // that into settings would turn one run into a permanent preference change.
            using (MainForm form = ShownForm())
            {
                var settings = Field<UserSettingsManager>(form, "_settingsManager");
                bool treeFloatingBefore = settings.Settings.TreeFloating;

                form.SetDebugFocusMode(true);

                Assert.AreEqual(treeFloatingBefore, settings.Settings.TreeFloating,
                    "entering debug focus mode must not touch persisted layout settings");
                form.SetDebugFocusMode(false);
            }
        }
    }
}
