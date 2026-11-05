using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public sealed class WorkbenchDocumentRegistryTests
    {
        [TestMethod]
        public void Registry_TracksOwnerAndRemovalIndependentlyOfView()
        {
            var registry = new WorkbenchDocumentRegistry();
            using (var editor = new TabPage())
            using (var preview = new TabPage())
            {
                registry["a.cs"] = editor;
                registry.Register("diagram:x", preview, "diagram-preview");
                Assert.AreEqual(editor, registry["a.cs"]);
                Assert.AreEqual(1, registry.OwnedBy("editor").Count);
                Assert.AreEqual("diagram:x", registry.OwnedBy("diagram-preview").Single().Key);
                registry.Remove("diagram:x");
                Assert.AreEqual(1, registry.Documents.Count);
            }
        }
    }
}
