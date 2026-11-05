using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using HelixPythonNamespace.Pdb.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// One query answered once per frame — Rg(t), contact count(t), helix content(t).
    ///
    /// The test that matters most here is the secondary-structure one. The <c>ss</c> column reads a
    /// cached assignment the viewer computes once from frame 0, so a helix-against-time plot built
    /// naively is a FLAT LINE whatever the molecule does. That is the failure this feature is most
    /// likely to ship with and least likely to be noticed, so it is pinned from both sides: the
    /// frozen assignment is shown to be flat, and the per-frame one to move.
    /// </summary>
    [TestClass]
    public class TrajectoryTimeSeriesTests
    {
        private readonly List<string> _temporary = new List<string>();

        [TestCleanup]
        public void TearDown()
        {
            foreach (string path in _temporary)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            }
        }

        private static Trajectory Open(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            return Trajectory.Open(path);
        }

        // ---- the series ----------------------------------------------------------------------------









        // ---- the one that would have shipped wrong ---------------------------------------------------





        // ---- leaving the IDE -------------------------------------------------------------------------





        // ---- running it ------------------------------------------------------------------------------

        [TestMethod]
        public void AQueryKnowsWhichColumnsItReads()
        {
            Assert.IsTrue(AtomQuery.Compile("select count(*) from atoms where ss='H'").ReferencesColumn("ss"));
            Assert.IsTrue(AtomQuery.Compile("select count(*) from atoms where sstype='H'").ReferencesColumn("ss"),
                "an alias resolves to the same column");
            Assert.IsFalse(AtomQuery.Compile("select avg(rg) from atoms").ReferencesColumn("ss"));
            Assert.IsFalse(AtomQuery.Compile("select count(*) from atoms where resn='SS'").ReferencesColumn("ss"),
                "a string that happens to read 'SS' is not a column reference");
        }

        // ---- helper --------------------------------------------------------------------------------

        /// <summary>
        /// Real ubiquitin twice: model 1 as deposited, model 2 with its alpha helix (residues 23-34)
        /// pulled 8 A out of place.
        /// </summary>
        private string TwoModelUbiquitinWithABrokenHelix()
        {
            string source = PdbFixtures.Path_(PdbFixtures.Ubiquitin);
            Assert.IsNotNull(source, "fixture tests/fixtures/pdb/1ubq.pdb not found");

            var atoms = new List<string>();
            foreach (string line in File.ReadAllLines(source))
                if (line.StartsWith("ATOM  ", StringComparison.Ordinal)
                    || line.StartsWith("HETATM", StringComparison.Ordinal)) atoms.Add(line);
            Assert.IsTrue(atoms.Count > 0, "the fixture should have coordinates");

            var sb = new StringBuilder();
            sb.Append("HEADER    TWO MODELS OF UBIQUITIN, THE SECOND WITH ITS HELIX DISPLACED\n");
            AppendModel(sb, atoms, 1, 0, 0, 0.0);
            AppendModel(sb, atoms, 2, 23, 34, 8.0);
            sb.Append("END\n");

            string path = Path.Combine(Path.GetTempPath(),
                "helix-timeseries-" + Guid.NewGuid().ToString("N") + ".pdb");
            File.WriteAllText(path, sb.ToString());
            _temporary.Add(path);
            return path;
        }

        private static void AppendModel(StringBuilder sb, List<string> atoms, int serial,
                                        int fromResidue, int toResidue, double dz)
        {
            sb.Append("MODEL     ").Append(serial.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (string line in atoms)
            {
                string emitted = line;
                int residue;
                if (dz != 0.0
                    && int.TryParse(line.Substring(22, 4).Trim(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out residue)
                    && residue >= fromResidue && residue <= toResidue)
                {
                    double z = double.Parse(line.Substring(46, 8), NumberStyles.Float,
                                            CultureInfo.InvariantCulture) + dz;
                    emitted = line.Substring(0, 46) + z.ToString("F3", CultureInfo.InvariantCulture).PadLeft(8)
                            + line.Substring(54);
                }
                sb.Append(emitted).Append('\n');
            }
            sb.Append("ENDMDL\n");
        }
    }
}
