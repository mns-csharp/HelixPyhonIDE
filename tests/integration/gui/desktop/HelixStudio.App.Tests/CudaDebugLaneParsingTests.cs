using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.CudaEmulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Debug ▸ CUDA Debug Lane…, which is typed by hand and therefore has to be forgiving about
    /// the ways people actually write a coordinate. Parsing is separated from the dialog so it can
    /// be pinned here: a lane silently misread as (0,0,0) would look exactly like a debugger that
    /// ignores the setting.
    /// </summary>
    [TestClass]
    public class CudaDebugLaneParsingTests
    {
        private static void AssertLane(string text, ulong bx, ulong by, ulong bz,
                                       ulong tx, ulong ty, ulong tz)
        {
            Dim3 block, thread;
            Assert.IsTrue(DebugCoordinator.TryParseLane(text, out block, out thread), "rejected: " + text);
            Assert.AreEqual(bx, block.X, "block.x of " + text);
            Assert.AreEqual(by, block.Y, "block.y of " + text);
            Assert.AreEqual(bz, block.Z, "block.z of " + text);
            Assert.AreEqual(tx, thread.X, "thread.x of " + text);
            Assert.AreEqual(ty, thread.Y, "thread.y of " + text);
            Assert.AreEqual(tz, thread.Z, "thread.z of " + text);
        }

        [TestMethod]
        public void FullCoordinateRoundTrips()
        {
            AssertLane("2,3,4 : 5,6,7", 2, 3, 4, 5, 6, 7);
            AssertLane("  0,0,0:1,0,0  ", 0, 0, 0, 1, 0, 0);
        }

        [TestMethod]
        public void MissingComponentsDefaultToZero()
        {
            AssertLane("1 : 5", 1, 0, 0, 5, 0, 0);
            AssertLane("0,1 : 2,3", 0, 1, 0, 2, 3, 0);
        }

        /// <summary>
        /// One half alone is the THREAD. "Which thread?" is the question people have; a small
        /// launch puts everything in block 0, so reading a bare "5" as block 5 would select a lane
        /// that does not exist and stop nowhere.
        /// </summary>
        [TestMethod]
        public void ASingleHalfIsTheThread()
        {
            AssertLane("5", 0, 0, 0, 5, 0, 0);
            AssertLane("5,1,0", 0, 0, 0, 5, 1, 0);
        }

        [TestMethod]
        public void NonsenseIsRejectedRatherThanGuessed()
        {
            Dim3 block, thread;
            Assert.IsFalse(DebugCoordinator.TryParseLane(null, out block, out thread));
            Assert.IsFalse(DebugCoordinator.TryParseLane("", out block, out thread));
            Assert.IsFalse(DebugCoordinator.TryParseLane("x,y,z", out block, out thread));
            Assert.IsFalse(DebugCoordinator.TryParseLane("1,2,3,4 : 0", out block, out thread),
                "a fourth component is not a typo to absorb -- there is no fourth dimension");
            Assert.IsFalse(DebugCoordinator.TryParseLane("0 : 0 : 0", out block, out thread));
            Assert.IsFalse(DebugCoordinator.TryParseLane("-1 : 0", out block, out thread),
                "a negative thread index is not a lane");
        }
    }
}
