using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using HelixPythonNamespace.Pdb.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// <c>GROUP BY</c> — the clause that turns a selection language into a summary one, and the
    /// CSV that carries the answer out.
    ///
    /// The oracle throughout is ubiquitin, whose 660 atoms and published secondary structure are
    /// known quantities: a partition of it must add back up to 660, and grouping by <c>ss</c> must
    /// find exactly the helix, sheet and coil atoms the assignment does.
    /// </summary>
    [TestClass]
    public class GroupedQueryTests
    {
        private Trajectory _t;
        private EvalContext _ctx;

        [TestInitialize]
        public void SetUp()
        {
            string path = PdbFixtures.Path_(PdbFixtures.Ubiquitin);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            _t = Trajectory.Open(path);

            // The `ss` column reads this; without it every atom reports coil.
            _t.Topology.AtomSS = BackboneExtractor.ComputeAtomSS(_t.Topology, _t.GetFrame(0));
            _t.Topology.AtomSSFrame = 0;
            _ctx = new EvalContext(_t.Topology, _t.GetFrame(0), _t.Index.FirstFrameXyz);
        }

        [TestCleanup]
        public void TearDown()
        {
            if (_t != null) _t.Dispose();
        }

        private GroupedResult Group(string sql)
        {
            return AtomQuery.Compile(sql).EvaluateGrouped(_ctx);
        }

        // ---- what it computes ----------------------------------------------------------------------









        // ---- how it is ordered and cut ----------------------------------------------------------------









        // ---- what it refuses ---------------------------------------------------------------------------









        // ---- leaving the IDE -----------------------------------------------------------------------------






            }
}
