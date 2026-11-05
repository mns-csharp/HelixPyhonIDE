using System.Windows.Forms;
using HelixPythonNamespace.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Solution Explorer's remembered width, on the way out and on the way back in.
    ///
    /// Reported from a live session: the IDE booted with the explorer filling roughly four fifths of
    /// the window and the Welcome page squeezed into the strip left over. The persisted width was
    /// 994px on a 1280px window. Two faults produced it — the save wrote the raw SplitterDistance
    /// (which measures the TAB area, not the tree, whenever the explorer is docked right), and the
    /// restore accepted any value at all short of the far edge.
    /// </summary>
    [TestClass]
    public class ExplorerWidthRestoreTests
    {
        // Gives the splitter a real width without showing the form: SplitterDistance and the
        // fraction cap are both meaningless against a zero-width container.
        private static MainForm SizedForm()
        {
            MainForm form = TestMainForm.CreateComposed();
            form.Size = new System.Drawing.Size(1280, 900);
            form._leftSplitter.Width = 1280;
            return form;
        }

        [TestMethod]
        public void RememberedWidthWiderThanTheCap_FallsBackToTheDefault()
        {
            using (MainForm form = SizedForm())
            {
                Assert.AreEqual(MainForm.DefaultExplorerWidth, form.RestorableExplorerWidth(994),
                    "an explorer wider than the cap is a bad record, not a preference");
            }
        }

        [TestMethod]
        public void RememberedWidthInsideTheBand_IsHonoured()
        {
            using (MainForm form = SizedForm())
            {
                Assert.AreEqual(320, form.RestorableExplorerWidth(320));
                Assert.AreEqual(MainForm.MinExplorerWidth, form.RestorableExplorerWidth(MainForm.MinExplorerWidth));
            }
        }

        [TestMethod]
        public void RememberedWidthNarrowerThanUsable_FallsBackToTheDefault()
        {
            using (MainForm form = SizedForm())
            {
                Assert.AreEqual(MainForm.DefaultExplorerWidth, form.RestorableExplorerWidth(12),
                    "a sliver of a tree is no more usable than one that swallows the window");
            }
        }

        [TestMethod]
        public void TheCapNeverLetsTheExplorerOutgrowTheDocumentArea()
        {
            using (MainForm form = SizedForm())
            {
                int available = form._leftSplitter.Width - form._leftSplitter.SplitterWidth;
                for (int remembered = 1; remembered < available; remembered += 37)
                {
                    int applied = form.RestorableExplorerWidth(remembered);
                    Assert.IsTrue(applied <= available * MainForm.MaxExplorerWidthFraction + 1,
                        "restored width " + applied + " (from " + remembered + ") exceeds the cap");
                    Assert.IsTrue(applied >= MainForm.MinExplorerWidth,
                        "restored width " + applied + " is below the readable minimum");
                }
            }
        }

        /// <summary>
        /// Docked left the two numbers agree — this is the case that always worked, pinned so the
        /// mirrored branch below cannot be "fixed" by inverting the wrong one.
        /// </summary>
        [TestMethod]
        public void DockedLeft_SavesTheSplitterDistanceItself()
        {
            using (MainForm form = SizedForm())
            {
                form._explorerDockedRight = false;
                form._leftSplitter.SplitterDistance = 260;
                Assert.AreEqual(260, form.CurrentExplorerWidth());
            }
        }

        /// <summary>
        /// The regression itself: docked right, Panel1 holds the tabs, so the raw SplitterDistance is
        /// the DOCUMENT width. Saving it verbatim is what made the next launch open a 994px tree.
        /// </summary>
        [TestMethod]
        public void DockedRight_SavesTheExplorersOwnWidth_NotTheTabAreas()
        {
            using (MainForm form = SizedForm())
            {
                form._explorerDockedRight = true;
                form._leftSplitter.SplitterDistance = 994;

                int expected = form._leftSplitter.Width - 994 - form._leftSplitter.SplitterWidth;
                Assert.AreEqual(expected, form.CurrentExplorerWidth(),
                    "docked right, the explorer is what is left AFTER the splitter, not before it");
                Assert.IsTrue(form.CurrentExplorerWidth() < 994,
                    "the saved number must be the tree's width, which here is the narrower one");
            }
        }

        /// <summary>
        /// Collapsed to its header strip, the splitter measures the strip. Persisting that would
        /// reopen the IDE with no tree at all, so the remembered width is what gets written.
        /// </summary>
        [TestMethod]
        public void Collapsed_SavesTheRememberedWidth_NotTheHeaderStrip()
        {
            using (MainForm form = SizedForm())
            {
                form._explorerDockedRight = false;
                // Collapsing is what records the width to come back to, so the remembered value is
                // set AFTER the toggle — setting it first would just be overwritten by the handler.
                form._leftDockPanel.IsExpanded = false;
                form._savedLeftSplitterDistance = 250;

                Assert.AreEqual(250, form.CurrentExplorerWidth());
            }
        }
    }
}
