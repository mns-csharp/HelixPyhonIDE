using System;
using HelixPythonNamespace.Pdb.Io;
using HelixPythonNamespace.Pdb.Model;
using HelixPythonNamespace.Pdb.Query;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Pdb.Tests
{
    /// <summary>
    /// The file layer, exercised against real files. These pin the port of PdbViewer.Core: indexing,
    /// topology, streaming frame reads and the atoms query engine that backs the filter box.
    /// </summary>
    [TestClass]
    public class PdbEngineTests
    {
        private static Trajectory Open(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            return Trajectory.Open(path);
        }










            }

    /// <summary>The filter. Everything the PDB tab's filter box does goes through here.</summary>
    [TestClass]
    public class AtomFilterTests
    {
        private static Trajectory Open(string fixture)
        {
            string path = PdbFixtures.Path_(fixture);
            Assert.IsNotNull(path, "fixture root tests/fixtures/pdb not found");
            return Trajectory.Open(path);
        }

        private static int CountMatching(Trajectory t, string filter)
        {
            ISelection sel = Selection.Compile(filter);
            Assert.IsNotNull(sel, "'" + filter + "' should compile to a selection");
            bool[] mask = sel.Evaluate(new EvalContext(t.Topology, t.GetFrame(0), t.Index.FirstFrameXyz));
            int n = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) n++;
            return n;
        }

        [TestMethod]
        public void AGeometricPredicateIsMarkedDynamic_SoItIsReEvaluatedPerFrame()
        {
            // A selection over x/y/z that was cached as static would freeze on frame 0 and show a
            // filter that is silently one frame's answer for the whole trajectory.
            Assert.IsTrue(Selection.Compile("z > 0").IsDynamic, "a coordinate predicate is per-frame");
            Assert.IsFalse(Selection.Compile("chain='A'").IsDynamic, "a chain predicate is static");
        }



        private static int Count(ISelection sel, Trajectory t, int frame)
        {
            bool[] mask = sel.Evaluate(new EvalContext(t.Topology, t.GetFrame(frame), t.Index.FirstFrameXyz));
            int n = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) n++;
            return n;
        }

        [TestMethod]
        public void AnEmptyFilterMeansEverything_NotAnError()
        {
            string error;
            Assert.IsTrue(Selection.Validate("", out error));
            Assert.IsNull(error);
            Assert.IsNull(Selection.Compile("   "), "empty text compiles to no selection at all");
        }

        [TestMethod]
        public void AnInvalidFilterIsReported_NotSilentlyIgnored()
        {
            // The renderer's own fallback for an uncompilable selection is "all atoms", so if the
            // filter box ever stopped validating, a typo would look like a filter that matched
            // everything. This is the boundary that has to refuse.
            string error;
            Assert.IsFalse(Selection.Validate("chain =", out error));
            Assert.IsFalse(string.IsNullOrEmpty(error), "the failure must carry a message to show");
        }

        [TestMethod]
        public void APythonStyleSelectionIsRejectedRatherThanMisread()
        {
            // PdbViewer accepted `a.chain=='A'` through its embedded Python. Here that must fail
            // loudly: silently matching nothing (or everything) would be worse than an error.
            string error;
            Assert.IsFalse(Selection.Validate("a.chain=='A'", out error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }


            }
}
