using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class StaticMethodBindingTests
    {
        [TestMethod]
        public void StaticMethodFirstParameterNamedClsIsNotImplicitlyBound()
        {
            const string source =
                "class Transformer:\n" +
                "    @staticmethod\n" +
                "    def required(cls: object, stmt: object, lhs: object) -> bool:\n" +
                "        return True\n" +
                "    def collect(self, cls: object, stmt: object, lhs: object) -> bool:\n" +
                "        return self.required(cls, stmt, lhs)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "plugin.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6002"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void OrdinaryInstanceMethodStillBindsSelf()
        {
            const string source =
                "class Transformer:\n" +
                "    def required(self, stmt: object) -> bool:\n" +
                "        return True\n" +
                "    def collect(self, stmt: object) -> bool:\n" +
                "        return self.required(stmt)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "plugin.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6002"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void OrdinaryMethodAccessedThroughClassKeepsExplicitReceiver()
        {
            const string source =
                "class Number:\n" +
                "    def add(self, other: object) -> object:\n" +
                "        return other\n" +
                "    def combine(self, other: object) -> object:\n" +
                "        return Number.add(self, other)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "number.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6002"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void ClassMethodAccessedThroughClassBindsCls()
        {
            const string source =
                "class Factory:\n" +
                "    @classmethod\n" +
                "    def make(cls, value: object) -> object:\n" +
                "        return value\n" +
                "result = Factory.make(1)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "factory.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6002"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void InstanceDescriptorBindsReceiverRegardlessOfItsName()
        {
            const string source =
                "class Poly:\n" +
                "    def monoms(f, order=None):\n" +
                "        return []\n" +
                "    def collect(self):\n" +
                "        return self.monoms()\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "poly.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6002"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void ConstructorUsesCustomNewWhenInitIsAbsent()
        {
            const string source =
                "class Region:\n" +
                "    def __new__(cls, definition, *bounds):\n" +
                "        return object.__new__(cls)\n" +
                "Region((1, 2), (3, 4, 5))\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "region.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6002"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void MemoizePropertyDecoratorYieldsValueNotBoundCallable()
        {
            const string source =
                "def memoize_property(fn):\n" +
                "    return property(fn)\n" +
                "class Keys:\n" +
                "    @memoize_property\n" +
                "    def predicate(self):\n" +
                "        return lambda left, right: left\n" +
                "Keys().predicate(1, 2)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "keys.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6002"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }
    }
}
