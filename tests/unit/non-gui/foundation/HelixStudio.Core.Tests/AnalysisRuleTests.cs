using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Analysis.Rules;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class AnalysisRuleTests
    {
        [TestMethod]
        public void MultipleInheritanceFlagsDiamond()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode a = new TypeNode("A", TypeKind.ConcreteClass);
            TypeNode b = new TypeNode("B", TypeKind.ConcreteClass);
            TypeNode child = new TypeNode("C", TypeKind.ConcreteClass);
            d.Nodes.AddRange(new[] { a, b, child });
            d.Relationships.Add(new Relationship(RelationshipKind.Inherits, child, a));
            d.Relationships.Add(new Relationship(RelationshipKind.Inherits, child, b));
            var results = new MultipleInheritanceMroRule().Evaluate(d).ToList();
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(child.Id, results[0].AffectedNodeId);
        }

        [TestMethod]
        public void MultipleInheritanceIgnoresInterfaces()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode a = new TypeNode("A", TypeKind.ConcreteClass);
            TypeNode i1 = new TypeNode("I1", TypeKind.Interface);
            TypeNode i2 = new TypeNode("I2", TypeKind.Interface);
            TypeNode child = new TypeNode("C", TypeKind.ConcreteClass);
            d.Nodes.AddRange(new[] { a, i1, i2, child });
            d.Relationships.Add(new Relationship(RelationshipKind.Inherits, child, a));
            d.Relationships.Add(new Relationship(RelationshipKind.Implements, child, i1));
            d.Relationships.Add(new Relationship(RelationshipKind.Implements, child, i2));
            var results = new MultipleInheritanceMroRule().Evaluate(d).ToList();
            Assert.AreEqual(0, results.Count);
        }

        [TestMethod]
        public void ProtocolSuggestedForPureAbstract()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode abc = new TypeNode("Shape", TypeKind.AbstractClass);
            abc.Members.Add(new MemberDefinition("area", MemberKind.Method) { IsAbstract = true, ReturnType = "float" });
            abc.Members.Add(new MemberDefinition("perimeter", MemberKind.Method) { IsAbstract = true, ReturnType = "float" });
            d.Nodes.Add(abc);
            var results = new ProtocolVsAbcRule().Evaluate(d).ToList();
            Assert.AreEqual(1, results.Count);
        }

        [TestMethod]
        public void ProtocolNotSuggestedWhenStateExists()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode abc = new TypeNode("Animal", TypeKind.AbstractClass);
            abc.Members.Add(new MemberDefinition("name", MemberKind.Field));
            abc.Members.Add(new MemberDefinition("speak", MemberKind.Method) { IsAbstract = true });
            d.Nodes.Add(abc);
            var results = new ProtocolVsAbcRule().Evaluate(d).ToList();
            Assert.AreEqual(0, results.Count);
        }

        [TestMethod]
        public void ProtocolNotSuggestedWhenConcreteMethodExists()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode abc = new TypeNode("Animal", TypeKind.AbstractClass);
            abc.Members.Add(new MemberDefinition("speak", MemberKind.Method) { IsAbstract = true });
            abc.Members.Add(new MemberDefinition("greet", MemberKind.Method));
            d.Nodes.Add(abc);
            var results = new ProtocolVsAbcRule().Evaluate(d).ToList();
            Assert.AreEqual(0, results.Count);
        }

        [TestMethod]
        public void MissingAnnotationFlagsUnannotatedMethod()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode c = new TypeNode("Car", TypeKind.ConcreteClass);
            c.Members.Add(new MemberDefinition("drive", MemberKind.Method)); // no ReturnType
            d.Nodes.Add(c);
            var results = new MissingTypeAnnotationRule().Evaluate(d).ToList();
            Assert.AreEqual(1, results.Count);
        }

        [TestMethod]
        public void MissingAnnotationSkipsDunders()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode c = new TypeNode("Car", TypeKind.ConcreteClass);
            c.Members.Add(new MemberDefinition("__init__", MemberKind.Method));
            d.Nodes.Add(c);
            var results = new MissingTypeAnnotationRule().Evaluate(d).ToList();
            Assert.AreEqual(0, results.Count);
        }

        [TestMethod]
        public void MissingAnnotationPassesWhenAnnotated()
        {
            Diagram d = new Diagram { Name = "t" };
            TypeNode c = new TypeNode("Car", TypeKind.ConcreteClass);
            c.Members.Add(new MemberDefinition("speed", MemberKind.Method) { ReturnType = "int" });
            d.Nodes.Add(c);
            var results = new MissingTypeAnnotationRule().Evaluate(d).ToList();
            Assert.AreEqual(0, results.Count);
        }
    }
}
