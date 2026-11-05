using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CppTypeExtractorTests
    {
        private static ProjectModel MakeProject()
        {
            string temp = Path.Combine(Path.GetTempPath(), "helix_cpp_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string marker = Path.Combine(temp, "lib_core.cmake-target");
            SolutionModel sln = new SolutionModel(Path.Combine(temp, "CMakeLists.txt"), "t");
            ProjectModel project = new ProjectModel(marker, "lib_core", sln);
            project.Language = ProjectLanguage.Cpp;
            sln.Projects.Add(project);
            return project;
        }

        private static string WriteTempSource(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "helix_cpp_src_" + System.Guid.NewGuid().ToString("N") + ".hpp");
            File.WriteAllText(path, content);
            return path;
        }

        private static TypeModel FindType(ProjectModel project, string name) =>
            project.Types.FirstOrDefault(t => t.Name == name);

        [TestMethod]
        public void ExtractsSimpleClass()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource("class Animal {\npublic:\n    Animal();\n    virtual void speak();\n};\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            Assert.AreEqual(1, p.Types.Count);
            TypeModel t = FindType(p, "Animal");
            Assert.IsNotNull(t);
            Assert.AreEqual(TypeKind.ConcreteClass, t.TypeKind);
            Assert.AreEqual(2, t.Members.Count);
            Assert.AreEqual(MemberKind.Constructor, t.Members[0].MemberKind);
            Assert.AreEqual(MemberKind.Method, t.Members[1].MemberKind);
            Assert.AreEqual(AccessModifier.Public, t.Members[0].AccessModifier);
        }

        [TestMethod]
        public void ExtractsStructAsStructKind()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource("struct Point3D {\n    double x;\n    double y;\n};\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            TypeModel t = FindType(p, "Point3D");
            Assert.IsNotNull(t);
            Assert.AreEqual(TypeKind.Struct, t.TypeKind);
            Assert.AreEqual(2, t.Members.Count);
            Assert.AreEqual(MemberKind.Field, t.Members[0].MemberKind);
            Assert.AreEqual("double", t.Members[0].ReturnType);
        }

        [TestMethod]
        public void ExtractsBaseClassInheritance()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource(
                "class Rectangle {\npublic:\n    double area();\n};\n" +
                "class Square : public Rectangle {\npublic:\n    double side();\n};\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            TypeModel square = FindType(p, "Square");
            Assert.IsNotNull(square);
            Assert.AreEqual(1, square.BaseTypes.Count);
            Assert.AreEqual("Rectangle", square.BaseTypes[0].FullyQualifiedName);
            Assert.AreEqual(RelationshipKind.Inherits, square.BaseTypes[0].RelationshipKind);
        }

        [TestMethod]
        public void ExtractsPrivateAndProtectedMembers()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource(
                "class Widget {\n" +
                "protected:\n    int protected_field;\n" +
                "private:\n    int private_field;\n" +
                "public:\n    int public_field;\n" +
                "};\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            TypeModel t = FindType(p, "Widget");
            Assert.IsNotNull(t);
            Assert.AreEqual(3, t.Members.Count);
            Assert.AreEqual(AccessModifier.Protected, t.Members.First(m => m.Name == "protected_field").AccessModifier);
            Assert.AreEqual(AccessModifier.Private, t.Members.First(m => m.Name == "private_field").AccessModifier);
            Assert.AreEqual(AccessModifier.Public, t.Members.First(m => m.Name == "public_field").AccessModifier);
        }

        [TestMethod]
        public void ExtractsFreeFunctionsAndNamespaces()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource(
                "namespace lib_core {\n" +
                "    double total_area();\n" +
                "    class Circle { public: double area(); };\n" +
                "}\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            Assert.AreEqual(1, p.Functions.Count);
            Assert.AreEqual("total_area", p.Functions[0].Name);
            Assert.AreEqual("lib_core", p.Functions[0].Namespace);
            TypeModel circle = FindType(p, "Circle");
            Assert.IsNotNull(circle);
            Assert.AreEqual("lib_core", circle.Namespace);
            Assert.AreEqual("lib_core.Circle", circle.FullyQualifiedName);
        }

        [TestMethod]
        public void ExtractsTemplateGenericParameters()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource("template<typename T>\nclass Boxy {\npublic:\n    T value;\n};\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            TypeModel t = FindType(p, "Boxy");
            Assert.IsNotNull(t);
            Assert.AreEqual(1, t.GenericParameters.Count);
            Assert.AreEqual("T", t.GenericParameters[0].Name);
        }

        [TestMethod]
        public void ExtractsNestedClassWithParentFqn()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource("class Outer {\npublic:\n    class Inner { public: void f(); };\n};\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            TypeModel outer = FindType(p, "Outer");
            TypeModel inner = FindType(p, "Inner");
            Assert.IsNotNull(outer);
            Assert.IsNotNull(inner);
            Assert.AreEqual(outer.FullyQualifiedName, inner.ParentTypeFqn);
            Assert.IsTrue(inner.IsNested);
        }

        [TestMethod]
        public void ForwardDeclarationDoesNotOverwriteRealDefinition()
        {
            ProjectModel p = MakeProject();
            string file = WriteTempSource(
                "class Shape;\n" +
                "class Shape {\npublic:\n    double area();\n};\n");
            CppTypeExtractor.ExtractFromFile(p, file);
            Assert.AreEqual(1, p.Types.Count(ty => ty.Name == "Shape"));
            TypeModel t = FindType(p, "Shape");
            Assert.AreEqual(1, t.Members.Count);
        }

        [TestMethod]
        public void ExtractsAgainstRealFixtureShapesHeader()
        {
            // Real, previously-built fixture file (tests/fixtures/cuda/CudaCppMultiPackageWorkspace),
            // not a synthetic snippet — proves the extractor works against genuine hand-written
            // C++23, not just minimal examples crafted to fit the extractor. The header carries the
            // same Shape hierarchy the retired cpp/CppTestSample one did.
            string repoRoot = FindRepoRoot();
            string shapesHeader = Path.Combine(repoRoot, "tests", "fixtures", "cuda",
                "CudaCppMultiPackageWorkspace", "lib_core", "include", "lib_core", "shapes.cuh");
            Assert.IsTrue(File.Exists(shapesHeader), "fixture file not found: " + shapesHeader);

            ProjectModel p = MakeProject();
            CppTypeExtractor.ExtractFromFile(p, shapesHeader);

            TypeModel circle = FindType(p, "Circle");
            Assert.IsNotNull(circle, "Circle should be extracted");
            Assert.IsTrue(circle.Members.Any(m => m.Name == "area"));

            TypeModel square = FindType(p, "Square");
            Assert.IsNotNull(square, "Square should be extracted");
            Assert.AreEqual(1, square.BaseTypes.Count);
            Assert.AreEqual("Rectangle", square.BaseTypes[0].FullyQualifiedName);

            TypeModel star = FindType(p, "Star");
            Assert.IsNotNull(star, "Star should be extracted");
            Assert.AreEqual(1, star.BaseTypes.Count);
            Assert.AreEqual("RegularPolygon", star.BaseTypes[0].FullyQualifiedName);
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && Directory.GetFiles(d.FullName, "*.sln").Length == 0)
                d = d.Parent;
            Assert.IsNotNull(d, "could not locate repo root (the product .sln)");
            return d.FullName;
        }
    }
}
