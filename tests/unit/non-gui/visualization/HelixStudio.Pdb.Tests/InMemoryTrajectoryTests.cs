namespace HelixPythonNamespace.Pdb.Tests
{
    using System;
    using HelixPythonNamespace.Pdb.Io;
    using HelixPythonNamespace.Pdb.Model;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// A trajectory whose coordinates are being produced rather than read.
    ///
    /// <para>Added for the CG designer's live 3D view: a running simulation streams its current
    /// conformation into the IDE, and the point of feeding it through <see cref="Trajectory"/> is
    /// that it is then drawn by the SAME GPU viewer as a structure off disk, with the same
    /// representations and the same camera, instead of by a second renderer that would drift.</para>
    /// </summary>
    [TestClass]
    public class InMemoryTrajectoryTests
    {
        private static Topology TwoAtoms()
        {
            var topology = new Topology
            {
                AtomCount = 2,
                AtomName = new[] { "CA", "CA" },
                ResName = new[] { "GLY", "GLY" },
                ResSeq = new[] { 1, 2 },
                ChainId = new[] { "A", "A" },
                Element = new[] { "C", "C" },
                Occupancy = new[] { 1f, 1f },
                Beta = new float[2],
                AtomSegment = new int[2],
            };
            topology.Chains.Add(new ChainSegment { Start = 0, Count = 2, ChainId = "A" });
            topology.ChainIndexById["A"] = 0;
            return topology;
        }

        [TestMethod]
        public void ATrajectoryCanBeBuiltWithNoFileBehindIt()
        {
            var frame = new float[] { 0, 0, 0, 3.8f, 0, 0 };
            using (Trajectory trajectory = Trajectory.FromMemory(TwoAtoms(), new[] { frame }))
            {
                Assert.AreEqual(1, trajectory.FrameCount);
                Assert.AreEqual(2, trajectory.AtomCount);
                CollectionAssert.AreEqual(frame, trajectory.GetFrame(0));
                Assert.IsFalse(trajectory.FirstFrameBounds.IsEmpty,
                    "the bounds were not measured, so the camera has nothing to fit to");
            }
        }

        [TestMethod]
        public void ReplacingAFrameIsVisibleToTheNextRead()
        {
            // The whole point, and the thing that does not work without FrameCache.Invalidate: the
            // cache hands out a COPY of every frame it has served, so a caller overwriting the
            // array it handed in would keep being shown the first conformation — which, on a live
            // simulation, looks exactly like a run that has frozen.
            var frame = new float[] { 0, 0, 0, 3.8f, 0, 0 };
            using (Trajectory trajectory = Trajectory.FromMemory(TwoAtoms(), new[] { frame }))
            {
                CollectionAssert.AreEqual(frame, trajectory.GetFrame(0));   // populates the cache

                var moved = new float[] { 0, 0, 0, 0, 3.8f, 0 };
                trajectory.ReplaceFrame(0, moved);

                CollectionAssert.AreEqual(moved, trajectory.GetFrame(0),
                    "the viewer would still be drawing the previous conformation");
            }
        }

        [TestMethod]
        public void ReplacingAFrameOnAFileBackedTrajectoryIsRefused()
        {
            // The negative control. Writing into a trajectory that reads from disk would produce a
            // view that disagrees with the file it claims to be showing, so it is refused rather
            // than half-supported.
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix_traj_" + Guid.NewGuid().ToString("N") + ".pdb");
            System.IO.File.WriteAllText(path,
                "MODEL        1\n"
                + "ATOM      1  CA  GLY A   1       0.000   0.000   0.000  1.00  0.00           C\n"
                + "ATOM      2  CA  GLY A   2       3.800   0.000   0.000  1.00  0.00           C\n"
                + "ENDMDL\nEND\n");
            try
            {
                using (Trajectory trajectory = Trajectory.Open(path))
                {
                    try
                    {
                        trajectory.ReplaceFrame(0, new float[6]);
                        Assert.Fail("a file-backed trajectory accepted a frame replacement");
                    }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
            }
        }

        [TestMethod]
        public void AnEmptyFrameListIsRefused()
        {
            try
            {
                Trajectory.FromMemory(TwoAtoms(), new float[0][]);
                Assert.Fail("a trajectory with no frames was accepted");
            }
            catch (ArgumentException) { }
        }
    }
}
