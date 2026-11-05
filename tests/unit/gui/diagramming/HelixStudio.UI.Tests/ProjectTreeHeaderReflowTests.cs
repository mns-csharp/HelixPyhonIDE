using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The Solution Explorer header row at realistic dock widths.
    ///
    /// Its four fixed-width controls — kind filter, expand, collapse, sort — want 252px between
    /// them, which is more than the panel's own default dock width. The Fill'd search box took
    /// whatever was left over, so at every ordinary width that was zero and the control disappeared
    /// with nothing to say it existed. It now drops to a row of its own instead.
    /// </summary>
    [TestClass]
    public class ProjectTreeHeaderReflowTests
    {
        private static T Field<T>(ProjectTreePanel panel, string name) where T : class
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected.");
            return (T)f.GetValue(panel);
        }

        private static TextBox SearchBox(ProjectTreePanel panel) { return Field<TextBox>(panel, "_searchBox"); }
        private static Panel ToolRow(ProjectTreePanel panel) { return Field<Panel>(panel, "_headerToolRow"); }
        private static Panel SearchRow(ProjectTreePanel panel) { return Field<Panel>(panel, "_headerSearchRow"); }

        private static bool Wrapped(ProjectTreePanel panel)
        {
            return (bool)typeof(ProjectTreePanel)
                .GetField("_headerWrapped", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(panel);
        }

        [TestMethod]
        public void AtTheDefaultDockWidth_TheSearchBoxIsStillOnScreen()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.Size = new System.Drawing.Size(240, 600);

                Assert.IsTrue(Wrapped(panel), "240px cannot hold 252px of fixed controls plus a search box");
                Assert.AreSame(SearchRow(panel), SearchBox(panel).Parent,
                    "the search box must move to its own row rather than be squeezed to nothing");
                Assert.IsTrue(SearchRow(panel).Height > 0, "its row must have height to show it in");
            }
        }

        [TestMethod]
        public void Wide_TheHeaderStaysOneRow()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.Size = new System.Drawing.Size(600, 600);

                Assert.IsFalse(Wrapped(panel), "600px has room for everything on one row");
                Assert.AreSame(ToolRow(panel), SearchBox(panel).Parent);
            }
        }

        /// <summary>
        /// Coming back from narrow must restore the ORIGINAL z-order: docked children are laid out
        /// from the end of the collection, so a Fill re-added at the end would be laid out first and
        /// swallow the row, leaving the four fixed controls painted on top of it.
        /// </summary>
        [TestMethod]
        public void NarrowThenWide_PutsTheSearchBoxBackAsTheFill_NotOverTheButtons()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.Size = new System.Drawing.Size(200, 600);
                Assert.IsTrue(Wrapped(panel));

                panel.Size = new System.Drawing.Size(700, 600);

                Assert.IsFalse(Wrapped(panel), "the row unwraps once there is room again");
                Panel row = ToolRow(panel);
                Assert.AreSame(row, SearchBox(panel).Parent);
                Assert.AreEqual(0, row.Controls.GetChildIndex(SearchBox(panel)),
                    "the Fill must be back at index 0 so the edges still reserve their space");
            }
        }

        [TestMethod]
        public void ResizingBackAndForth_LeavesExactlyOneCopyOfTheSearchBox()
        {
            using (var panel = new ProjectTreePanel())
            {
                for (int i = 0; i < 4; i++)
                {
                    panel.Size = new System.Drawing.Size(200, 600);
                    panel.Size = new System.Drawing.Size(700, 600);
                }

                Assert.AreEqual(0, SearchRow(panel).Controls.Count, "no stale copy left behind");
                Assert.AreSame(ToolRow(panel), SearchBox(panel).Parent);
            }
        }
    }
}
