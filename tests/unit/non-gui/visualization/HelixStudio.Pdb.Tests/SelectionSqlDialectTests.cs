using System;
using System.Linq;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using HelixPythonNamespace.Pdb.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// The Filter box speaks ANSI SQL, adapted to atoms — and these are the places where it either
    /// did not, or claimed to and did the opposite.
    ///
    /// <para>
    /// The oracle is ubiquitin on disk, never an inline snippet: a real PDB carries the blank
    /// columns, mixed-case element symbols and TER records that make <c>IS NULL</c> and the text
    /// comparisons mean something. Each test states the SQL a person would actually type.
    /// </para>
    /// </summary>
    [TestClass]
    public class SelectionSqlDialectTests
    {
        private Trajectory _t;
        private EvalContext _ctx;

        [TestInitialize]
        public void SetUp()
        {
            string path = PdbFixtures.Path_(PdbFixtures.Ubiquitin);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            _t = Trajectory.Open(path);
            _ctx = new EvalContext(_t.Topology, _t.GetFrame(0), _t.Index.FirstFrameXyz);
        }

        [TestCleanup]
        public void TearDown()
        {
            if (_t != null) _t.Dispose();
        }

        /// <summary>How many atoms a filter selects.</summary>
        private int Count(string filter)
        {
            ISelection sel = Selection.Compile(filter);
            Assert.IsNotNull(sel, "the filter compiled to nothing");
            return sel.Evaluate(_ctx).Count(b => b);
        }

        private static string ErrorOf(string filter)
        {
            string error;
            Assert.IsFalse(Selection.Validate(filter, out error), "'" + filter + "' should be refused");
            return error ?? string.Empty;
        }

        // ---- 1. A leading WHERE ----------------------------------------------------------------







        // ---- 2. IS NULL / IS NOT NULL ----------------------------------------------------------











        // ---- 3. `==` is Python, not SQL --------------------------------------------------------





        // ---- 4. Delimited identifiers ----------------------------------------------------------









        // ---- the dialect that was already right, pinned so the above cannot regress it ----------


            }
}
