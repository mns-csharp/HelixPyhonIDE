using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using HelixPythonNamespace.Pdb.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// The sequence bar's data: one letter per residue with its DSSP assignment beside it, and a
    /// selection that reaches the residue's atoms.
    ///
    /// The anchor is ubiquitin, whose assignment is published: H 23-34; G 38-40 and 57-59;
    /// E 2-7, 12-16, 41-45, 48-49, 66-71; B 22 and 55. A track that reproduces those has not merely
    /// produced a plausible string of letters — it has produced the right one, in the right frame,
    /// numbered the way the depositor numbered it.
    /// </summary>
    [TestClass]
    public class SequenceTrackTests
    {
        private static Trajectory Open(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            return Trajectory.Open(path);
        }

        private static SequenceTrack TrackOf(Trajectory t)
        {
            return SequenceTrack.Build(t.Topology, t.GetFrame(0));
        }

        // ---- what it reads -----------------------------------------------------------------------









        // ---- what it leaves out --------------------------------------------------------------------







        // ---- what a click does ----------------------------------------------------------------------

        [TestMethod]
        public void EveryLetterOfTheAlphabetIsSpeltOut()
        {
            foreach (char c in "HGIEBTSP")
                Assert.AreNotEqual("no secondary structure", SequenceTrack.DescribeStructure(c),
                    "'" + c + "' should have a name");
            Assert.AreEqual("no secondary structure", SequenceTrack.DescribeStructure(' '));
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static void AssertRun(Dictionary<int, char> byNumber, int from, int to, char letter, string what)
        {
            for (int n = from; n <= to; n++)
            {
                Assert.IsTrue(byNumber.ContainsKey(n), "residue " + n + " is missing from the track");
                Assert.AreEqual(letter, byNumber[n],
                    what + ": residue " + n + " should be '" + letter + "'");
            }
        }

        private static Dssp.SsClass ClassOf(char structure)
        {
            switch (structure)
            {
                case 'H': case 'G': case 'I': return Dssp.SsClass.Helix;
                case 'E': case 'B': return Dssp.SsClass.Sheet;
                default: return Dssp.SsClass.Coil;
            }
        }
    }
}
