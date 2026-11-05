namespace HelixPythonNamespace.Editor.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Editor.Bookmarks;
    using HelixPythonNamespace.Editor.Doc;

    [TestClass]
    public class BookmarksManagerTests
    {
        [TestMethod]
        public void ToggleAddsThenRemoves()
        {
            BookmarksManager b = new BookmarksManager();
            Assert.IsTrue(b.Toggle("a.py", 5));
            Assert.IsTrue(b.IsBookmarked("a.py", 5));
            Assert.IsFalse(b.Toggle("a.py", 5));
            Assert.IsFalse(b.IsBookmarked("a.py", 5));
        }

        [TestMethod]
        public void NextAndPreviousWrap()
        {
            BookmarksManager b = new BookmarksManager();
            b.Toggle("a.py", 3);
            b.Toggle("a.py", 10);
            b.Toggle("a.py", 20);
            Assert.AreEqual(10, b.Next("a.py", 3));
            Assert.AreEqual(3, b.Next("a.py", 20));   // wrap
            Assert.AreEqual(10, b.Previous("a.py", 20));
            Assert.AreEqual(20, b.Previous("a.py", 3)); // wrap
            Assert.AreEqual(-1, b.Next("other.py", 1));
        }

        [TestMethod]
        public void AllReturnsSortedEntries()
        {
            BookmarksManager b = new BookmarksManager();
            b.Toggle("b.py", 2);
            b.Toggle("a.py", 9);
            b.Toggle("a.py", 1);
            List<BookmarkEntry> all = b.All();
            Assert.AreEqual(3, all.Count);
            Assert.AreEqual("a.py", all[0].FilePath);
            Assert.AreEqual(1, all[0].Line);
            Assert.AreEqual("a.py", all[1].FilePath);
            Assert.AreEqual(9, all[1].Line);
            Assert.AreEqual("b.py", all[2].FilePath);
        }

        [TestMethod]
        public void SetLinesReplacesAndSorts()
        {
            BookmarksManager b = new BookmarksManager();
            b.SetLines("a.py", new[] { 30, 10, 20 });
            IReadOnlyList<int> lines = b.LinesFor("a.py");
            CollectionAssert.AreEqual(new List<int> { 10, 20, 30 }, new List<int>(lines));
        }

        [TestMethod]
        public void ChangedEventFires()
        {
            BookmarksManager b = new BookmarksManager();
            int count = 0;
            b.BookmarksChanged += () => count++;
            b.Toggle("a.py", 1);
            Assert.AreEqual(1, count);
        }
    }

    [TestClass]
    public class DocUrlResolverTests
    {
        [TestMethod]
        public void RecognisesStdlibModules()
        {
            Assert.IsTrue(DocUrlResolver.IsStdlibModule("os"));
            Assert.IsTrue(DocUrlResolver.IsStdlibModule("os.path"));
            Assert.IsTrue(DocUrlResolver.IsStdlibModule("urllib.parse"));
            Assert.IsFalse(DocUrlResolver.IsStdlibModule("mypackage"));
            Assert.IsFalse(DocUrlResolver.IsStdlibModule(""));
        }

        [TestMethod]
        public void ResolvesUrlWithAndWithoutSymbol()
        {
            Assert.AreEqual("https://docs.python.org/3/library/json.html#json.dumps",
                DocUrlResolver.ResolvePythonDocsUrl("json", "dumps"));
            Assert.AreEqual("https://docs.python.org/3/library/json.html",
                DocUrlResolver.ResolvePythonDocsUrl("json", ""));
            Assert.AreEqual("", DocUrlResolver.ResolvePythonDocsUrl("mypackage", "x"));
        }
    }

    [TestClass]
    public class DocFetcherTests
    {
        [TestMethod]
        public void CachePathStripsFragmentAndIsDeterministic()
        {
            DocFetcher f = new DocFetcher();
            string a = f.CachePathFor("https://x/page.html#anchor");
            string b = f.CachePathFor("https://x/page.html");
            Assert.AreEqual(b, a, "Fragment must not affect the cache key.");
            Assert.IsTrue(a.EndsWith(".html"));
        }

        [TestMethod]
        public void ServesFromCacheRaisesLoaded()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_doc_cache_" + Guid.NewGuid().ToString("N"));
            try
            {
                DocFetcher f = new DocFetcher();
                f.SetCacheDirForTesting(dir);
                f.WriteCache("https://x/p.html", "<html>cached</html>");

                string gotUrl = null, gotHtml = null;
                using (ManualResetEventSlim done = new ManualResetEventSlim(false))
                {
                    f.Loaded += (u, h) => { gotUrl = u; gotHtml = h; done.Set(); };
                    f.Fetch("https://x/p.html");
                    Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(5)));
                }
                Assert.AreEqual("https://x/p.html", gotUrl);
                Assert.AreEqual("<html>cached</html>", gotHtml);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void InvalidUrlRaisesFailed()
        {
            DocFetcher f = new DocFetcher();
            string msg = null;
            f.Failed += (u, m) => msg = m;
            f.Fetch("not a url");
            Assert.IsNotNull(msg);
            Assert.IsTrue(msg.Contains("invalid"));
        }
    }
}
