namespace HelixPythonNamespace.Editor.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Editor.Navigation;

    [TestClass]
    public class FindInPathServiceTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_fip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "a.py"), "needle here\nand needle again\n");
            Directory.CreateDirectory(Path.Combine(_root, "sub"));
            File.WriteAllText(Path.Combine(_root, "sub", "b.py"), "one needle\n");
            Directory.CreateDirectory(Path.Combine(_root, "__pycache__"));
            File.WriteAllText(Path.Combine(_root, "__pycache__", "c.py"), "needle in cache\n");
            File.WriteAllText(Path.Combine(_root, "d.txt"), "needle in text\n");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [TestMethod]
        public void SearchFindsLiteralMatchesSkippingCacheAndOtherExtensions()
        {
            FindInPathService svc = new FindInPathService();
            List<FindInPathHit> hits = svc.Search(new FindInPathOptions { Query = "needle", RootDir = _root });
            // a.py (2) + sub/b.py (1) = 3; __pycache__ skipped, d.txt excluded by extension.
            Assert.AreEqual(3, hits.Count);
        }

        [TestMethod]
        public void WholeWordExcludesSubstringMatches()
        {
            File.WriteAllText(Path.Combine(_root, "a.py"), "foo and foobar\n");
            FindInPathService svc = new FindInPathService();
            List<FindInPathHit> ww = svc.Search(new FindInPathOptions { Query = "foo", RootDir = _root, WholeWord = true });
            int aHits = 0;
            foreach (FindInPathHit h in ww) if (h.FilePath.EndsWith("a.py")) aHits++;
            Assert.AreEqual(1, aHits);
        }

        [TestMethod]
        public void CaseSensitivityRespected()
        {
            FindInPathService svc = new FindInPathService();
            Assert.AreEqual(0, svc.Search(new FindInPathOptions { Query = "NEEDLE", RootDir = _root, CaseSensitive = true }).Count);
            Assert.IsTrue(svc.Search(new FindInPathOptions { Query = "NEEDLE", RootDir = _root, CaseSensitive = false }).Count >= 3);
        }

        [TestMethod]
        public void RegexSearchWorks()
        {
            FindInPathService svc = new FindInPathService();
            List<FindInPathHit> hits = svc.Search(new FindInPathOptions { Query = "n..dle", RootDir = _root, UseRegex = true });
            Assert.IsTrue(hits.Count >= 3);
        }

        [TestMethod]
        public void MaxHitsBoundsResults()
        {
            FindInPathService svc = new FindInPathService();
            List<FindInPathHit> hits = svc.Search(new FindInPathOptions { Query = "needle", RootDir = _root, MaxHits = 2 });
            Assert.AreEqual(2, hits.Count);
        }

        [TestMethod]
        public void IncludeExtensionsCanWidenToTxt()
        {
            FindInPathService svc = new FindInPathService();
            List<FindInPathHit> hits = svc.Search(new FindInPathOptions
            {
                Query = "needle",
                RootDir = _root,
                IncludeExtensions = new List<string> { ".txt" },
            });
            Assert.AreEqual(1, hits.Count);
            Assert.IsTrue(hits[0].FilePath.EndsWith("d.txt"));
        }

        [TestMethod]
        public void ReplaceAllRewritesFilesAndCounts()
        {
            FindInPathService svc = new FindInPathService();
            int n = svc.ReplaceAll(new FindInPathOptions { Query = "needle", RootDir = _root }, "pin");
            Assert.AreEqual(3, n);
            string a = File.ReadAllText(Path.Combine(_root, "a.py"));
            Assert.IsFalse(a.Contains("needle"));
            Assert.IsTrue(a.Contains("pin"));
        }
    }
}
