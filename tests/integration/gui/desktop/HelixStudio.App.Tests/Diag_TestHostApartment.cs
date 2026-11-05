using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Reports the apartment state and thread identity the current test runner gives this assembly,
    /// alongside the same facts captured in <c>AssemblyInitialize</c>.
    ///
    /// <para>
    /// Both numbers decide whether this suite can run without putting a crash box on someone's
    /// desktop. MTA means every <c>TreeView</c> with <c>AllowDrop</c> throws
    /// "DragDrop registration did not succeed" the moment it gets a handle; a test thread that
    /// differs from the AssemblyInitialize thread means the <c>Application.ThreadException</c>
    /// guard — which is per-thread — is not attached where that throw happens, and WinForms shows
    /// its own modal dialog instead. This creates no window of its own, so it is safe to run when
    /// the suite is not.
    /// </para>
    /// </summary>
    [TestClass]
    public class Diag_TestHostApartment
    {
        [TestMethod]
        public void Diag_ReportApartmentAndThread()
        {
            Thread t = Thread.CurrentThread;
            string report = "test thread=" + t.ManagedThreadId + " apartment=" + t.GetApartmentState();
            TestAssemblySetup.Trace("apartment", report);
            System.Console.WriteLine(report + " (see test-diagnostics.log for the AssemblyInitialize thread)");
            // The report used to end Inconclusive unconditionally. The one fact it can check on its
            // own is the one the summary names as unsafe: under MTA every AllowDrop TreeView throws.
            Assert.AreEqual(ApartmentState.STA, t.GetApartmentState(),
                "the test thread must be STA, or every AllowDrop TreeView throws on handle creation: " + report);
        }
    }
}
