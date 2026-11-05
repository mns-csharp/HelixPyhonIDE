using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PyprojectDependencyServiceTests
    {
        private string _dir;
        private string _path;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "helix_dep_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "pyproject.toml");
        }

        [TestCleanup]
        public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

        private void Write(string s) { File.WriteAllText(_path, s); }
        private string Read() { return File.ReadAllText(_path); }

        [TestMethod]
        public void Parse_SplitsNameExtrasSpecifierMarker()
        {
            var d = PyprojectDependencyService.Parse("requests[security]>=2.0,<3 ; python_version >= '3.8'");
            Assert.AreEqual("requests", d.Name);
            Assert.AreEqual("requests", d.CanonicalName);
            Assert.AreEqual("[security]", d.Extras);
            Assert.AreEqual(">=2.0,<3", d.Specifier);
            StringAssert.Contains(d.Marker, "python_version");
        }

        [TestMethod]
        public void CanonicalName_NormalizesPep503()
        {
            Assert.AreEqual("ruamel-yaml", PyprojectDependencyService.CanonicalName("ruamel.yaml"));
            Assert.AreEqual("zope-interface", PyprojectDependencyService.CanonicalName("Zope__Interface"));
        }

        [TestMethod]
        public void List_ReadsInlineDependencies()
        {
            Write("[project]\nname = \"demo\"\ndependencies = [\"numpy>=1.24\", \"requests\"]\n\n[tool.helixstudio]\nkind = \"app\"\n");
            var deps = PyprojectDependencyService.List(_path);
            CollectionAssert.AreEquivalent(new[] { "numpy", "requests" }, deps.Select(d => d.Name).ToArray());
        }

        [TestMethod]
        public void Add_AppendsAndPreservesOtherTables()
        {
            Write("[project]\nname = \"demo\"\nrequires-python = \">=3.12\"\ndependencies = [\"numpy\"]\n\n"
                + "[tool.uv.workspace]\nmembers = [\"a\", \"b\"]\n\n[tool.helixstudio]\nkind = \"app\"\n");
            bool changed = PyprojectDependencyService.Add(_path, "requests>=2.31");
            Assert.IsTrue(changed);
            string outp = Read();
            // dependency added...
            var deps = PyprojectDependencyService.List(_path);
            CollectionAssert.AreEquivalent(new[] { "numpy", "requests" }, deps.Select(d => d.Name).ToArray());
            Assert.IsTrue(deps.Any(d => d.Name == "requests" && d.Specifier == ">=2.31"));
            // ...and the OTHER tables survive verbatim (NFR-04 round-trip safety).
            StringAssert.Contains(outp, "[tool.uv.workspace]");
            StringAssert.Contains(outp, "members = [\"a\", \"b\"]");
            StringAssert.Contains(outp, "[tool.helixstudio]");
        }

        [TestMethod]
        public void Add_SameName_ReplacesSpecifier_NoDuplicate()
        {
            Write("[project]\nname = \"demo\"\ndependencies = [\"numpy>=1.20\"]\n");
            PyprojectDependencyService.Add(_path, "numpy>=1.26");
            var deps = PyprojectDependencyService.List(_path);
            Assert.AreEqual(1, deps.Count(d => d.CanonicalName == "numpy"), "no duplicate numpy");
            Assert.AreEqual(">=1.26", deps.Single(d => d.Name == "numpy").Specifier);
        }

        [TestMethod]
        public void Remove_DropsByCanonicalName()
        {
            Write("[project]\nname = \"demo\"\ndependencies = [\"numpy\", \"requests\", \"Flask\"]\n");
            bool changed = PyprojectDependencyService.Remove(_path, "flask"); // case/canonical-insensitive
            Assert.IsTrue(changed);
            var deps = PyprojectDependencyService.List(_path);
            CollectionAssert.AreEquivalent(new[] { "numpy", "requests" }, deps.Select(d => d.Name).ToArray());
        }

        [TestMethod]
        public void Add_WhenNoDependenciesKey_InsertsIntoProject()
        {
            Write("[project]\nname = \"demo\"\nrequires-python = \">=3.12\"\n\n[tool.helixstudio]\nkind = \"app\"\n");
            PyprojectDependencyService.Add(_path, "rich");
            var deps = PyprojectDependencyService.List(_path);
            CollectionAssert.AreEqual(new[] { "rich" }, deps.Select(d => d.Name).ToArray());
            StringAssert.Contains(Read(), "[tool.helixstudio]"); // preserved
        }

        [TestMethod]
        public void Remove_NonexistentName_NoChange()
        {
            Write("[project]\nname = \"demo\"\ndependencies = [\"numpy\"]\n");
            Assert.IsFalse(PyprojectDependencyService.Remove(_path, "pandas"));
        }
    }
}
