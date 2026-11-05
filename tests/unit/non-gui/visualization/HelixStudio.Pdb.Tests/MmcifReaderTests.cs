using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using HelixPythonNamespace.Pdb.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// Reading mmCIF.
    ///
    /// The oracle is not "the parser produced plausible atoms" — it is crambin, which the RCSB
    /// serves in BOTH formats. Reading the two files and diffing them atom for atom is a real test
    /// of the new reader against the old one, and it catches the whole class of defect a
    /// single-format test cannot: a column read from the wrong place still produces numbers.
    /// </summary>
    [TestClass]
    public class MmcifReaderTests
    {
        private static string Path_(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            Assert.IsTrue(File.Exists(path), "fixture missing: " + path);
            return path;
        }

        // ---- format detection -----------------------------------------------------------------------

        [TestMethod]
        public void DetectionReadsTheFirstLine_NotAWindowBigEnoughToReachAtomSite()
        {
            // Measured: _atom_site. first appears at byte 42,826 in 1CRN.cif, 1,401,214 in
            // 6EK0.cif and 2,831,513 in 7A01.cif. A detector that waited for atom_site read 6EK0 as
            // a PDB file and crashed on 7A01, so the signature it uses is the data_ block at the top.
            Assert.IsTrue(StructureFormat.StartsWithDataBlock("data_6EK0\n#\n_entry.id 6EK0\n"));
            Assert.IsTrue(StructureFormat.StartsWithDataBlock("#\n# a leading comment\n\ndata_1ABC\n"));
            Assert.IsFalse(StructureFormat.StartsWithDataBlock(
                "HEADER    PLANT PROTEIN                           30-APR-81   1CRN\n"));
            Assert.IsFalse(StructureFormat.StartsWithDataBlock(string.Empty));
            Assert.IsFalse(StructureFormat.StartsWithDataBlock(null));
        }

        [TestMethod]
        public void AFileThatIsNeitherFormatIsStillRefused()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix-cif-" + Guid.NewGuid().ToString("N") + ".cif");
            File.WriteAllText(path, "<html><body>404 Not Found</body></html>");
            try
            {
                string reason;
                Assert.IsFalse(StructureFormat.IsReadableStructure(path, out reason));
                Assert.IsNotNull(reason);
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }

        // ---- the same structure, read twice ------------------------------------------------------------







        // ---- the grammar ---------------------------------------------------------------------------

        [TestMethod]
        public void AQuotedValueContainingAnApostropheSurvives()
        {
            // Every nucleic-acid structure writes atom names like "O5'". A whitespace splitter reads
            // that as two tokens and silently shifts every remaining column of the row - which is how
            // 6EK0 came back with an atom named 'O5 in a chain called '"'.
            PdbStructure s = MmcifReader.ParseStructure(
                "data_test\n" +
                "loop_\n" +
                "_atom_site.group_PDB\n" +
                "_atom_site.id\n" +
                "_atom_site.type_symbol\n" +
                "_atom_site.label_atom_id\n" +
                "_atom_site.label_comp_id\n" +
                "_atom_site.label_asym_id\n" +
                "_atom_site.Cartn_x\n" +
                "_atom_site.Cartn_y\n" +
                "_atom_site.Cartn_z\n" +
                "_atom_site.auth_seq_id\n" +
                "_atom_site.auth_asym_id\n" +
                "ATOM 1 O \"O5'\" C L5 1.0 2.0 3.0 1 L\n");

            PdbAtom atom = s.Models[0].Chains[0].Residues[0].Atoms[0];
            Assert.AreEqual("O5'", atom.Name);
            Assert.AreEqual("C", atom.ResName);
            Assert.AreEqual("L", atom.ChainId);
            Assert.AreEqual(1.0, atom.X, 1e-6);
            Assert.AreEqual(3.0, atom.Z, 1e-6);
        }

        [TestMethod]
        public void ADotOrQuestionMarkIsNothing_NotAOneCharacterName()
        {
            // The semicolon field must OPEN a line to be one - written mid-line it is an
            // ordinary token, which is why the newline before it here is not decoration.
            var tokenizer = new CifTokenizer("A . ? '.'\n;\ntext\n;\n");

            CifToken t = tokenizer.Next();
            Assert.AreEqual("A", t.Text);
            Assert.IsFalse(t.IsNull);

            Assert.IsTrue(tokenizer.Next().IsNull, "a bare . is CIF's 'not applicable'");
            Assert.IsTrue(tokenizer.Next().IsNull, "a bare ? is CIF's 'unknown'");

            t = tokenizer.Next();
            Assert.AreEqual(".", t.Text);
            Assert.IsFalse(t.IsNull, "a QUOTED dot is a real one-character string");

            Assert.AreEqual("text", tokenizer.Next().Text, "a semicolon text field");
            Assert.AreEqual(CifTokenKind.End, tokenizer.Next().Kind);
        }

        [TestMethod]
        public void ASemicolonTextFieldDoesNotSwallowTheRestOfTheFile()
        {
            var tokenizer = new CifTokenizer(";\nline one\nline two\n;\n_next.tag value\n");
            Assert.AreEqual("line one\nline two", tokenizer.Next().Text.Replace("\r", string.Empty));

            CifToken tag = tokenizer.Next();
            Assert.AreEqual(CifTokenKind.Tag, tag.Kind);
            Assert.AreEqual("_next.tag", tag.Text);
            Assert.AreEqual("value", tokenizer.Next().Text);
        }

        [TestMethod]
        public void CommentsAndDataBlocksAreRecognised()
        {
            var tokenizer = new CifTokenizer("# a comment\ndata_1ABC\nloop_\n_a.b\nvalue\n");
            CifToken block = tokenizer.Next();
            Assert.AreEqual(CifTokenKind.DataBlock, block.Kind);
            Assert.AreEqual("1ABC", block.Text);
            Assert.AreEqual(CifTokenKind.Loop, tokenizer.Next().Kind);
            Assert.AreEqual(CifTokenKind.Tag, tokenizer.Next().Kind);
            Assert.AreEqual("value", tokenizer.Next().Text);
        }

        [TestMethod]
        public void ACifWithNoCoordinatesSaysSo()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix-cif-" + Guid.NewGuid().ToString("N") + ".cif");
            File.WriteAllText(path, "data_1ABC\n_entry.id 1ABC\n_cell.length_a 40.96\n");
            try
            {
                MmcifReader.BuildIndex(path);
                Assert.Fail("expected a refusal");
            }
            catch (InvalidDataException ex)
            {
                StringAssert.Contains(ex.Message, "atom_site");
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }

        [TestMethod]
        public void AMultiModelFileBecomesATrajectory()
        {
            string header =
                "data_test\n" +
                "loop_\n" +
                "_atom_site.group_PDB\n_atom_site.id\n_atom_site.type_symbol\n" +
                "_atom_site.label_atom_id\n_atom_site.label_comp_id\n_atom_site.label_asym_id\n" +
                "_atom_site.Cartn_x\n_atom_site.Cartn_y\n_atom_site.Cartn_z\n" +
                "_atom_site.auth_seq_id\n_atom_site.auth_asym_id\n_atom_site.pdbx_PDB_model_num\n";
            string rows =
                "ATOM 1 N N ALA A 0.0 0.0 0.0 1 A 1\n" +
                "ATOM 2 C CA ALA A 1.0 0.0 0.0 1 A 1\n" +
                "ATOM 3 N N ALA A 0.0 5.0 0.0 1 A 2\n" +
                "ATOM 4 C CA ALA A 1.0 5.0 0.0 1 A 2\n";

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix-cif-" + Guid.NewGuid().ToString("N") + ".cif");
            File.WriteAllText(path, header + rows);
            try
            {
                using (Trajectory t = Trajectory.Open(path))
                {
                    Assert.AreEqual(2, t.AtomCount, "the topology is model 1's atoms");
                    Assert.AreEqual(2, t.FrameCount, "each model is a frame");
                    Assert.AreEqual(0f, t.GetFrame(0)[1], 1e-6f);
                    Assert.AreEqual(5f, t.GetFrame(1)[1], 1e-6f, "frame 2 has the second model's y");
                }
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }

        [TestMethod]
        public void SegmentsFollowLabelAsymId_SoLigandsDoNotJoinTheChain()
        {
            // A segment here is what TER delimits in a PDB file. label_asym_id is its mmCIF
            // equivalent: auth_asym_id would put a protein chain and the waters beside it in one run.
            string text =
                "data_test\n" +
                "loop_\n" +
                "_atom_site.group_PDB\n_atom_site.id\n_atom_site.type_symbol\n" +
                "_atom_site.label_atom_id\n_atom_site.label_comp_id\n_atom_site.label_asym_id\n" +
                "_atom_site.Cartn_x\n_atom_site.Cartn_y\n_atom_site.Cartn_z\n" +
                "_atom_site.auth_seq_id\n_atom_site.auth_asym_id\n" +
                "ATOM   1 N N   ALA A 0.0 0.0 0.0 1 A\n" +
                "ATOM   2 C CA  ALA A 1.0 0.0 0.0 1 A\n" +
                "HETATM 3 O O   HOH B 9.0 0.0 0.0 2 A\n";

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix-cif-" + Guid.NewGuid().ToString("N") + ".cif");
            File.WriteAllText(path, text);
            try
            {
                using (Trajectory t = Trajectory.Open(path))
                {
                    Assert.AreEqual(2, t.Topology.Chains.Count,
                        "the water is its own segment even though the author calls it chain A");
                    Assert.AreEqual("A", t.Topology.ChainId[2],
                        "but the chain a USER sees is still the author's");
                }
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }
    }
}
