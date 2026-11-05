using HelixPythonNamespace.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// <see cref="TestEnvironment"/> is the only product code that knows about test runs (book
    /// Chapter 4, §4.6.5). Unset, every decision must be the user's behaviour unchanged: the
    /// clamp onto the screen and the activation still happen, and window styles are untouched.
    /// Pure functions, so nothing is shown.
    /// </summary>
    [TestClass]
    public class TestEnvironmentTests
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [TestMethod]
        public void OnlyTheValueOne_SwitchesItOn()
        {
            Assert.IsFalse(TestEnvironment.IsSet(null), "unset: the user's IDE");
            Assert.IsFalse(TestEnvironment.IsSet(""));
            Assert.IsFalse(TestEnvironment.IsSet("0"));
            Assert.IsFalse(TestEnvironment.IsSet("true"));
            Assert.IsTrue(TestEnvironment.IsSet("1"));
        }

        [TestMethod]
        public void Unset_TheClampAndTheActivationStillHappen_AndStylesAreUntouched()
        {
            Assert.IsTrue(TestEnvironment.ClampOnShownWhen(false), "a user's window is still pulled onto the screen");
            Assert.IsTrue(TestEnvironment.MayActivateWhen(false), "a user's window is still activated");
            Assert.AreEqual(WS_EX_TOOLWINDOW, TestEnvironment.ExStyle(WS_POPUP, WS_EX_TOOLWINDOW, false),
                "no style change for users");
        }

        [TestMethod]
        public void Set_NoClamp_NoActivation_AndTopLevelWindowsAreInvisibleAndNeverActivated()
        {
            Assert.IsFalse(TestEnvironment.ClampOnShownWhen(true));
            Assert.IsFalse(TestEnvironment.MayActivateWhen(true));
            Assert.AreEqual(WS_EX_TOOLWINDOW | TestEnvironment.WS_EX_LAYERED | TestEnvironment.WS_EX_NOACTIVATE,
                TestEnvironment.ExStyle(WS_POPUP, WS_EX_TOOLWINDOW, true));
            Assert.AreEqual(0, TestEnvironment.ExStyle(WS_CHILD, 0, true), "a child window is never made layered");
        }

        [TestMethod]
        public void TheOffScreenPlace_IsLeftOfTheWholeVirtualScreen()
        {
            // The right-most window on a 1280-wide desktop, 2000 wide, still ends left of x = 0.
            Assert.IsTrue(TestEnvironment.OffScreenLeft(1279, 1280) + 2000 < 0);
            Assert.IsTrue(TestEnvironment.OffScreenLeft(0, 3840) + 3840 < 0);
        }

        [TestMethod]
        public void ThisTestRun_HasItOn_FromTheRunsettings()
        {
            Assert.IsTrue(TestEnvironment.OffScreen,
                TestEnvironment.Variable + " must come from HelixStudio.runsettings (run with /Settings:HelixStudio.runsettings)");
        }
    }
}
