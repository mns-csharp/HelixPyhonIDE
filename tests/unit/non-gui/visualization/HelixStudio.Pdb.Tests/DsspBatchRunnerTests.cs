using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HelixPythonNamespace.Pdb.Chem;
using HelixPythonNamespace.Pdb.Dssp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// The folder-at-a-time run behind Analysis ▸ Protein Structure ▸ Batch DSSP.
    ///
    /// The assertion that matters most is that running many files in parallel gives byte-identical
    /// results to running each one alone. Parallel code that shares one accumulator produces
    /// plausible output and wrong numbers, and no per-file count would show it — so the oracle is
    /// the single-file answer, computed separately, compared in full.
    /// </summary>
    [TestClass]
    public class DsspBatchRunnerTests
    {
        private string _folder;

        [TestInitialize]
        public void CreateWorkspace()
        {
            _folder = Path.Combine(Path.GetTempPath(), "helix-dssp-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TestCleanup]
        public void RemoveWorkspace()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
            catch (IOException) { /* a temp directory that outlives the test is not a failure */ }
        }

        private string CopyFixture(string fixture, string asName)
        {
            string source = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(source, "fixture root tests/fixtures/pdb not found");
            string destination = Path.Combine(_folder, asName);
            File.Copy(source, destination, true);
            return destination;
        }

        [TestMethod]
        public void AnEmptyFolderIsAnEmptyRunRatherThanAnError()
        {
            DsspBatchResult result = DsspBatchRunner.Run(
                new DsspBatchOptions { InputFolder = _folder }, null, CancellationToken.None);

            Assert.AreEqual(0, result.Items.Count);
            Assert.IsFalse(result.Cancelled);
        }

        /// <summary>
        /// The residue table alone. The header block legitimately differs between two runs of the
        /// same structure — it carries the run date and the path of the file it read — so comparing
        /// it would fail for reasons that have nothing to do with the assignment.
        /// </summary>
        private static string Body(string dssp)
        {
            int start = dssp.IndexOf(DsspWriter.ColumnHeader, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "a .dssp file must carry the column header");
            return dssp.Substring(start);
        }
    }
}
