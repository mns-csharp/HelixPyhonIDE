using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Toolchains;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// MSVC is the compiler a PATH scan cannot find — cl.exe is only on PATH inside a developer
    /// command prompt — so the Development Environment view reaches it through vswhere instead.
    /// The vswhere call is injected here so the assertions are about the layout walk, not about
    /// whether the machine running the suite has Visual Studio.
    /// </summary>
    [TestClass]
    public class MsvcKitProviderTests
    {
        private string _install;

        [TestInitialize]
        public void MakeFakeVsInstall()
        {
            _install = Path.Combine(Path.GetTempPath(), "helix-msvc-" + Guid.NewGuid().ToString("N"));
            Toolset("14.29.30133");
            Toolset("14.38.33130");
        }

        [TestCleanup]
        public void RemoveFakeVsInstall()
        {
            try { if (Directory.Exists(_install)) Directory.Delete(_install, true); }
            catch (IOException) { }
        }

        private void Toolset(string version)
        {
            string bin = Path.Combine(_install, @"VC\Tools\MSVC", version, @"bin\Hostx64\x64");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "cl.exe"), string.Empty);
        }

        private static IList<InstalledKit> Discover(Func<string> locate)
        {
            return new MsvcKitProvider(locate).Discover(CancellationToken.None);
        }

        [TestMethod]
        public void EveryInstalledToolset_IsListed_NewestFirst()
        {
            IList<InstalledKit> kits = Discover(delegate { return _install; });

            Assert.AreEqual(2, kits.Count);
            Assert.AreEqual("MSVC toolset 14.38.33130", kits[0].Component,
                "the newest toolset is the one a build would pick, so it leads");
            Assert.AreEqual("14.38.33130", kits[0].Version);
            Assert.AreEqual(Path.Combine(_install, @"VC\Tools\MSVC\14.38.33130\bin\Hostx64\x64\cl.exe"),
                kits[0].Path);
            Assert.AreEqual(KitOutcome.Installed, kits[0].Outcome);
            StringAssert.Contains(kits[0].Detail, "vswhere", "the row says how it was found");
            Assert.AreEqual("MSVC toolset 14.29.30133", kits[1].Component);
        }

        [TestMethod]
        public void AToolsetDirectoryWithoutClExe_IsNotClaimed()
        {
            Directory.CreateDirectory(Path.Combine(_install, @"VC\Tools\MSVC\14.99.00000\bin\Hostx64\x64"));

            IList<InstalledKit> kits = Discover(delegate { return _install; });

            Assert.AreEqual(2, kits.Count, "a toolset folder with no compiler in it is not a compiler");
        }

        [TestMethod]
        public void NoVisualStudio_YieldsNothing_RatherThanAnEmptyRow()
        {
            Assert.AreEqual(0, Discover(delegate { return null; }).Count);
            Assert.AreEqual(0, Discover(delegate { return @"C:\nowhere-" + Guid.NewGuid().ToString("N"); }).Count);
        }

        [TestMethod]
        public void AFailingVswhere_DoesNotThrowAtTheScanner()
        {
            IList<InstalledKit> kits = Discover(delegate { throw new InvalidOperationException("vswhere died"); });
            Assert.AreEqual(0, kits.Count);
        }
    }
}
