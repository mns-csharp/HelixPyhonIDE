using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PythonTypeExtractorTests
    {
        private static ProjectModel MakeProject()
        {
            string temp = Path.Combine(Path.GetTempPath(), "helix_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string pyproject = Path.Combine(temp, "pyproject.toml");
            File.WriteAllText(pyproject, "[project]\nname = \"t\"\nrequires-python = \">=3.12\"\n");
            SolutionModel sln = new SolutionModel(pyproject, "t");
            ProjectModel project = new ProjectModel(pyproject, "t", sln);
            project.DefaultNamespace = "t";
            sln.Projects.Add(project);
            return project;
        }

        private static TypeModel First(ProjectModel project) => project.Types.FirstOrDefault();

        [TestMethod]
        public void ExtractsSimpleClass()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class Animal:\n    pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "animal.py");
            Assert.AreEqual(1, p.Types.Count);
            Assert.AreEqual(TypeKind.ConcreteClass, First(p).TypeKind);
        }

        [TestMethod]
        public void ExtractsProtocolAsInterface()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("from typing import Protocol\n\nclass Shape(Protocol):\n    def area(self) -> float: ...\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "shape.py");
            TypeModel t = First(p);
            Assert.AreEqual(TypeKind.Interface, t.TypeKind);
            Assert.AreEqual(0, t.BaseTypes.Count);
        }

        [TestMethod]
        public void ExtractsAbcAsAbstractClass()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("from abc import ABC, abstractmethod\n\nclass Animal(ABC):\n    @abstractmethod\n    def speak(self):\n        pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "animal.py");
            TypeModel t = First(p);
            Assert.AreEqual(TypeKind.AbstractClass, t.TypeKind);
        }

        [TestMethod]
        public void CapturesTypeParams_FromClassicGenericBase()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule(
                "from typing import Generic, TypeVar\n" +
                "T = TypeVar('T')\n" +
                "class Box(Generic[T]):\n" +
                "    def get(self) -> T: ...\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "box.py");
            TypeModel t = First(p);
            Assert.AreEqual(1, t.GenericParameters.Count, "the Generic[T] base contributes one type parameter");
            Assert.AreEqual("T", t.GenericParameters[0].Name);
        }

        [TestMethod]
        public void CapturesMultipleTypeParams_FromProtocolBase()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule(
                "from typing import Protocol, TypeVar\n" +
                "K = TypeVar('K')\n" +
                "V = TypeVar('V')\n" +
                "class Mapping(Protocol[K, V]):\n" +
                "    def get(self, k: K) -> V: ...\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "mapping.py");
            TypeModel t = First(p);
            CollectionAssert.AreEqual(
                new[] { "K", "V" },
                t.GenericParameters.Select(g => g.Name).ToArray());
        }

        [TestMethod]
        public void MarksDeprecatedMethod_FromPep702Decorator()
        {
            ProjectModel p = MakeProject();
            // PEP 702 @deprecated, spelled via `warnings.deprecated`.
            ParseResult r = Python.Python.ParseModule(
                "import warnings\n" +
                "class Vehicle:\n" +
                "    @warnings.deprecated(\"use start_engine\")\n" +
                "    def crank(self) -> None: ...\n" +
                "    def start_engine(self) -> None: ...\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "vehicle.py");
            TypeModel t = First(p);
            MemberModel crank = t.Members.First(m => m.Name == "crank");
            MemberModel start = t.Members.First(m => m.Name == "start_engine");
            Assert.IsTrue(crank.IsDeprecated, "the @deprecated-decorated method should be marked deprecated");
            Assert.IsFalse(start.IsDeprecated, "an undecorated method should not be marked deprecated");
        }

        [TestMethod]
        public void ExtractsInstanceFieldsFromSelfAssignments()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule(
                "class Point:\n    def __init__(self, x, y):\n        self.x = x\n        self.y = y\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "point.py");
            TypeModel t = First(p);
            Assert.IsTrue(t.Members.Any(m => m.MemberKind == MemberKind.Field && m.Name == "x"), "field x extracted");
            Assert.IsTrue(t.Members.Any(m => m.MemberKind == MemberKind.Field && m.Name == "y"), "field y extracted");
            Assert.IsTrue(t.Members.Any(m => m.MemberKind == MemberKind.Constructor && m.Name == "__init__"), "ctor kept");
            // Fields come before methods (conventional class-diagram layout).
            int lastField = t.Members.FindLastIndex(m => m.MemberKind == MemberKind.Field);
            int firstMethodish = t.Members.FindIndex(m => m.MemberKind != MemberKind.Field);
            Assert.IsTrue(lastField < firstMethodish, "fields ordered before methods");
        }

        [TestMethod]
        public void ExtractsClassLevelAndAnnotatedFields()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class C:\n    count: int = 0\n    name = 'x'\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "c.py");
            TypeModel t = First(p);
            MemberModel count = t.Members.FirstOrDefault(m => m.Name == "count");
            Assert.IsNotNull(count, "class-level 'count' field");
            Assert.AreEqual(MemberKind.Field, count.MemberKind);
            Assert.AreEqual("int", count.ReturnType, "annotation captured as field type");
            Assert.IsTrue(t.Members.Any(m => m.Name == "name" && m.MemberKind == MemberKind.Field), "unannotated 'name' field");
        }

        [TestMethod]
        public void FieldVisibilityFollowsUnderscoreConvention()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule(
                "class C:\n    def __init__(self):\n        self.pub = 1\n        self._prot = 2\n        self.__priv = 3\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "c.py");
            TypeModel t = First(p);
            Assert.AreEqual(AccessModifier.Public, t.Members.First(m => m.Name == "pub").AccessModifier);
            Assert.AreEqual(AccessModifier.Protected, t.Members.First(m => m.Name == "_prot").AccessModifier);
            Assert.AreEqual(AccessModifier.Private, t.Members.First(m => m.Name == "__priv").AccessModifier);
        }

        [TestMethod]
        public void FieldDedupedAcrossClassVarAndSelfAssign()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule(
                "class C:\n    x: int = 0\n    def set_it(self):\n        self.x = 5\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "c.py");
            TypeModel t = First(p);
            Assert.AreEqual(1, t.Members.Count(m => m.Name == "x" && m.MemberKind == MemberKind.Field), "x not duplicated");
            Assert.AreEqual("int", t.Members.First(m => m.Name == "x").ReturnType, "annotation type retained");
        }

        [TestMethod]
        public void InfersFieldTypeFromTypedConstructorParam()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule(
                "class Point:\n    def __init__(self, x: int, y: str):\n        self.x = x\n        self.y = y\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "point.py");
            TypeModel t = First(p);
            Assert.AreEqual("int", t.Members.First(m => m.Name == "x").ReturnType, "x inherits param type int");
            Assert.AreEqual("str", t.Members.First(m => m.Name == "y").ReturnType, "y inherits param type str");
        }

        [TestMethod]
        public void DunderClassVarsNotExtractedAsFields()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class C:\n    __slots__ = ('a',)\n    def __init__(self):\n        self.a = 1\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "c.py");
            TypeModel t = First(p);
            Assert.IsFalse(t.Members.Any(m => m.Name == "__slots__"), "__slots__ is machinery, not a field");
            Assert.IsTrue(t.Members.Any(m => m.Name == "a" && m.MemberKind == MemberKind.Field), "real field a kept");
        }

        // TypeReference.EffectiveFqn (pass #33): a base written by short name resolves to the target
        // type's FQN (which the node/visual maps are keyed by); the raw FQN keeps the source spelling.
        [TestMethod]
        public void TypeReference_EffectiveFqn_ResolvesShortBaseNameToFqn()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class Shape:\n    pass\nclass Circle(Shape):\n    pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "shapes.py");
            TypeModel shape = p.Types.First(t => t.Name == "Shape");
            TypeModel circle = p.Types.First(t => t.Name == "Circle");
            TypeReference baseRef = circle.BaseTypes.First();
            baseRef.Bind(p.Solution); // the app binds references during solution load

            Assert.AreEqual("Shape", baseRef.FullyQualifiedName, "raw reference keeps the short source name");
            Assert.AreEqual(shape.FullyQualifiedName, baseRef.EffectiveFqn,
                "EffectiveFqn resolves the short base name to the target type's FQN");

            // An unresolvable reference falls back to its raw name.
            TypeReference dangling = new TypeReference("DoesNotExist", RelationshipKind.Inherits);
            dangling.Bind(p.Solution);
            Assert.AreEqual("DoesNotExist", dangling.EffectiveFqn, "unresolved reference falls back to the raw name");
        }

        // DiagramProjection (pass #39): inheritance relationships must be emitted into the Diagram
        // (used by JSON/metadata export). The base ref's short name must resolve via EffectiveFqn to
        // the target node keyed by FQN — else relationships came out empty.
        [TestMethod]
        public void DiagramProjection_EmitsInheritanceRelationship()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class Shape:\n    pass\nclass Circle(Shape):\n    pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "shapes.py");
            SolutionModel sln = p.Solution;
            foreach (TypeModel t in p.Types)
                foreach (TypeReference b in t.BaseTypes)
                    b.Bind(sln); // the app binds references at solution load

            Diagram d = new DiagramProjection(sln, new LayoutModel(sln.FilePath)).Build();
            Assert.AreEqual(1, d.Relationships.Count, "Circle -> Shape inheritance must be emitted, not dropped");
            Relationship rel = d.Relationships[0];
            Assert.AreEqual(RelationshipKind.Inherits, rel.Kind);
            Assert.AreEqual("Circle", rel.Source.Name);
            Assert.AreEqual("Shape", rel.Target.Name);
        }

        [TestMethod]
        public void ExtractsDataclassAsRecord()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("from dataclasses import dataclass\n\n@dataclass\nclass Point:\n    pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "point.py");
            Assert.AreEqual(TypeKind.Record, First(p).TypeKind);
        }

        [TestMethod]
        public void ExtractsEnumAsEnumKind()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("from enum import Enum\n\nclass Color(Enum):\n    RED = 1\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "color.py");
            Assert.AreEqual(TypeKind.Enum, First(p).TypeKind);
        }

        [TestMethod]
        public void InheritanceProducesBaseType()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class Dog(Animal):\n    pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "dog.py");
            TypeModel t = First(p);
            Assert.AreEqual(1, t.BaseTypes.Count);
            Assert.AreEqual("Animal", t.BaseTypes[0].SimpleName);
            Assert.AreEqual(RelationshipKind.Inherits, t.BaseTypes[0].RelationshipKind);
        }

        [TestMethod]
        public void MethodsAreExtracted()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class Car:\n    def drive(self, speed: int) -> None:\n        pass\n    def stop(self):\n        pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "car.py");
            TypeModel t = First(p);
            Assert.AreEqual(2, t.Members.Count);
            Assert.AreEqual("drive", t.Members[0].Name);
            Assert.AreEqual(1, t.Members[0].Parameters.Count); // self is stripped
            Assert.AreEqual("speed", t.Members[0].Parameters[0].Name);
            Assert.AreEqual("int", t.Members[0].Parameters[0].TypeName);
        }

        [TestMethod]
        public void StaticMethodDetected()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class A:\n    @staticmethod\n    def f():\n        pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "a.py");
            MemberModel m = First(p).Members.Single();
            Assert.IsTrue(m.IsStatic);
        }

        [TestMethod]
        public void PropertyDetected()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class A:\n    @property\n    def x(self) -> int:\n        pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "a.py");
            MemberModel m = First(p).Members.Single();
            Assert.AreEqual(MemberKind.Property, m.MemberKind);
        }

        [TestMethod]
        public void VisibilityFromName()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class A:\n    def foo(self): pass\n    def _bar(self): pass\n    def __baz(self): pass\n    def __init__(self): pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "a.py");
            var members = First(p).Members.ToDictionary(m => m.Name);
            Assert.AreEqual(AccessModifier.Public, members["foo"].AccessModifier);
            Assert.AreEqual(AccessModifier.Protected, members["_bar"].AccessModifier);
            Assert.AreEqual(AccessModifier.Private, members["__baz"].AccessModifier);
            Assert.AreEqual(AccessModifier.Public, members["__init__"].AccessModifier);
            Assert.AreEqual(MemberKind.Constructor, members["__init__"].MemberKind);
        }

        [TestMethod]
        public void NestedClassExtracted()
        {
            ProjectModel p = MakeProject();
            ParseResult r = Python.Python.ParseModule("class Outer:\n    class Inner:\n        pass\n");
            PythonTypeExtractor.ExtractInto(p, r.Module, "outer.py");
            Assert.AreEqual(2, p.Types.Count);
            TypeModel inner = p.Types.First(t => t.Name == "Inner");
            Assert.IsTrue(inner.IsNested);
            Assert.IsTrue(inner.ParentTypeFqn.EndsWith("Outer"));
        }
    }
}
