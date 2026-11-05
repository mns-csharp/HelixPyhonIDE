using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class ParserTests
    {
        [TestMethod]
        public void ParsesEmptyModule()
        {
            ParseResult result = Python.ParseModule("");
            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(0, result.Module.Statements.Count);
        }

        [TestMethod]
        public void ParsesSimpleClass()
        {
            ParseResult result = Python.ParseModule("class Animal:\n    pass\n");
            Assert.IsFalse(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().FirstOrDefault();
            Assert.IsNotNull(cls);
            Assert.AreEqual("Animal", cls.Name);
            Assert.AreEqual(0, cls.BaseExpressions.Count);
        }

        [TestMethod]
        public void ParsesClassWithBases()
        {
            ParseResult result = Python.ParseModule("class Dog(Animal, Mammal):\n    pass\n");
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual(2, cls.BaseExpressions.Count);
            Assert.AreEqual("Animal", ((NameExpression)cls.BaseExpressions[0]).Name);
            Assert.AreEqual("Mammal", ((NameExpression)cls.BaseExpressions[1]).Name);
        }

        [TestMethod]
        public void ParsesClassWithDottedBase()
        {
            ParseResult result = Python.ParseModule("class Foo(abc.ABC):\n    pass\n");
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            AttributeExpression baseName = (AttributeExpression)cls.BaseExpressions[0];
            Assert.AreEqual("ABC", baseName.Name);
            Assert.AreEqual("abc", ((NameExpression)baseName.Target).Name);
        }

        [TestMethod]
        public void ParsesClassWithKeywordArguments()
        {
            ParseResult result = Python.ParseModule("class Meta(metaclass=ABCMeta):\n    pass\n");
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual(1, cls.KeywordArguments.Count);
            Assert.AreEqual("metaclass", cls.KeywordArguments[0].Key);
        }

        [TestMethod]
        public void ParsesDecoratedClass()
        {
            string src = "@dataclass\nclass Point:\n    pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual(1, cls.Decorators.Count);
        }

        [TestMethod]
        public void ParsesFunctionWithAnnotationsAndDefaults()
        {
            string src = "def greet(name: str, loud: bool = False) -> str:\n    pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            FunctionDefSyntax fn = result.Module.Statements.OfType<FunctionDefSyntax>().Single();
            Assert.AreEqual("greet", fn.Name);
            Assert.AreEqual(2, fn.Parameters.Count);
            Assert.IsNotNull(fn.Parameters[0].Annotation);
            Assert.IsNotNull(fn.Parameters[1].DefaultValue);
            Assert.IsNotNull(fn.ReturnAnnotation);
        }

        [TestMethod]
        public void ParsesAsyncDef()
        {
            ParseResult result = Python.ParseModule("async def fetch():\n    pass\n");
            Assert.IsFalse(result.HasErrors);
            FunctionDefSyntax fn = result.Module.Statements.OfType<FunctionDefSyntax>().Single();
            Assert.IsTrue(fn.IsAsync);
        }

        [TestMethod]
        public void ParsesDecoratedFunction()
        {
            string src = "@staticmethod\n@functools.lru_cache\ndef f():\n    pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            FunctionDefSyntax fn = result.Module.Statements.OfType<FunctionDefSyntax>().Single();
            Assert.AreEqual(2, fn.Decorators.Count);
        }

        [TestMethod]
        public void ParsesImport()
        {
            ParseResult result = Python.ParseModule("import os, sys as system\n");
            Assert.IsFalse(result.HasErrors);
            ImportStatement imp = result.Module.Statements.OfType<ImportStatement>().Single();
            Assert.AreEqual(2, imp.Names.Count);
            Assert.AreEqual("system", imp.Names[1].Alias);
        }

        [TestMethod]
        public void ParsesFromImport()
        {
            ParseResult result = Python.ParseModule("from typing import Protocol, List as L\n");
            Assert.IsFalse(result.HasErrors);
            FromImportStatement imp = result.Module.Statements.OfType<FromImportStatement>().Single();
            Assert.AreEqual("typing", imp.Module.AsString);
            Assert.AreEqual(2, imp.Names.Count);
            Assert.AreEqual("L", imp.Names[1].Value);
        }

        [TestMethod]
        public void ParsesRelativeFromImport()
        {
            ParseResult result = Python.ParseModule("from ..pkg import foo\n");
            Assert.IsFalse(result.HasErrors);
            FromImportStatement imp = result.Module.Statements.OfType<FromImportStatement>().Single();
            Assert.AreEqual(2, imp.RelativeDots);
        }

        [TestMethod]
        public void ParsesNestedClass()
        {
            string src = "class Outer:\n    class Inner:\n        pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax outer = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual("Outer", outer.Name);
            ClassDefSyntax inner = outer.Body.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual("Inner", inner.Name);
        }

        [TestMethod]
        public void ParsesClassWithMethods()
        {
            string src = "class Car:\n    def drive(self, speed: int) -> None:\n        pass\n    def stop(self):\n        pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual(2, cls.Body.Statements.OfType<FunctionDefSyntax>().Count());
        }

        [TestMethod]
        public void ParsesPep695TypeParameters()
        {
            string src = "class Stack[T]:\n    pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual(1, cls.TypeParameters.Count);
            Assert.AreEqual("T", cls.TypeParameters[0].Name);
        }

        [TestMethod]
        public void ParsesPep695VariadicTypeParameter()
        {
            string src = "def foo[T, *Ts, **P]():\n    pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            FunctionDefSyntax fn = result.Module.Statements.OfType<FunctionDefSyntax>().Single();
            Assert.AreEqual(3, fn.TypeParameters.Count);
            Assert.AreEqual(TypeParameterSyntax.TypeParamKind.TypeVarTuple, fn.TypeParameters[1].Kind);
            Assert.AreEqual(TypeParameterSyntax.TypeParamKind.ParamSpec, fn.TypeParameters[2].Kind);
        }

        [TestMethod]
        public void AssignmentStatementsParsedInClassBody()
        {
            string src = "class A:\n    x = 1 + 2\n    y = [1, 2, 3]\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            // The new parser produces typed Assignment statements instead of UnparsedStatement spans.
            Assert.AreEqual(2, cls.Body.Statements.OfType<AssignmentStatement>().Count());
            Assert.AreEqual(0, cls.Body.Statements.OfType<UnparsedStatement>().Count());
        }

        [TestMethod]
        public void ProtocolClassParses()
        {
            string src = "from typing import Protocol\n\nclass Shape(Protocol):\n    def area(self) -> float: ...\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            Assert.AreEqual("Shape", cls.Name);
            Assert.AreEqual(1, cls.BaseExpressions.Count);
            Assert.AreEqual("Protocol", ((NameExpression)cls.BaseExpressions[0]).Name);
            FunctionDefSyntax method = cls.Body.Statements.OfType<FunctionDefSyntax>().Single();
            Assert.AreEqual("area", method.Name);
        }

        [TestMethod]
        public void AbcClassParses()
        {
            string src = "from abc import ABC, abstractmethod\n\n" +
                         "class Animal(ABC):\n" +
                         "    @abstractmethod\n" +
                         "    def speak(self) -> str:\n" +
                         "        pass\n";
            ParseResult result = Python.ParseModule(src);
            Assert.IsFalse(result.HasErrors);
            ClassDefSyntax cls = result.Module.Statements.OfType<ClassDefSyntax>().Single();
            FunctionDefSyntax m = cls.Body.Statements.OfType<FunctionDefSyntax>().Single();
            Assert.AreEqual(1, m.Decorators.Count);
        }
    }
}
