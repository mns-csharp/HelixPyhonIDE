using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class NavigationHistoryTests
    {
        [TestMethod]
        public void SourceNavigation_BackwardAndForwardRestoreFileAndCaret()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_nav_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string first = Path.Combine(dir, "first.py");
            string second = Path.Combine(dir, "second.py");
            File.WriteAllText(first, "one = 1\ntwo = 2\nthree = 3\n");
            File.WriteAllText(second, "target = 4\n");

            using (var form = TestMainForm.CreateComposed())
            {
                form.OpenInEditor(first, 2, true, 3);
                form.OpenInEditor(second, 1, true, 1);

                form.OnNavigateBackward(null, EventArgs.Empty);
                var back = form.ActiveDocumentTab();
                Assert.IsNotNull(back);
                Assert.AreEqual(Path.GetFullPath(first), Path.GetFullPath(back.FilePath));
                Assert.AreEqual(2, back.Editor.CurrentLine + 1);

                form.OnNavigateForward(null, EventArgs.Empty);
                var forward = form.ActiveDocumentTab();
                Assert.IsNotNull(forward);
                Assert.AreEqual(Path.GetFullPath(second), Path.GetFullPath(forward.FilePath));
                Assert.AreEqual(1, forward.Editor.CurrentLine + 1);
            }
        }
    }
}
