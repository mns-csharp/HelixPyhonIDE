using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Workspace.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The disk scan's survival properties. Everything else in the parity engine assumes the scan
    /// returned, and returned the truth; these are the two ways that assumption used to fail.
    /// </summary>
    [TestClass]
    public class StructureScanGuardTests
    {
        /// <summary>
        /// A junction pointing back at its own parent is an infinite path.
        ///
        /// The walk used to descend into it, and a <c>StackOverflowException</c> cannot be caught
        /// on .NET Framework — the process is torn down with no exception, no handler and no
        /// dialog. Because this scan runs on the solution-load path, the user's experience of the
        /// bug is that the IDE disappears when they open their project. This repository is itself
        /// reached through a junction, so the shape is not hypothetical here.
        ///
        /// The link's own entry is still recorded: it is a real thing at a real path that the tree
        /// should show. It is only not walked THROUGH.
        /// </summary>
        [TestMethod]
        public void AJunctionPointingAtItsOwnAncestor_IsRecordedButNotFollowed()
        {
            string root = StructureParityTests.NewTempSolutionDirectory();
            Directory.CreateDirectory(Path.Combine(root, "pkg"));
            File.WriteAllText(Path.Combine(root, "pkg", "shapes.py"), "class Square: pass\n");

            if (!TryCreateJunction(Path.Combine(root, "pkg", "loop"), root))
                Assert.Inconclusive("could not create a junction on this volume");

            var scanner = new DiskLayoutScanner();
            StructureLayout layout = scanner.Scan(root);   // must return at all

            CollectionAssert.Contains(layout.Directories.ToList(), "pkg/loop",
                string.Join(", ", layout.Directories));
            Assert.IsFalse(layout.Directories.Any(d => d.StartsWith("pkg/loop/", StringComparison.Ordinal)),
                "the walk descended through the junction: " + string.Join(", ", layout.Directories));
        }

        /// <summary>
        /// A walk that stopped early found nothing because it did not look, and an empty report
        /// from it is not a clean one.
        ///
        /// This is the failure mode where a scan manufactures success out of its own error: every
        /// path it never reached looks exactly like a path the tree correctly omits, so a truncated
        /// walk produces zero defects and the gate waves the solution through. Callers ask
        /// <see cref="StructureParityReport.IsVerifiedClean"/> rather than
        /// <see cref="StructureParityReport.IsEmpty"/> for precisely this reason.
        /// </summary>
        [TestMethod]
        public void ATruncatedScan_IsNotVerifiedClean()
        {
            string root = StructureParityTests.NewTempSolutionDirectory();
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"s\"\n");

            // Past the depth backstop — the belt to the reparse-point guard's braces, for a link
            // the guard cannot identify or a genuinely pathological tree.
            string deep = root;
            for (int i = 0; i < 70; i++)
            {
                deep = Path.Combine(deep, "d");
                try { Directory.CreateDirectory(deep); }
                catch (PathTooLongException) { Assert.Inconclusive("long paths unavailable here"); }
                catch (IOException) { Assert.Inconclusive("long paths unavailable here"); }
            }

            var scanner = new DiskLayoutScanner();
            scanner.Scan(root);
            Assert.IsTrue(scanner.WasTruncated, "a 70-deep tree must report the walk as incomplete");

            StructureParityReport report = new DirectoryStructureCorrectionEngine()
                .Verify(StructureParityTests.Load(root));

            Assert.IsFalse(report.ScanCompleted, report.Describe());
            Assert.IsFalse(report.IsVerifiedClean,
                "an incomplete scan must never read as clean\n" + report.Describe());
            // And it says so in the text a maintainer reads out of a failure, so a defect list from
            // a truncated walk is never mistaken for the whole story.
            StringAssert.Contains(report.Describe(), "could not complete");
        }

        private static bool TryCreateJunction(string link, string target)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + link + "\" \"" + target + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    return p.HasExited && p.ExitCode == 0 && Directory.Exists(link);
                }
            }
            catch { return false; }
        }
    }
}
