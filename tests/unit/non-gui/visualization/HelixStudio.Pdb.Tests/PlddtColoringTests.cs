using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Pdb.Geometry;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// Colouring a predicted model by pLDDT, and — just as much — refusing to colour anything else
    /// that way.
    ///
    /// The b-factor column carries two incompatible meanings. In an experimental structure it is
    /// displacement, where 30 is ordinary; in a predicted model it is per-residue confidence, where
    /// 30 is a guess. Reading one as the other produces a picture that is faithful to the numbers
    /// and false about the molecule, so the probe that tells them apart is as much a part of this
    /// feature as the ramp is.
    /// </summary>
    [TestClass]
    public class PlddtColoringTests
    {
        // ---- the ramp --------------------------------------------------------------------------

        [TestMethod]
        public void TheFourBandsAreTheOnesTheDatabasePublishes()
        {
            // AlphaFold DB's own colours. A viewer that renders this ramp in a palette of its own
            // is showing the reader an image they have to re-learn.
            AssertRgb(ColorMaps.PlddtColor(98f), 0, 83, 214, "very high");
            AssertRgb(ColorMaps.PlddtColor(80f), 101, 203, 243, "confident");
            AssertRgb(ColorMaps.PlddtColor(60f), 255, 219, 19, "low");
            AssertRgb(ColorMaps.PlddtColor(30f), 255, 125, 69, "very low");
        }

        [TestMethod]
        public void ABoundaryValueFallsIntoTheLowerBand()
        {
            // The database states the bands as "> 90", "70-90", "50-70", "< 50". A residue sitting
            // exactly on a boundary is reported as the less confident of the two, which is the
            // conservative direction for a confidence score.
            AssertRgb(ColorMaps.PlddtColor(90f), 101, 203, 243, "exactly 90");
            AssertRgb(ColorMaps.PlddtColor(70f), 255, 219, 19, "exactly 70");
            AssertRgb(ColorMaps.PlddtColor(50f), 255, 125, 69, "exactly 50");
        }

        [TestMethod]
        public void AMissingValueIsShownAsTheLeastConfidentBand()
        {
            AssertRgb(ColorMaps.PlddtColor(float.NaN), 255, 125, 69, "NaN");
        }

        [TestMethod]
        public void AModelWrittenOnTheZeroToOneScaleIsRecognised()
        {
            // AlphaFold writes 0-100, but tools that re-write a model routinely normalise it. Read
            // as 0-100, such a file comes out uniformly "very low" — a confident model rendered as
            // a failed one, which is the worst available way for this to be wrong.
            Assert.IsTrue(ColorMaps.LooksLikeFractionalPlddt(new[] { 0.12f, 0.98f, 0.55f }));
            Assert.IsFalse(ColorMaps.LooksLikeFractionalPlddt(new[] { 12f, 98f, 55f }));
            Assert.IsFalse(ColorMaps.LooksLikeFractionalPlddt(new float[0]));
            Assert.IsFalse(ColorMaps.LooksLikeFractionalPlddt(null));
        }

        // ---- over a real predicted model ---------------------------------------------------------





        // ---- telling the two kinds of file apart --------------------------------------------------

        [TestMethod]
        public void AMarkerInTheCoordinatesDoesNotCount()
        {
            // Only the annotation records are read. A chain or residue named after a program should
            // not change how a structure is coloured, and in a multi-gigabyte trajectory a match
            // somewhere in the coordinates is a coincidence.
            string producer;
            Assert.IsFalse(PredictedModelProbe.SaysItIsPredicted(
                "ATOM      1  N   ALPHAFOLD A   1      17.047  14.099   3.625  1.00 13.79           N\n",
                out producer));
            Assert.IsTrue(PredictedModelProbe.SaysItIsPredicted(
                "TITLE     ALPHAFOLD MONOMER V2.0 PREDICTION FOR ALPHA-SYNUCLEIN (P37840)\n",
                out producer));
        }

        [TestMethod]
        public void AMissingOrUnreadableFileIsNotAPrediction()
        {
            string producer;
            Assert.IsFalse(PredictedModelProbe.IsPredictedModel(null, out producer));
            Assert.IsFalse(PredictedModelProbe.IsPredictedModel(string.Empty, out producer));
            Assert.IsFalse(PredictedModelProbe.IsPredictedModel(
                Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".pdb"),
                out producer));
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static Topology TopologyOf(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            Assert.IsTrue(File.Exists(path), "fixture missing: " + path);

            using (Trajectory t = Trajectory.Open(path))
            {
                Assert.IsNotNull(t.Topology, "the trajectory should have produced a topology");
                return t.Topology;
            }
        }

        private static string NameOf(ColorF c)
        {
            if (Same(c, ColorMaps.PlddtVeryHigh)) return "very high";
            if (Same(c, ColorMaps.PlddtConfident)) return "confident";
            if (Same(c, ColorMaps.PlddtLow)) return "low";
            if (Same(c, ColorMaps.PlddtVeryLow)) return "very low";
            return "other";
        }

        private static bool Same(ColorF a, ColorF b)
        {
            return Math.Abs(a.R - b.R) < 1e-4f && Math.Abs(a.G - b.G) < 1e-4f && Math.Abs(a.B - b.B) < 1e-4f;
        }

        private static IEnumerable<string> Describe(Dictionary<string, int> counts)
        {
            foreach (var kv in counts) yield return kv.Key + "=" + kv.Value;
        }

        private static void AssertRgb(ColorF c, int r, int g, int b, string what)
        {
            Assert.AreEqual(r, (int)Math.Round(c.R * 255f), what + " red");
            Assert.AreEqual(g, (int)Math.Round(c.G * 255f), what + " green");
            Assert.AreEqual(b, (int)Math.Round(c.B * 255f), what + " blue");
        }
    }
}
