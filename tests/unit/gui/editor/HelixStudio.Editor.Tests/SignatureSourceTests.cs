namespace HelixPythonNamespace.Editor.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Catalog;
    using HelixPythonNamespace.Editor;

    [TestClass]
    public class SignatureSourceTests
    {
        [TestMethod]
        public void BuildProducesTextAndParamSpans()
        {
            SignatureInfo info = SignatureSource.Build("f", new[] { "a", "b" }, "int");
            Assert.AreEqual("f(a, b) -> int", info.Text);
            Assert.AreEqual(2, info.ParamSpans.Count);
            Assert.AreEqual("a", info.Text.Substring(info.ParamSpans[0].Start, info.ParamSpans[0].End - info.ParamSpans[0].Start));
            Assert.AreEqual("b", info.Text.Substring(info.ParamSpans[1].Start, info.ParamSpans[1].End - info.ParamSpans[1].Start));
        }

        [TestMethod]
        public void LookupReturnsBuiltinSignature()
        {
            SignatureSource s = new SignatureSource();
            SignatureInfo print = s.Lookup("print");
            Assert.IsNotNull(print);
            Assert.IsTrue(print.Text.StartsWith("print("));
        }

        [TestMethod]
        public void LookupReturnsNullForUnknown()
        {
            SignatureSource s = new SignatureSource();
            Assert.IsNull(s.Lookup("definitely_not_a_builtin_xyz"));
        }

        [TestMethod]
        public void CatalogTakesPriorityOverBuiltins()
        {
            PythonCatalog cat = new PythonCatalog();
            cat.Add(new CatalogSymbol
            {
                Name = "myfunc",
                Signature = "myfunc(a, b)",
                Summary = "does a thing",
                Kind = "function",
            });
            SignatureSource s = new SignatureSource();
            s.SetCatalog(cat);

            SignatureInfo info = s.Lookup("myfunc");
            Assert.IsNotNull(info);
            Assert.IsTrue(info.Text.StartsWith("myfunc(a, b)"));
            Assert.IsTrue(info.Text.Contains("does a thing"));
            Assert.AreEqual(2, info.ParamSpans.Count);
        }

        [TestMethod]
        public void ProjectCatalogWinsOverSystemCatalog()
        {
            PythonCatalog sys = new PythonCatalog();
            sys.Add(new CatalogSymbol { Name = "shared", Signature = "shared(sys_param)", Summary = "system" });
            PythonCatalog proj = new PythonCatalog();
            proj.Add(new CatalogSymbol { Name = "shared", Signature = "shared(proj_param)", Summary = "project" });

            SignatureSource s = new SignatureSource();
            s.SetCatalog(sys);
            s.SetProjectCatalog(proj);

            SignatureInfo info = s.Lookup("shared");
            Assert.IsTrue(info.Text.StartsWith("shared(proj_param)"));
        }

        [TestMethod]
        public void WrapPreservingLinesBreaksLongText()
        {
            // maxCols has a floor of 16 (faithful to the C++), so request a wider wrap.
            string wrapped = SignatureSource.WrapPreservingLines(
                "alpha beta gamma delta epsilon zeta eta theta iota kappa", 20);
            Assert.IsTrue(wrapped.Contains("\n"));
            foreach (string line in wrapped.Split('\n'))
                Assert.IsTrue(line.Length <= 20, "line too long: " + line);
        }
    }
}
