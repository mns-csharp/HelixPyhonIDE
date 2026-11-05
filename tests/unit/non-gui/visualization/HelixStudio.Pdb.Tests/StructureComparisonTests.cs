using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using HelixPythonNamespace.Pdb.Compare;
using HelixPythonNamespace.Pdb.Geometry;
using HelixPythonNamespace.Pdb.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// Kabsch superposition, cRMSD, the distribution statistics and the distance matrix.
    ///
    /// The fixtures are real PDB files, per the rule that a synthetic snippet is not an acceptance
    /// oracle. The synthetic point sets that do appear are here for the cases a real file cannot
    /// produce on demand — an exact reflection, an exact rotation — where the whole point is that
    /// the expected answer is known in closed form.
    /// </summary>
    [TestClass]
    public class StructureComparisonTests
    {
        // ---- Kabsch -------------------------------------------------------------------------

        private static Dvec3[] SamplePoints()
        {
            // Deliberately not planar and not symmetric: a planar set makes a reflection look like
            // a rotation, which would let the determinant bug through.
            return new[]
            {
                new Dvec3(0.0, 0.0, 0.0),
                new Dvec3(3.8, 0.0, 0.0),
                new Dvec3(5.2, 3.1, 0.4),
                new Dvec3(2.7, 5.6, 2.2),
                new Dvec3(-1.1, 4.4, 3.9),
                new Dvec3(-2.5, 1.2, 1.7),
                new Dvec3(1.3, -2.4, 4.1),
            };
        }

        private static Dvec3[] Transform(Dvec3[] points, double[] m, Dvec3 shift)
        {
            var result = new Dvec3[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                Dvec3 p = points[i];
                result[i] = new Dvec3(
                    m[0] * p.X + m[1] * p.Y + m[2] * p.Z + shift.X,
                    m[3] * p.X + m[4] * p.Y + m[5] * p.Z + shift.Y,
                    m[6] * p.X + m[7] * p.Y + m[8] * p.Z + shift.Z);
            }
            return result;
        }

        private static double[] RotationXyz(double ax, double ay, double az)
        {
            double ca = Math.Cos(ax), sa = Math.Sin(ax);
            double cb = Math.Cos(ay), sb = Math.Sin(ay);
            double cc = Math.Cos(az), sc = Math.Sin(az);
            return new[]
            {
                cb * cc,                      -cb * sc,                      sb,
                sa * sb * cc + ca * sc,       -sa * sb * sc + ca * cc,      -sa * cb,
                -ca * sb * cc + sa * sc,       ca * sb * sc + sa * cc,       ca * cb,
            };
        }

        [TestMethod]
        public void AStructureComparedWithItselfHasZeroCrmsd()
        {
            Dvec3[] points = SamplePoints();
            SuperpositionResult r = Kabsch.Superpose(points, (Dvec3[])points.Clone(), false);

            Assert.IsTrue(r.Succeeded, r.Message);
            Assert.AreEqual(0.0, r.Crmsd, 1e-9);
            Assert.AreEqual(points.Length, r.PointCount);
        }

        [TestMethod]
        public void PureTranslationIsRemovedBySuperposition()
        {
            Dvec3[] a = SamplePoints();
            Dvec3[] b = Transform(a, RotationXyz(0, 0, 0), new Dvec3(120.5, -77.25, 9.0));

            Assert.AreEqual(0.0, Kabsch.Crmsd(a, b), 1e-9);
        }

        [TestMethod]
        public void PureRotationIsRemovedBySuperposition()
        {
            Dvec3[] a = SamplePoints();
            Dvec3[] b = Transform(a, RotationXyz(0.7, -1.3, 2.1), new Dvec3(-4.0, 11.0, 3.0));

            Assert.AreEqual(0.0, Kabsch.Crmsd(a, b), 1e-9);
        }

        /// <summary>
        /// The 1978 correction. A mirror image is NOT superposable by a rotation, and an
        /// implementation that takes the positive root of the smallest eigenvalue unconditionally
        /// reports it as a perfect fit. Break the determinant sign in <c>Kabsch.Superpose</c> and
        /// this is the test that goes red.
        /// </summary>
        [TestMethod]
        public void AMirrorImageIsNotReportedAsAPerfectFit()
        {
            Dvec3[] a = SamplePoints();
            var mirrored = new Dvec3[a.Length];
            for (int i = 0; i < a.Length; i++) mirrored[i] = new Dvec3(a[i].X, a[i].Y, -a[i].Z);

            double crmsd = Kabsch.Crmsd(a, mirrored);

            Assert.IsTrue(crmsd > 1.0,
                "a reflected structure must not superpose onto the original; got " +
                crmsd.ToString("F6", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The eigenvalue route and the "actually move the atoms" route have to agree, or one of
        /// them is wrong. This is what pins the rotation matrix, which the fast path never builds.
        /// </summary>
        [TestMethod]
        public void TheRotationMatrixReproducesTheEigenvalueCrmsd()
        {
            Dvec3[] a = SamplePoints();
            Dvec3[] b = Transform(a, RotationXyz(0.35, 1.1, -0.6), new Dvec3(2, 3, 4));
            // Move one point so the fit is imperfect and the two routes have something to disagree about.
            b[3] = new Dvec3(b[3].X + 1.4, b[3].Y - 0.9, b[3].Z + 0.3);

            SuperpositionResult r = Kabsch.Superpose(a, b, true);
            Assert.IsTrue(r.Succeeded, r.Message);
            Assert.IsNotNull(r.Rotation);

            double sum = 0.0;
            for (int i = 0; i < a.Length; i++)
                sum += Dvec3.DistanceSquared(r.Apply(a[i]), b[i]);
            double byMovingAtoms = Math.Sqrt(sum / a.Length);

            Assert.AreEqual(r.Crmsd, byMovingAtoms, 1e-8,
                "the closed-form cRMSD and the value after applying the rotation must agree");
        }

        [TestMethod]
        public void ADisplacementOfOnePointGivesTheAnalyticCrmsd()
        {
            // Displace one point of a set along a direction the fit cannot rotate away, and the
            // RMSD is bounded above by d/sqrt(n) - equality would need the rest to stay fixed.
            Dvec3[] a = SamplePoints();
            var b = (Dvec3[])a.Clone();
            b[0] = new Dvec3(a[0].X + 1.0, a[0].Y, a[0].Z);

            double crmsd = Kabsch.Crmsd(a, b);

            Assert.IsTrue(crmsd > 0.0, "a displaced point must produce a non-zero cRMSD");
            Assert.IsTrue(crmsd <= 1.0 / Math.Sqrt(a.Length) + 1e-9,
                "cRMSD " + crmsd.ToString("F6", CultureInfo.InvariantCulture) + " exceeds the d/sqrt(n) bound");
        }

        [TestMethod]
        public void MismatchedPointCountsAreRefusedWithBothCountsNamed()
        {
            Dvec3[] a = SamplePoints();
            var b = new Dvec3[a.Length - 1];
            Array.Copy(a, b, b.Length);

            SuperpositionResult r = Kabsch.Superpose(a, b, false);

            Assert.IsFalse(r.Succeeded);
            StringAssert.Contains(r.Message, a.Length.ToString(CultureInfo.InvariantCulture));
            StringAssert.Contains(r.Message, b.Length.ToString(CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void FewerThanThreePointsCannotFixARotation()
        {
            var a = new[] { new Dvec3(0, 0, 0), new Dvec3(1, 0, 0) };
            SuperpositionResult r = Kabsch.Superpose(a, (Dvec3[])a.Clone(), false);

            Assert.IsFalse(r.Succeeded);
            StringAssert.Contains(r.Message, "3 points");
        }

        // ---- point extraction ---------------------------------------------------------------









        // ---- workflow 1: two files, one number ------------------------------------------------









        // ---- workflow 2: statistics -----------------------------------------------------------

        [TestMethod]
        public void MeanMedianAndModeAreComputedOverTheDistribution()
        {
            var values = new List<double> { 1.0, 2.0, 2.0, 3.0, 100.0 };
            CrmsdStatistics stats = CrmsdStatistics.Compute(values, 0.5);

            Assert.AreEqual(5, stats.Count);
            Assert.AreEqual(21.6, stats.Mean, 1e-9);
            Assert.AreEqual(2.0, stats.Median, 1e-9);
            Assert.AreEqual(1.0, stats.Minimum, 1e-9);
            Assert.AreEqual(100.0, stats.Maximum, 1e-9);
            // The 2.0 pair shares a bin; its centre is the mode.
            Assert.IsTrue(stats.Mode >= 2.0 && stats.Mode < 2.5,
                "mode " + stats.Mode.ToString("F3", CultureInfo.InvariantCulture) + " should sit in the [2.0, 2.5) bin");
            Assert.AreEqual(2, stats.ModeBinCount);
        }

        [TestMethod]
        public void MedianOfAnEvenSampleIsTheMidpointOfTheTwoCentralValues()
        {
            var values = new List<double> { 4.0, 1.0, 3.0, 2.0 };
            Assert.AreEqual(2.5, CrmsdStatistics.Compute(values).Median, 1e-9);
        }

        [TestMethod]
        public void ModeReportsItsBinWidthAndOccupancySoANoiseModeIsVisible()
        {
            var values = new List<double>();
            for (int i = 0; i < 1000; i++) values.Add(i * 0.01);   // uniform: every bin holds the same count

            CrmsdStatistics stats = CrmsdStatistics.Compute(values, 0.1);

            Assert.AreEqual(0.1, stats.BinWidth, 1e-12);
            Assert.IsTrue(stats.ModeBinCount > 0);
            Assert.IsTrue(stats.ModeBinCount <= 11,
                "a flat distribution must not report a heavily occupied mode; got " + stats.ModeBinCount);
        }

        [TestMethod]
        public void FailedComparisonsCannotPoisonTheMean()
        {
            var values = new List<double> { 1.0, double.NaN, 3.0, double.PositiveInfinity };
            CrmsdStatistics stats = CrmsdStatistics.Compute(values);

            Assert.AreEqual(2, stats.Count);
            Assert.AreEqual(2.0, stats.Mean, 1e-9);
        }

        [TestMethod]
        public void NoValuesIsReportedAsNoValuesRatherThanZero()
        {
            CrmsdStatistics stats = CrmsdStatistics.Compute(new List<double>());
            Assert.AreEqual(0, stats.Count);
            StringAssert.Contains(stats.Summary(), "no values");
        }

        // ---- workflow 2: the folder sweep and its exclusions -----------------------------------

        [TestMethod]
        public void WildcardMatchingHandlesTheFormsAUserTypes()
        {
            Assert.IsTrue(CrmsdBatchRunner.WildcardMatch("decoy_0001.pdb", "*.pdb"));
            Assert.IsTrue(CrmsdBatchRunner.WildcardMatch("native_h.pdb", "*_h.pdb"));
            Assert.IsTrue(CrmsdBatchRunner.WildcardMatch("1UBQ.PDB", "1ubq.pdb"), "matching is case-insensitive");
            Assert.IsTrue(CrmsdBatchRunner.WildcardMatch("model1.pdb", "model?.pdb"));
            Assert.IsFalse(CrmsdBatchRunner.WildcardMatch("model12.pdb", "model?.pdb"));
            Assert.IsFalse(CrmsdBatchRunner.WildcardMatch("decoy.ent", "*.pdb"));
        }



        /// <summary>
        /// Found by sweeping 19,772 culled PDB structures against one 775-residue reference: 8 of
        /// them had exactly 775 CA atoms while being completely different proteins, so positional
        /// pairing produced 30-42 A "measurements" — and those 8 were the entire sample the mean,
        /// median and mode were computed from. Equal counts are not correspondence, and no fixture
        /// small enough to keep in the repo exhibits the collision.
        /// </summary>






        // ---- workflows 3 and 4: the distance matrix -------------------------------------------

        [TestMethod]
        public void TheDistanceMatrixIsSymmetricWithAZeroDiagonal()
        {
            Dvec3[] points = SamplePoints();
            double[,] matrix = DistanceMatrix.Compute(points);

            for (int i = 0; i < points.Length; i++)
            {
                Assert.AreEqual(0.0, matrix[i, i], 1e-12, "the diagonal is a point's distance to itself");
                for (int j = 0; j < points.Length; j++)
                    Assert.AreEqual(matrix[i, j], matrix[j, i], 1e-12, "distance is symmetric");
            }

            Assert.AreEqual(3.8, matrix[0, 1], 1e-12);
        }

        [TestMethod]
        public void ADebugSymbolPdbIsSkippedRatherThanCalledAFailure()
        {
            string temp = Path.Combine(Path.GetTempPath(), "helix_dm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string fake = Path.Combine(temp, "symbols.pdb");
                File.WriteAllBytes(fake, new byte[] { 0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, 0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20 });

                DistanceMatrixItem item = DistanceMatrix.RunOne(fake,
                    new DistanceMatrixOptions { OutputFolder = temp }, CancellationToken.None);

                Assert.IsTrue(item.Skipped, "a program database is not a failed structure");
                Assert.IsFalse(item.Succeeded);
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
        }
    }
}
