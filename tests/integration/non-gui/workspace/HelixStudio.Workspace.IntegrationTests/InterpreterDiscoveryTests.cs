using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Debugger;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class InterpreterDiscoveryTests
    {
        [TestMethod]
        public void DiscoverAll_DoesNotThrow_OnCleanInvocation()
        {
            var mgr = new PythonInterpreterManager();
            var list = mgr.DiscoverAll();
            Assert.IsNotNull(list);
            // The machine may have zero or many Pythons — just assert no exception and
            // that every returned entry has an existing executable path.
            foreach (var i in list)
                Assert.IsTrue(File.Exists(i.ExecutablePath),
                    "Discovered interpreter path must exist: " + i.ExecutablePath);
        }

        [TestMethod]
        public void DiscoverAll_DedupsByCanonicalPath()
        {
            var mgr = new PythonInterpreterManager();
            var list = mgr.DiscoverAll();
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var i in list)
                Assert.IsTrue(seen.Add(Path.GetFullPath(i.ExecutablePath)),
                    "Duplicate path in discovery results: " + i.ExecutablePath);
        }

        [TestMethod]
        public void DiscoverDefault_FallsBackToPathScan()
        {
            var mgr = new PythonInterpreterManager();
            var fallback = mgr.DiscoverDefault();
            // On a dev machine with Python on PATH, this returns an interpreter.
            // On a clean machine, null is acceptable — just assert no throw.
            if (fallback != null)
                Assert.IsTrue(File.Exists(fallback.ExecutablePath));
        }
    }
}
