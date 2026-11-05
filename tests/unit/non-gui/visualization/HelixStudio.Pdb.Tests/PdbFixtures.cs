using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// Locates <c>tests/fixtures/pdb</c> from the test binary's directory.
    ///
    /// These are real Protein Data Bank files, not snippets built in a string: a coarse-grained
    /// bead trajectory with CRLF line endings, HETATM records and 80 TER-delimited chains
    /// (fixture_1/2), a crystallographic protein with element columns and no MODEL block at all
    /// (5i55), and three small files that exist to be malformed. Every one of them carries
    /// something a hand-written snippet leaves out, which is exactly why the acceptance oracle is
    /// a file on disk.
    /// </summary>
    internal static class PdbFixtures
    {
        public static string Root()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "tests", "fixtures", "pdb");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            Assert.Inconclusive("Optional structure test data is unavailable.");
            return null;
        }

        public static string Path_(string name)
        {
            string root = Root();
            string path = System.IO.Path.Combine(Root(), name);
            if (!File.Exists(path)) Assert.Inconclusive("Optional structure test data is unavailable.");
            return path;
        }

        /// <summary>An 80-chain coarse-grained bead trajectory, two frames, CRLF, HETATM records.</summary>
        public const string BeadTrajectory = "fixture_2.pdb";

        /// <summary>The same system as a single frame.</summary>
        public const string BeadSingleFrame = "fixture_1.pdb";

        /// <summary>A real crystallographic protein: 218 atoms, element column populated, no MODEL.</summary>
        public const string Protein = "5i55.pdb";

        /// <summary>
        /// Ubiquitin at 1.8 A — the reference case for secondary-structure work, and the reason it
        /// is here rather than a smaller file. Its 76 residues carry a twelve-residue alpha helix,
        /// two 3-10 helices, a five-stranded mixed beta sheet with both parallel and antiparallel
        /// ladders, several turns and bends, and no disulphide. A DSSP that gets ubiquitin right has
        /// exercised every branch of the assignment; one that gets a single helical peptide right
        /// has exercised the helix branch.
        /// </summary>
        public const string Ubiquitin = "1ubq.pdb";

        /// <summary>
        /// Alpha-synuclein as AlphaFold predicts it. The b-factor column here is per-residue pLDDT,
        /// not displacement, and all four confidence bands are well represented — which is the
        /// point: a uniformly confident model would exercise one branch of the ramp and pass.
        /// </summary>
        public const string AlphaFoldModel = "AF-P37840-F1-model_v6.pdb";

        /// <summary>Crambin in both formats: one structure served two ways, so the two readers can
        /// be diffed against each other atom for atom.</summary>
        public const string CrambinPdb = "1crn.pdb";
        public const string CrambinCif = "1crn.cif";

        /// <summary>Five frames of ubiquitin in the two simulation formats, with the coordinates
        /// mdtraj put in - an independent oracle rather than a round-trip through our own writer.</summary>
        public const string UbiquitinDcd = "1ubq.dcd";
        public const string UbiquitinXtc = "1ubq.xtc";
        public const string TrajectoryReference = "1ubq.trajectory.reference.txt";

        public const string Broken = "broken.pdb";
        public const string Pbc = "pbc.pdb";
    }
}
