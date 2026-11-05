using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CppCompletionEngineTests
    {
        [TestMethod]
        public void CatalogUsesReadableTypesAndParameterDocumentation()
        {
            CppCatalog.Entry memcpy = CppCatalog.Get("memcpy");
            Assert.IsNotNull(memcpy);
            Assert.AreEqual("void* memcpy(void* destination, const void* source, size_t byte_count)", memcpy.Signature);
            StringAssert.Contains(memcpy.Documentation, "destination");
            StringAssert.Contains(memcpy.Documentation, "overlap");
        }

        [TestMethod]
        public void Cpp26KeywordsAndLibraryItemsAreOffered()
        {
            var items = new CppCompletionEngine().GetCompletions("", 0, "sample.cpp", null);
            Assert.IsTrue(items.Any(i => i.Label == "concept"));
            Assert.IsTrue(items.Any(i => i.Label == "co_await"));
            Assert.IsTrue(items.Any(i => i.Label == "string_view" && i.Detail == "std::string_view"));
            Assert.IsTrue(items.Any(i => i.Label == "make_unique" && i.Documentation.Contains("C++14")));
        }

        [TestMethod]
        public void CurrentTranslationUnitFunctionsAndTypesAreOffered()
        {
            const string source = "struct Particle { float x; };\nfloat energy(const Particle& p) { return p.x; }\n";
            var items = new CppCompletionEngine().GetCompletions(source, source.Length, "sample.cpp", null);
            Assert.IsTrue(items.Any(i => i.Label == "Particle" && i.Kind == CompletionKind.Class));
            Assert.IsTrue(items.Any(i => i.Label == "energy" && i.Detail.Contains("Particle")));
        }

        [TestMethod]
        public void ScopeResolutionClassifiesAsMemberCompletion()
        {
            CompletionContext context = new CppCompletionEngine().Classify("std::vec", 8);
            Assert.AreEqual(CompletionContextKind.Member, context.Kind);
            Assert.AreEqual("vec", context.Prefix);
        }

        [TestMethod]
        public void PreprocessorLineOffersDirectivesOnly()
        {
            const string source = "  #in";
            var items = new CppCompletionEngine().GetCompletions(source, source.Length, "sample.cpp", null);
            Assert.IsTrue(items.Any(i => i.Label == "include"));
            Assert.IsTrue(items.Any(i => i.Label == "ifndef"));
            Assert.IsFalse(items.Any(i => i.Label == "int"), "ordinary language keywords stay out of directive completion");
        }
    }
}
