namespace HelixPythonNamespace.Catalog.Tests
{
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Catalog;

    [TestClass]
    public class PythonCatalogTests
    {
        private static CatalogSymbol Sym(string mod, string name, string kind)
        {
            return new CatalogSymbol { ModuleFqn = mod, Name = name, Kind = kind, Signature = name + "()" };
        }

        [TestMethod]
        public void LookupByFqnAndBareName()
        {
            PythonCatalog c = new PythonCatalog();
            c.Add(Sym("os.path", "join", "function"));
            Assert.IsNotNull(c.Lookup("os.path.join"));
            Assert.AreEqual("function", c.Lookup("os.path.join").Kind);
            Assert.IsNotNull(c.LookupByName("join"));
            Assert.IsNull(c.Lookup("os.path.missing"));
            Assert.AreEqual(1, c.Size);
        }

        [TestMethod]
        public void ResolveImport()
        {
            PythonCatalog c = new PythonCatalog();
            c.Add(Sym("collections", "OrderedDict", "class"));
            Assert.IsNotNull(c.ResolveImport("collections", "OrderedDict"));
            Assert.IsNull(c.ResolveImport("collections", "Nope"));
            // bare-name fallback when module empty
            Assert.IsNotNull(c.ResolveImport("", "OrderedDict"));
        }

        [TestMethod]
        public void BareNameFirstRegistrationWins()
        {
            PythonCatalog c = new PythonCatalog();
            c.Add(Sym("a", "thing", "function"));
            c.Add(Sym("b", "thing", "class"));
            Assert.AreEqual("function", c.LookupByName("thing").Kind);
            Assert.AreEqual(2, c.Size);
        }

        [TestMethod]
        public void MergeCombinesEntries()
        {
            PythonCatalog a = new PythonCatalog();
            a.Add(Sym("os", "getcwd", "function"));
            PythonCatalog b = new PythonCatalog();
            b.Add(Sym("sys", "exit", "function"));
            a.Merge(b);
            Assert.IsNotNull(a.Lookup("os.getcwd"));
            Assert.IsNotNull(a.Lookup("sys.exit"));
        }

        [TestMethod]
        public void SaveLoadRoundTrip()
        {
            PythonCatalog c = new PythonCatalog();
            CatalogSymbol s = Sym("os.path", "join", "function");
            s.Summary = "Join paths.";
            s.Params.Add(new CatalogParam { Name = "a", TypeAnnotation = "str" });
            c.Add(s);

            string path = Path.Combine(Path.GetTempPath(), "helix_catalog_test.json");
            try
            {
                Assert.IsTrue(c.Save(path));
                PythonCatalog loaded = new PythonCatalog();
                Assert.IsTrue(loaded.Load(path));
                CatalogSymbol r = loaded.Lookup("os.path.join");
                Assert.IsNotNull(r);
                Assert.AreEqual("Join paths.", r.Summary);
                Assert.AreEqual(1, r.Params.Count);
                Assert.AreEqual("str", r.Params[0].TypeAnnotation);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [TestClass]
    public class InstallationFingerprintTests
    {
        [TestMethod]
        public void FnvHashIsDeterministicAndHex()
        {
            string a = InstallationFingerprint.ToHex(InstallationFingerprint.FnvHash64("abc"));
            string b = InstallationFingerprint.ToHex(InstallationFingerprint.FnvHash64("abc"));
            Assert.AreEqual(a, b);
            Assert.AreEqual(16, a.Length);
            Assert.AreNotEqual(a, InstallationFingerprint.ToHex(InstallationFingerprint.FnvHash64("abd")));
        }

        [TestMethod]
        public void DiffersFromDetectsChanges()
        {
            InstallationFingerprint a = new InstallationFingerprint { InterpreterPath = "/p", InterpreterVersion = "3.11", SitePackagesHash = "x" };
            InstallationFingerprint b = new InstallationFingerprint { InterpreterPath = "/p", InterpreterVersion = "3.11", SitePackagesHash = "x" };
            Assert.IsFalse(InstallationFingerprint.DiffersFrom(a, b));
            b.SitePackagesHash = "y";
            Assert.IsTrue(InstallationFingerprint.DiffersFrom(a, b));
        }

        [TestMethod]
        public void SaveLoadRoundTrip()
        {
            InstallationFingerprint fp = new InstallationFingerprint
            {
                InterpreterPath = "/usr/bin/python3",
                InterpreterVersion = "3.11.4",
                SitePackagesDir = "/sp",
                SitePackagesHash = "deadbeefdeadbeef",
            };
            string path = Path.Combine(Path.GetTempPath(), "helix_fp_test.json");
            try
            {
                Assert.IsTrue(InstallationFingerprint.Save(path, fp));
                InstallationFingerprint loaded;
                Assert.IsTrue(InstallationFingerprint.Load(path, out loaded));
                Assert.IsFalse(InstallationFingerprint.DiffersFrom(fp, loaded));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void EmptyFingerprintForMissingInterpreter()
        {
            InstallationFingerprint fp = InstallationFingerprint.Compute("");
            Assert.IsTrue(fp.IsEmpty());
        }
    }
}
