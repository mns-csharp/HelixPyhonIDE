using System;
using System.IO;
using HelixPythonNamespace.Core.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Core.Tests
{
    /// <summary>
    /// <see cref="PathComparison"/> — the one place that decides how filesystem paths are compared.
    ///
    /// The distinction under test is between PATH IDENTITY (which follows the filesystem's own case
    /// rules) and NAMING CONVENTIONS (which stay case-insensitive everywhere, and live in the
    /// callers). Conflating them is what let the policy engine attribute a file to a project that
    /// does not contain it on a case-sensitive volume.
    ///
    /// Note the shape of the case-sensitivity assertions: they are written against
    /// <see cref="PathComparison.IsCaseSensitive"/> rather than hard-coding the Windows answer, so
    /// they state the CONTRACT on both platforms instead of pinning today's host. On this
    /// net472/WinForms product they exercise the case-insensitive branch.
    /// </summary>
    [TestClass]
    public class PathComparisonTests
    {
        private static string P(params string[] parts)
        {
            return Path.Combine(parts);
        }

        [TestMethod]
        public void CaseSensitivity_MatchesThePlatform()
        {
            // Windows filesystems are case-insensitive; POSIX ones are not. The separator is the
            // signal the detector uses, so the two must agree or the detection is broken.
            bool onWindows = Path.DirectorySeparatorChar == '\\';
            Assert.AreEqual(onWindows, !PathComparison.IsCaseSensitive,
                "Windows must compare paths case-insensitively, POSIX case-sensitively");
        }

        [TestMethod]
        public void AreSame_IgnoresTrailingSeparators()
        {
            string dir = P("root", "projects", "app");
            Assert.IsTrue(PathComparison.AreSame(dir, dir + Path.DirectorySeparatorChar));
            Assert.IsTrue(PathComparison.AreSame(dir + "/", dir + "\\"));
            Assert.IsFalse(PathComparison.AreSame(dir, P("root", "projects", "app2")));
        }

        [TestMethod]
        public void AreSame_FollowsThePlatformsCaseRules()
        {
            string lower = P("root", "projects", "calc");
            string upper = P("root", "projects", "Calc");
            Assert.AreEqual(!PathComparison.IsCaseSensitive, PathComparison.AreSame(lower, upper),
                "on a case-sensitive volume these are two different directories");
        }

        [TestMethod]
        public void AreSame_HandlesNulls()
        {
            Assert.IsTrue(PathComparison.AreSame(null, null));
            Assert.IsFalse(PathComparison.AreSame(null, "x"));
            Assert.IsFalse(PathComparison.AreSame("x", null));
        }

        [TestMethod]
        public void IsUnderDirectory_RequiresAWholeComponentBoundary()
        {
            // The guard every prefix test in the policy engine needs: a sibling whose name merely
            // begins with the directory's name is NOT inside it. Without the appended separator,
            // calc_engine's files would be attributed to project calc.
            string calc = P("root", "src", "calc");
            Assert.IsTrue(PathComparison.IsUnderDirectory(P("root", "src", "calc", "x.py"), calc));
            Assert.IsFalse(PathComparison.IsUnderDirectory(P("root", "src", "calc_engine", "x.py"), calc));
        }

        [TestMethod]
        public void IsUnderDirectory_IsStrict_AndToleratesTrailingSeparators()
        {
            string dir = P("root", "src", "calc");
            Assert.IsFalse(PathComparison.IsUnderDirectory(dir, dir),
                "a directory is not strictly inside itself");
            Assert.IsTrue(PathComparison.IsUnderDirectory(
                P("root", "src", "calc", "x.py"), dir + Path.DirectorySeparatorChar));
            Assert.IsFalse(PathComparison.IsUnderDirectory(null, dir));
            Assert.IsFalse(PathComparison.IsUnderDirectory(P("a", "b"), null));
        }

        [TestMethod]
        public void IsUnderDirectory_FollowsThePlatformsCaseRules()
        {
            Assert.AreEqual(!PathComparison.IsCaseSensitive,
                PathComparison.IsUnderDirectory(P("root", "src", "calc", "x.py"), P("root", "src", "Calc")),
                "case-sensitively, calc/x.py is not inside Calc — the bug that let rule 1 miss "
                + "a non-member file and rule 3 blame the wrong project");
        }

        [TestMethod]
        public void SplitSegments_HonoursBothSeparators_OnEveryPlatform()
        {
            // The real defect: on POSIX both Path.*SeparatorChar constants are '/', so splitting on
            // those two leaves a Windows-authored path as one unsplittable segment — and every
            // per-component decision (vendored code, test directories) silently answers "no".
            CollectionAssert.AreEqual(new[] { "a", "b", "c.py" },
                PathComparison.SplitSegments(@"a\b\c.py"));
            CollectionAssert.AreEqual(new[] { "a", "b", "c.py" },
                PathComparison.SplitSegments("a/b/c.py"));
            CollectionAssert.AreEqual(new[] { "a", "b", "c.py" },
                PathComparison.SplitSegments(@"a/b\c.py"), "mixed separators too");
        }

        [TestMethod]
        public void SplitSegments_DropsEmptyComponents()
        {
            CollectionAssert.AreEqual(new[] { "a", "b" }, PathComparison.SplitSegments(@"\\a\\b\\"));
            Assert.AreEqual(0, PathComparison.SplitSegments("").Length);
            Assert.AreEqual(0, PathComparison.SplitSegments(null).Length);
        }

        [TestMethod]
        public void TrimTrailing_RemovesEitherSeparator()
        {
            Assert.AreEqual(@"a\b", PathComparison.TrimTrailing(@"a\b\"));
            Assert.AreEqual("a/b", PathComparison.TrimTrailing("a/b/"));
            Assert.AreEqual("a", PathComparison.TrimTrailing("a"));
            Assert.IsNull(PathComparison.TrimTrailing(null));
        }
    }
}
