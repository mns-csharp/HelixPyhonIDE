using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class TomlCompletionEngineTests
    {
        private static System.Collections.Generic.List<CompletionItem> Complete(string src, int caret)
        {
            return new TomlCompletionEngine().GetCompletions(src, caret, "pyproject.toml", null);
        }

        [TestMethod]
        public void InsideProject_OffersPep621Keys()
        {
            string src = "[project]\n";
            var items = Complete(src, src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "name"), "name offered");
            Assert.IsTrue(items.Any(i => i.Label == "dependencies"), "dependencies offered");
            var reqPy = items.FirstOrDefault(i => i.Label == "requires-python");
            Assert.IsNotNull(reqPy);
            StringAssert.Contains(reqPy.Documentation, "Python version", "key carries documentation");
        }

        [TestMethod]
        public void InsideBuildSystem_OffersBuildKeys()
        {
            string src = "[build-system]\n";
            var items = Complete(src, src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "requires"));
            Assert.IsTrue(items.Any(i => i.Label == "build-backend"));
            Assert.IsFalse(items.Any(i => i.Label == "dependencies"), "project keys not offered in build-system");
        }

        [TestMethod]
        public void InsideToolHelixstudio_OffersToolKeys()
        {
            string src = "[tool.helixstudio]\n";
            var items = Complete(src, src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "kind"));
            Assert.IsTrue(items.Any(i => i.Label == "interface-style"));
        }

        [TestMethod]
        public void AtBracketLine_OffersTableHeaders()
        {
            string src = "[project]\nname = \"x\"\n[";
            var items = Complete(src, src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "project"));
            Assert.IsTrue(items.Any(i => i.Label == "build-system"));
            Assert.IsTrue(items.Any(i => i.Label == "tool.helixstudio"));
        }

        [TestMethod]
        public void SecondProjectKey_StillInProjectContext()
        {
            string src = "[project]\nname = \"x\"\nversion = \"1\"\n";
            var items = Complete(src, src.Length);
            // still inside [project] after two keys
            Assert.IsTrue(items.Any(i => i.Label == "description"));
        }
    }
}
