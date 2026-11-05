using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CompletionRankerTests
    {
        [TestMethod]
        public void ExactPrefix_BeatsCaseInsensitivePrefix()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem("Total_area", CompletionKind.Function),
                new CompletionItem("total_area", CompletionKind.Function),
            };
            var ranked = CompletionRanker.FilterAndRank(items, "total");
            Assert.AreEqual("total_area", ranked[0].Label);
            Assert.AreEqual("Total_area", ranked[1].Label);
        }

        [TestMethod]
        public void CamelCaseAbbrev_Matches()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem("getFileName", CompletionKind.Method),
                new CompletionItem("doOther", CompletionKind.Method),
                new CompletionItem("elsewhere", CompletionKind.Method),
            };
            var ranked = CompletionRanker.FilterAndRank(items, "gFN");
            // getFileName matches (g / F / N boundaries). Others don't.
            CollectionAssert.AreEquivalent(
                new[] { "getFileName" }, ranked.Select(r => r.Label).ToArray());
        }

        [TestMethod]
        public void Substring_AppearsAfterPrefixMatches()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem("do_apple", CompletionKind.Function),
                new CompletionItem("apple_pie", CompletionKind.Function),
            };
            var ranked = CompletionRanker.FilterAndRank(items, "apple");
            Assert.AreEqual("apple_pie", ranked[0].Label);
            Assert.AreEqual("do_apple", ranked[1].Label);
        }

        [TestMethod]
        public void Fuzzy_MatchesOutOfOrderlessCharacters()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem("get_file_name", CompletionKind.Method),
                new CompletionItem("unrelated", CompletionKind.Method),
            };
            var ranked = CompletionRanker.FilterAndRank(items, "gfn");
            Assert.IsTrue(ranked.Any(r => r.Label == "get_file_name"));
            Assert.IsFalse(ranked.Any(r => r.Label == "unrelated"));
        }

        [TestMethod]
        public void EmptyPrefix_KeepsAllSortedShortestFirst()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem("longer_name", CompletionKind.Variable),
                new CompletionItem("abc", CompletionKind.Variable),
            };
            var ranked = CompletionRanker.FilterAndRank(items, "");
            Assert.AreEqual("abc", ranked[0].Label);
            Assert.AreEqual("longer_name", ranked[1].Label);
        }

        [TestMethod]
        public void NonMatching_IsRejected()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem("apple", CompletionKind.Variable),
            };
            var ranked = CompletionRanker.FilterAndRank(items, "xyz");
            Assert.AreEqual(0, ranked.Count);
        }
    }
}
