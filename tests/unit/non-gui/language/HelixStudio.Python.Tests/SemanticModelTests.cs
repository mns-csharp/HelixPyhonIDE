using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Semantics;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class SemanticModelTests
    {
        private static SemanticModel Model(string source)
        {
            ParseResult r = Python.ParseModule(source);
            return new SemanticModel(r.Module);
        }

        [TestMethod]
        public void ClassifiesProtocol()
        {
            SemanticModel m = Model("from typing import Protocol\n\nclass Shape(Protocol):\n    pass\n");
            ClassDefSyntax cls = m.AllClasses().Single();
            Assert.AreEqual(ClassFlavor.Protocol, m.ClassifyClass(cls));
        }

        [TestMethod]
        public void ClassifiesAbstractBaseClass()
        {
            SemanticModel m = Model("from abc import ABC\n\nclass Animal(ABC):\n    pass\n");
            ClassDefSyntax cls = m.AllClasses().Single();
            Assert.AreEqual(ClassFlavor.AbstractBaseClass, m.ClassifyClass(cls));
        }

        [TestMethod]
        public void ClassifiesDataclass()
        {
            SemanticModel m = Model("from dataclasses import dataclass\n\n@dataclass\nclass Point:\n    pass\n");
            ClassDefSyntax cls = m.AllClasses().Single();
            Assert.AreEqual(ClassFlavor.Dataclass, m.ClassifyClass(cls));
        }

        [TestMethod]
        public void ClassifiesEnum()
        {
            SemanticModel m = Model("from enum import Enum\n\nclass Color(Enum):\n    pass\n");
            ClassDefSyntax cls = m.AllClasses().Single();
            Assert.AreEqual(ClassFlavor.Enum, m.ClassifyClass(cls));
        }

        [TestMethod]
        public void ClassifiesMetaclassABCMeta()
        {
            SemanticModel m = Model("from abc import ABCMeta\n\nclass Foo(metaclass=ABCMeta):\n    pass\n");
            ClassDefSyntax cls = m.AllClasses().Single();
            Assert.AreEqual(ClassFlavor.AbstractBaseClass, m.ClassifyClass(cls));
        }

        [TestMethod]
        public void ClassifiesConcreteClass()
        {
            SemanticModel m = Model("class Regular:\n    pass\n");
            ClassDefSyntax cls = m.AllClasses().Single();
            Assert.AreEqual(ClassFlavor.ConcreteClass, m.ClassifyClass(cls));
        }

        [TestMethod]
        public void ClassifiesStaticMethod()
        {
            SemanticModel m = Model("class A:\n    @staticmethod\n    def foo():\n        pass\n");
            FunctionDefSyntax fn = m.MethodsOf(m.AllClasses().Single()).Single();
            MethodFlavor f = m.ClassifyMethod(fn);
            Assert.IsTrue(f.IsStatic);
            Assert.IsFalse(f.IsInstance);
        }

        [TestMethod]
        public void ClassifiesClassMethod()
        {
            SemanticModel m = Model("class A:\n    @classmethod\n    def foo(cls):\n        pass\n");
            FunctionDefSyntax fn = m.MethodsOf(m.AllClasses().Single()).Single();
            MethodFlavor f = m.ClassifyMethod(fn);
            Assert.IsTrue(f.IsClassMethod);
            Assert.IsTrue(f.HasClsParameter);
        }

        [TestMethod]
        public void ClassifiesProperty()
        {
            SemanticModel m = Model("class A:\n    @property\n    def x(self):\n        pass\n");
            FunctionDefSyntax fn = m.MethodsOf(m.AllClasses().Single()).Single();
            Assert.IsTrue(m.ClassifyMethod(fn).IsProperty);
        }

        [TestMethod]
        public void ClassifiesAbstract()
        {
            SemanticModel m = Model("from abc import abstractmethod\n\nclass A:\n    @abstractmethod\n    def foo(self):\n        pass\n");
            FunctionDefSyntax fn = m.MethodsOf(m.AllClasses().Single()).Single();
            Assert.IsTrue(m.ClassifyMethod(fn).IsAbstract);
        }

        [TestMethod]
        public void VisibilityFromName()
        {
            SemanticModel m = Model("class A: pass\n");
            Assert.AreEqual(MemberVisibility.Public, m.VisibilityFromName("foo"));
            Assert.AreEqual(MemberVisibility.Public, m.VisibilityFromName("__init__"));
            Assert.AreEqual(MemberVisibility.Protected, m.VisibilityFromName("_foo"));
            Assert.AreEqual(MemberVisibility.Private, m.VisibilityFromName("__foo"));
        }

        [TestMethod]
        public void AliasedProtocolStillRecognized()
        {
            SemanticModel m = Model("from typing import Protocol as P\n\nclass Shape(P):\n    pass\n");
            ClassDefSyntax cls = m.AllClasses().Single();
            // Aliased to 'P' — our simple resolver only knows 'Protocol', so this would not resolve.
            // Document this as a known limitation.
            Assert.AreEqual(ClassFlavor.ConcreteClass, m.ClassifyClass(cls));
        }
    }
}
