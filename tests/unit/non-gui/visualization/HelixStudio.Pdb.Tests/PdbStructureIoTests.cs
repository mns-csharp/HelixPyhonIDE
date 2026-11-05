using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Pdb.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// The editable file layer: reading every coordinate field, and writing the file back out with
    /// atoms inserted without disturbing anything else in it.
    ///
    /// The round-trip assertions are the load-bearing ones. Adding hydrogens rewrites somebody's
    /// deposition, and the promise this layer makes is that nothing but the added lines and the
    /// serial numbers changes. A writer that silently dropped REMARK blocks, or renumbered atoms
    /// without fixing the CONECT records that point at them, would pass every count assertion and
    /// hand the user a corrupted file.
    /// </summary>
    [TestClass]
    public class PdbStructureIoTests
    {
        private static PdbStructure Open(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            Assert.IsTrue(File.Exists(path), "missing fixture " + fixture);
            return PdbStructureReader.Read(path);
        }

        [TestMethod]
        public void HybridThirtySixEncodingRoundTripsPastTheSerialCeiling()
        {
            // Adding hydrogens roughly doubles an atom count, which is exactly how a large structure
            // crosses 99999 and overflows the five-wide serial field.
            Assert.AreEqual("    1", PdbStructureWriter.Hy36Encode(5, 1));
            Assert.AreEqual("99999", PdbStructureWriter.Hy36Encode(5, 99999));
            Assert.AreEqual("A0000", PdbStructureWriter.Hy36Encode(5, 100000));

            int[] probes = { 0, 1, 12345, 99999, 100000, 100001, 200000, 1000000 };
            foreach (int value in probes)
                Assert.AreEqual(value, PdbStructureWriter.Hy36Decode(PdbStructureWriter.Hy36Encode(5, value)),
                    "hybrid-36 must round-trip " + value);
        }

        [TestMethod]
        public void TheAtomNameColumnsFollowTheIndentationRule()
        {
            // " CA " is an alpha carbon; "CA  " is a calcium ion. The one-column indent for a
            // one-letter element is the only thing that separates them.
            Assert.AreEqual(" CA ", PdbStructureWriter.FormatAtomName("CA", "C"));
            Assert.AreEqual("CA  ", PdbStructureWriter.FormatAtomName("CA", "CA"));
            Assert.AreEqual(" N  ", PdbStructureWriter.FormatAtomName("N", "N"));
            Assert.AreEqual(" HB2", PdbStructureWriter.FormatAtomName("HB2", "H"));
            Assert.AreEqual("HD21", PdbStructureWriter.FormatAtomName("HD21", "H"));
            Assert.AreEqual("SE  ", PdbStructureWriter.FormatAtomName("SE", "Se"));
        }

        private static int FindNextResidueFirstSerial(PdbStructure structure, PdbResidue residue)
        {
            List<PdbResidue> residues = structure.FirstModel.Chains[0].Residues;
            int index = residues.IndexOf(residue);
            if (index < 0 || index + 1 >= residues.Count) return int.MaxValue;
            return residues[index + 1].Atoms[0].Serial;
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").TrimEnd('\n');
        }
    }
}
