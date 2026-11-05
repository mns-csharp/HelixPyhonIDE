using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 275: the Manage-Packages search used `dotnet package search` (NuGet) even for Python
    /// projects, returning the wrong ecosystem. For a pyproject it now resolves the query against the
    /// PyPI JSON API (exact-name lookup; PyPI has no public fuzzy search). These guard that resolution.
    /// </summary>
    [TestClass]
    public class ManagePackagesPyPITests
    {
        private static void SearchPyPI(string query, List<string[]> results)
        {
            MethodInfo m = typeof(ManagePackagesDialog).GetMethod(
                "SearchPyPI", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "SearchPyPI(string, List<string[]>) expected");
            m.Invoke(null, new object[] { query, results });
        }

        [TestMethod]
        public void NonexistentPackage_YieldsNoResult()
        {
            var results = new List<string[]>();
            // A 404 (online) and a WebException (offline) both yield an empty list — deterministic.
            SearchPyPI("qzxjw-not-a-real-pkg-xyz123", results);
            Assert.AreEqual(0, results.Count);
        }

        [TestMethod]
        public void KnownPackage_ResolvesWithVersion_WhenOnline()
        {
            var results = new List<string[]>();
            SearchPyPI("requests", results);
            if (results.Count == 0)
            {
                Assert.Inconclusive("PyPI unreachable (offline) — skipping the online assertion.");
                return;
            }
            Assert.AreEqual(3, results[0].Length, "row is [name, version, summary]");
            StringAssert.Contains(results[0][0].ToLowerInvariant(), "requests");
            Assert.IsFalse(string.IsNullOrEmpty(results[0][1]), "a version is expected for a real package");
        }
    }
}
