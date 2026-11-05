using System;
using System.Collections.Generic;
using HelixPythonNamespace.Pdb.Chem;
using HelixPythonNamespace.Pdb.Geometry;
using HelixPythonNamespace.Pdb.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// Hydrogen construction, checked against chemistry rather than against itself.
    ///
    /// A geometry builder is easy to test badly: assert the atom count, get a green light, and ship
    /// hydrogens sitting a quarter of an angstrom inside their own carbon. So the assertions here
    /// are bond lengths, bond angles and per-residue hydrogen counts that come from the periodic
    /// table, not from a previous run of this code. If the placement maths inverts a sign, the bond
    /// lengths go wrong and these fail; a count-only test would not notice.
    /// </summary>
    [TestClass]
    public class HydrogenBuilderTests
    {
        /// <summary>Hydrogens per residue for a residue in the middle of a chain, by chemistry.</summary>
        private static readonly Dictionary<string, int> ExpectedHydrogens =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"ALA",5},{"ARG",13},{"ASN",6},{"ASP",4},{"CYS",5},{"GLN",8},{"GLU",6},{"GLY",3},
            {"HIS",7},{"ILE",11},{"LEU",11},{"LYS",13},{"MET",9},{"PHE",9},{"PRO",7},{"SER",5},
            {"THR",7},{"TRP",10},{"TYR",9},{"VAL",9},
        };

        private static PdbStructure Open(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            return PdbStructureReader.Read(path);
        }

        [TestMethod]
        public void InternalCoordinatePlacementReproducesTheAngleAndTorsionItWasGiven()
        {
            // The construction underneath every template. A sign error here would put whole methyl
            // groups on the wrong side of their carbon while every bond length stayed correct.
            var b = new Dvec3(0, 0, 0);
            var a = new Dvec3(1.5, 0, 0);
            var root = new Dvec3(2.1, 1.4, 0);

            foreach (double torsion in new[] { 0.0, 60.0, 120.0, 180.0, -60.0, -120.0 })
            {
                Dvec3 placed = HydrogenGeometry.PlaceByInternalCoordinates(root, a, b, 1.09, 109.5, torsion);

                Assert.AreEqual(1.09, Dvec3.Distance(placed, root), 1e-9, "bond length");
                Assert.AreEqual(109.5, Dvec3.Angle(placed, root, a), 1e-6, "bond angle");

                double actual = Dvec3.Dihedral(placed, root, a, b);
                double expected = torsion;
                double difference = Math.Abs(NormalizeAngle(actual - expected));
                Assert.IsTrue(difference < 1e-6,
                    "torsion " + torsion + " came back as " + actual.ToString("F4"));
            }
        }

        private static double NormalizeAngle(double degrees)
        {
            while (degrees > 180.0) degrees -= 360.0;
            while (degrees <= -180.0) degrees += 360.0;
            return degrees;
        }
    }
}
