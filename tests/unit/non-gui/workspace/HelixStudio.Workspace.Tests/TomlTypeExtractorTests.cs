using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Toml;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// FR-DES-05 / FR-PROJ-07: TOML projected onto the shared GDI+ OOP canvas — each table becomes a
    /// <see cref="TypeModel"/> box (scalar keys as typed fields), sub-tables/array-of-tables become linked boxes.
    /// </summary>
    [TestClass]
    public class TomlTypeExtractorTests
    {
        private static ProjectModel Extract(string toml)
        {
            var sln = new SolutionModel("t.sln", "T");
            var proj = new ProjectModel("pyproject.toml", "cfg", sln);
            sln.Projects.Add(proj);
            var doc = TomlParser.Parse(toml);
            TomlTypeExtractor.ExtractInto(proj, doc, "pyproject.toml");
            return proj;
        }

        [TestMethod]
        public void EachTableBecomesABox_WithScalarKeysAsFields()
        {
            var proj = Extract(
                "[project]\nname = \"myapp\"\nversion = \"0.1.0\"\nrequires-python = \">=3.12\"\n");
            var project = proj.Types.Single(t => t.Name == "project");
            Assert.AreEqual(TypeKind.ConcreteClass, project.TypeKind);
            var fieldNames = project.Members.Select(m => m.Name).ToArray();
            CollectionAssert.AreEquivalent(new[] { "name", "version", "requires-python" }, fieldNames);
            Assert.IsTrue(project.Members.All(m => m.MemberKind == MemberKind.Field));
            Assert.AreEqual("string", project.Members.Single(m => m.Name == "name").ReturnType);
        }

        [TestMethod]
        public void NestedTables_BecomeSeparateBoxes_LinkedToParent()
        {
            var proj = Extract("[tool.uv.workspace]\nmembers = [\"a\", \"b\"]\n");
            // tables: (root), tool, tool.uv, tool.uv.workspace
            var names = proj.Types.Select(t => t.Name).ToArray();
            CollectionAssert.Contains(names, "tool");
            CollectionAssert.Contains(names, "tool.uv");
            CollectionAssert.Contains(names, "tool.uv.workspace");
            var ws = proj.Types.Single(t => t.Name == "tool.uv.workspace");
            Assert.IsTrue(ws.BaseTypes.Any(r => r.RelationshipKind == RelationshipKind.DependsOn),
                "sub-table must link to its parent via a containment edge");
            Assert.AreEqual("array[2]", ws.Members.Single(m => m.Name == "members").ReturnType);
        }

        [TestMethod]
        public void ArrayOfTables_BecomesABox_WithElementSchema()
        {
            var proj = Extract(
                "[[tool.targets]]\nname = \"cli\"\nkind = \"console\"\n" +
                "[[tool.targets]]\nname = \"gui\"\nkind = \"window\"\n");
            var aot = proj.Types.SingleOrDefault(t => t.Name == "tool.targets[]");
            Assert.IsNotNull(aot, "array-of-tables must render as its own box");
            Assert.AreEqual(TypeKind.Struct, aot.TypeKind);
            Assert.AreEqual("2", aot.Members.Single(m => m.Name == "«count»").ReturnType);
            CollectionAssert.IsSubsetOf(new[] { "name", "kind" }, aot.Members.Select(m => m.Name).ToArray());
        }

        [TestMethod]
        public void ScalarValueKinds_AreTyped()
        {
            var proj = Extract("[t]\ns = \"x\"\ni = 42\nf = 3.14\nb = true\narr = [1, 2, 3]\n");
            var t = proj.Types.Single(x => x.Name == "t");
            Assert.AreEqual("string", t.Members.Single(m => m.Name == "s").ReturnType);
            Assert.AreEqual("int", t.Members.Single(m => m.Name == "i").ReturnType);
            Assert.AreEqual("float", t.Members.Single(m => m.Name == "f").ReturnType);
            Assert.AreEqual("bool", t.Members.Single(m => m.Name == "b").ReturnType);
            Assert.AreEqual("array[3]", t.Members.Single(m => m.Name == "arr").ReturnType);
        }
    }
}
