using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Emit;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class EmitterTests
    {
        private static string RoundTrip(string source)
        {
            ParseResult r = Python.ParseModule(source);
            Assert.IsFalse(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            return new PythonWriter().WriteModule(r.Module);
        }

        [TestMethod]
        public void EmitsSimpleClass()
        {
            string result = RoundTrip("class Animal:\n    pass\n");
            StringAssert.Contains(result, "class Animal:");
            StringAssert.Contains(result, "pass");
        }

        [TestMethod]
        public void EmitsClassWithBases()
        {
            string result = RoundTrip("class Dog(Animal, Mammal):\n    pass\n");
            StringAssert.Contains(result, "class Dog(Animal, Mammal):");
        }

        [TestMethod]
        public void EmitsDecoratedClass()
        {
            string result = RoundTrip("@dataclass\nclass Point:\n    pass\n");
            StringAssert.Contains(result, "@dataclass");
            StringAssert.Contains(result, "class Point:");
        }

        [TestMethod]
        public void EmitsFunctionWithAnnotations()
        {
            string result = RoundTrip("def greet(name: str) -> str:\n    pass\n");
            StringAssert.Contains(result, "def greet(name: str) -> str:");
        }

        [TestMethod]
        public void EmitsAsyncDef()
        {
            string result = RoundTrip("async def fetch():\n    pass\n");
            StringAssert.Contains(result, "async def fetch():");
        }

        [TestMethod]
        public void EmitsPep695TypeParameters()
        {
            string result = RoundTrip("class Stack[T]:\n    pass\n");
            StringAssert.Contains(result, "class Stack[T]:");
        }

        [TestMethod]
        public void EmitsFromImport()
        {
            string result = RoundTrip("from typing import Protocol, List as L\n");
            StringAssert.Contains(result, "from typing import Protocol, List as L");
        }

        [TestMethod]
        public void EmitsClassWithMethods()
        {
            string src = "class Car:\n    def drive(self, speed: int) -> None:\n        pass\n";
            string result = RoundTrip(src);
            StringAssert.Contains(result, "class Car:");
            StringAssert.Contains(result, "def drive(self, speed: int) -> None:");
        }

        [TestMethod]
        public void EmitsMetaclassKeyword()
        {
            string result = RoundTrip("class Foo(metaclass=ABCMeta):\n    pass\n");
            StringAssert.Contains(result, "metaclass=ABCMeta");
        }
    }
}
