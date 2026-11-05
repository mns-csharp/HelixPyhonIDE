using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Coordinators;

namespace HelixPythonNamespace.App.Tests
{
    // Regression for the "double-click a file and nothing opens" bug.
    //
    // TabControl.TabPages.Insert only registers a page when the control's handle exists. At
    // launch the editor's inner TabControl is hidden (the "No file open" placeholder owns the
    // pane) and therefore has no handle, so the first file opened was parented but never became
    // a tab — and since EditorCoordinator had already recorded it in _openEditorTabs, every
    // later attempt took the "reuse the existing tab" path and the file stayed un-openable.
    [TestClass]
    public class EditorTabRealizationTests
    {
        [TestMethod]
        public void Insert_WithoutAHandle_SilentlyLosesThePage()
        {
            // Documents the WinForms behaviour the fix exists for — if this ever starts passing
            // with Count == 1, the workaround can go.
            using (var tabs = new TabControl())
            using (var page = new TabPage("shapes.py"))
            {
                tabs.Visible = false;
                tabs.TabPages.Insert(0, page);

                Assert.IsFalse(tabs.IsHandleCreated, "no handle expected on a hidden, unparented TabControl");
                Assert.AreEqual(0, tabs.TabPages.Count, "TabPages.Insert unexpectedly registered the page");
                Assert.AreSame(tabs, page.Parent, "the page is parented even though it is not a tab");
            }
        }

        [TestMethod]
        public void EnsureRealized_MakesInsertStick_EvenWhileHidden()
        {
            using (var tabs = new TabControl())
            using (var page = new TabPage("shapes.py"))
            {
                tabs.Visible = false;

                EditorCoordinator.EnsureRealized(tabs);
                tabs.TabPages.Insert(0, page);

                Assert.IsTrue(tabs.IsHandleCreated, "EnsureRealized should force the native handle");
                Assert.AreEqual(1, tabs.TabPages.Count, "the opened file must become a real tab");
                Assert.AreSame(page, tabs.TabPages[0]);
            }
        }

        [TestMethod]
        public void EnsureRealized_IsSafeOnNullAndOnAnAlreadyRealizedControl()
        {
            EditorCoordinator.EnsureRealized(null);

            using (var tabs = new TabControl())
            {
                EditorCoordinator.EnsureRealized(tabs);
                System.IntPtr first = tabs.Handle;
                EditorCoordinator.EnsureRealized(tabs);
                Assert.AreEqual(first, tabs.Handle, "the handle must not be recreated");
            }
        }
    }
}
