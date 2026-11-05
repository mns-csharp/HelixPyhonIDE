using System;
using System.Linq;
using HelixPythonNamespace.Core.Extensibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class ContributionRegistryTests
    {
        [TestMethod]
        public void SnapshotAndResolution_ArePriorityThenRegistrationOrdered()
        {
            var registry = new ContributionRegistry<string>();
            registry.Register("fallback", "fallback", -100);
            registry.Register("first", "first", 10);
            registry.Register("second", "second", 10);

            CollectionAssert.AreEqual(new[] { "first", "second", "fallback" }, registry.Snapshot().ToArray());
            Assert.AreEqual("second", registry.FirstOrDefault(x => x.StartsWith("s", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void StableIds_RejectDuplicates_AndCanBeUnregistered()
        {
            var registry = new ContributionRegistry<object>();
            registry.Register("tool", new object());
            Assert.ThrowsException<InvalidOperationException>(() => registry.Register("TOOL", new object()));
            Assert.IsTrue(registry.Unregister("Tool"));
            Assert.AreEqual(0, registry.Snapshot().Count);
        }
    }
}
