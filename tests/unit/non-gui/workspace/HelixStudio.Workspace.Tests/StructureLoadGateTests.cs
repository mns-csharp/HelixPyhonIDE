using System;
using System.IO;
using HelixPythonNamespace.Workspace.Services;
using HelixPythonNamespace.Workspace.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The load gate for PY8004: <see cref="SolutionService.LoadAsync"/> verifies structure parity
    /// and hands the answer back on the load result.
    ///
    /// <b>Why it lives on the loader and not on a menu handler.</b> Menu ▸ Open, Recent Files,
    /// drag-and-drop and command-line open all funnel through <c>LoadAsync</c>; a check bolted onto
    /// one of those four is bypassed by the other three. These tests pin the property that makes
    /// that true — the report arrives with the result, from the load itself, with no GUI involved.
    /// </summary>
    [TestClass]
    public class StructureLoadGateTests
    {
        private static SolutionLoadResult Open(string which)
        {
            string root = StructureParityTests.Fixture(which);
            return new SolutionService().LoadAsync(Path.Combine(root, "pyproject.toml"))
                                        .GetAwaiter().GetResult();
        }



        /// <summary>
        /// The load still SUCCEEDS on a mismatched solution. Parity is a gate on what the IDE will
        /// vouch for, not a refusal to read the file — the user has to be able to open the solution
        /// to see what is wrong with it.
        /// </summary>




        private static string StructurePathOf(string p)
        {
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// A failed load carries no report. Null means "not verified" everywhere in this feature,
        /// and the caller must treat it as such — never as "verified clean", which is the one
        /// misreading that would let the gate pass a solution it never looked at.
        /// </summary>
        [TestMethod]
        public void AFailedLoad_CarriesNoReport()
        {
            SolutionLoadResult result = new SolutionService()
                .LoadAsync(Path.Combine(Path.GetTempPath(), "helix-does-not-exist", "pyproject.toml"))
                .GetAwaiter().GetResult();

            Assert.IsFalse(result.Success);
            Assert.IsNull(result.StructureParity);
        }

        /// <summary>
        /// The repaired solution loads clean through the production path — the end-to-end shape the
        /// GUI gate relies on: open, repair, re-load, and the second load has nothing left to say
        /// about the structure.
        /// </summary>


        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (string.Equals(name, "__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
                CopyTree(directory, Path.Combine(target, name));
            }
        }
    }
}
