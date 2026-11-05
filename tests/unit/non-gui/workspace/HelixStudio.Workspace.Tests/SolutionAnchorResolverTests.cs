using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Windows associates files by EXTENSION, so once Tools ▸ Register TOML Files claims
    /// <c>.toml</c>, the IDE is handed whichever .toml the user double-clicked — usually NOT the
    /// workspace root. The promised outcome is "the whole solution opens", so every one of those
    /// paths has to widen to the same root manifest. These run against the real
    /// <c>python-test-sample-oop</c> fixture, which is a genuine uv workspace with members two
    /// directories down; a synthetic one-file temp dir would pass while the real nesting failed.
    /// </summary>
    [TestClass]
    public class SolutionAnchorResolverTests
    {
        private static string FixtureRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "tests", "fixtures", "python", "python-test-sample-oop");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static string RequireFixture()
        {
            string root = FixtureRoot();
            Assert.IsNotNull(root, "fixture tests/fixtures/python/python-test-sample-oop not found");
            return root;
        }

        private static void AssertSamePath(string expected, string actual, string because)
        {
            Assert.IsNotNull(actual, because + " (got null)");
            Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(actual), true, because);
        }

        [TestMethod]
        public void TheWorkspaceRootManifest_ResolvesToItself()
        {
            string root = RequireFixture();
            string rootToml = Path.Combine(root, "pyproject.toml");

            var result = SolutionAnchorResolver.Resolve(rootToml);

            AssertSamePath(rootToml, result.SolutionPath, "the root manifest is already the solution");
            Assert.IsNull(result.FileToOpen, "a solution root opens as a solution, not as a text file");
        }

        [TestMethod]
        public void AMemberPackagesManifest_ResolvesUpToTheSolutionRoot()
        {
            string root = RequireFixture();
            string memberToml = Path.Combine(root, "lib_core", "pyproject.toml");
            Assert.IsTrue(File.Exists(memberToml), "fixture member manifest missing: " + memberToml);

            var result = SolutionAnchorResolver.Resolve(memberToml);

            // The whole point of the feature: double-clicking a member's manifest must not open a
            // one-package solution. Before the walk existed, that is exactly what happened.
            AssertSamePath(Path.Combine(root, "pyproject.toml"), result.SolutionPath,
                           "a member manifest must widen to the workspace root");
        }

        [TestMethod]
        public void AnUnrelatedTomlInsideASolution_StillOpensThatSolution()
        {
            string root = RequireFixture();
            string stray = Path.Combine(root, "lib_core", "helix_anchor_probe.toml");
            File.WriteAllText(stray, "[tool.example]\nkey = 1\n");
            try
            {
                var result = SolutionAnchorResolver.Resolve(stray);

                AssertSamePath(Path.Combine(root, "pyproject.toml"), result.SolutionPath,
                               "any .toml inside a solution opens that solution");
                Assert.IsNull(result.FileToOpen, "the solution took precedence, so no bare file open");
            }
            finally { File.Delete(stray); }
        }

        [TestMethod]
        public void ATomlWithNoSolutionAboveIt_OpensInTheEditorInstead()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_anchor_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string orphan = Path.Combine(dir, "settings.toml");
                File.WriteAllText(orphan, "[section]\nvalue = true\n");

                var result = SolutionAnchorResolver.Resolve(orphan);

                Assert.IsNull(result.SolutionPath, "there is no solution to open: " + result.Reason);
                AssertSamePath(orphan, result.FileToOpen, "the file the user asked for still opens");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void TheWalkStopsAtTheRepositoryBoundary()
        {
            // A .git directory marks a different checkout. A manifest ABOVE it must never be
            // adopted, or one stray pyproject.toml high on a drive would silently become "the
            // solution" for every .toml beneath it.
            string outer = Path.Combine(Path.GetTempPath(), "helix_anchor_" + Guid.NewGuid().ToString("N"));
            string inner = Path.Combine(outer, "checkout", "pkg");
            Directory.CreateDirectory(inner);
            Directory.CreateDirectory(Path.Combine(outer, "checkout", ".git"));
            try
            {
                File.WriteAllText(Path.Combine(outer, "pyproject.toml"),
                                  "[tool.uv.workspace]\nmembers = [\"checkout\"]\n");
                string target = Path.Combine(inner, "config.toml");
                File.WriteAllText(target, "x = 1\n");

                var result = SolutionAnchorResolver.Resolve(target);

                Assert.IsNull(result.SolutionPath,
                              "a manifest above the repository boundary must not be adopted: " + result.Reason);
                AssertSamePath(target, result.FileToOpen, "with no solution in range the file itself opens");
            }
            finally { try { Directory.Delete(outer, true); } catch { } }
        }

        [TestMethod]
        public void AMissingPath_IsReportedRatherThanThrown()
        {
            var result = SolutionAnchorResolver.Resolve(
                Path.Combine(Path.GetTempPath(), "helix_no_such_" + Guid.NewGuid().ToString("N"), "pyproject.toml"));

            Assert.IsNull(result.SolutionPath);
            Assert.IsNull(result.FileToOpen);
            StringAssert.Contains(result.Reason, "not found");
        }

        [TestMethod]
        public void NullAndBlankPaths_AreAnsweredNotThrown()
        {
            // Program.Main hands this whatever the shell passed; a blank argv entry must not take
            // the IDE down before its window appears.
            Assert.IsNull(SolutionAnchorResolver.Resolve(null).SolutionPath);
            Assert.IsNull(SolutionAnchorResolver.Resolve("   ").SolutionPath);
            Assert.IsNotNull(SolutionAnchorResolver.Resolve(null).Reason);
        }
    }
}
