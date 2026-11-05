using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Pdb.Geometry;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// Colouring the molecule by DSSP's own assignment, at two granularities.
    ///
    /// <para>
    /// The oracle is ubiquitin's published secondary structure — H 23-34, G 38-40 and 57-59,
    /// E 2-7, 12-16, 41-45, 48-49, 66-71, B 22 and 55 — so these tests check that the colours land
    /// on the residues the wwPDB entry says are helical and stranded, not merely that the code
    /// returns some colour for every atom. A palette applied to a wrong assignment is a picture that
    /// is confidently wrong, which is worse than a blank one.
    /// </para>
    /// </summary>
    [TestClass]
    public class SecondaryStructureColoringTests
    {
        private Trajectory _t;
        private Topology _top;

        [TestInitialize]
        public void SetUp()
        {
            string path = PdbFixtures.Path_(PdbFixtures.Ubiquitin);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            _t = Trajectory.Open(path);

            // The same single Kabsch-Sander pass the viewer runs on open, filling BOTH the 3-state
            // class and the 8-state letter.
            byte[] cls; int[] ha, hb;
            BackboneExtractor.ComputeSecondaryStructure(_top = _t.Topology, _t.GetFrame(0),
                                                        out cls, out ha, out hb);
            _top.AtomSS = cls;
            _top.AtomSSFrame = 0;
        }

        [TestCleanup]
        public void TearDown()
        {
            if (_t != null) _t.Dispose();
        }

        private ColorF[] Colors(ColorMode mode)
        {
            return ColorMaps.ComputeAtomColors(_top, mode, ColorF.FromBytes(255, 255, 255));
        }

        /// <summary>The first atom index of residue <paramref name="resSeq"/> in chain A.</summary>
        private int AtomOfResidue(int resSeq)
        {
            for (int i = 0; i < _top.AtomCount; i++)
                if (_top.ResSeq[i] == resSeq && _top.ChainId[i] == "A") return i;
            return -1;
        }

















        private void AssertColor(ColorF[] colors, int resSeq, char expectedLetter, string because)
        {
            int atom = AtomOfResidue(resSeq);
            Assert.IsTrue(atom >= 0, "residue " + resSeq + " is in the fixture");
            Assert.AreEqual(expectedLetter, _top.AtomStructure[atom],
                "DSSP assignment for residue " + resSeq);
            Assert.AreEqual(Key(ColorMaps.SecondaryStructureColor(expectedLetter)), Key(colors[atom]), because);
        }

        /// <summary>A comparable form of a colour — ColorF is a float struct, so compare bytes.</summary>
        private static string Key(ColorF c)
        {
            return ((int)Math.Round(c.R * 255)) + "," + ((int)Math.Round(c.G * 255))
                 + "," + ((int)Math.Round(c.B * 255));
        }
    }
}
