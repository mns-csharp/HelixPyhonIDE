using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class SlotsAndFactoryAttributeTests
    {
        private static bool Missing(CompileResult result, string member)
        {
            return result.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound &&
                d.Message.Contains("'" + member + "'"));
        }

        [TestMethod]
        public void LiteralSlotsDeclareInstanceAttributes()
        {
            const string source =
                "class Rational:\n" +
                "    __slots__ = ('numerator', 'denominator')\n" +
                "    def ratio(self):\n" +
                "        return self.numerator / self.denominator\n";
            CompileResult result = VirtualCompiler.CompileSource(source);

            string diagnostics = string.Join(", ",
                result.Diagnostics.Select(d => d.Code + " " + d.Message));
            Assert.IsFalse(Missing(result, "numerator"), diagnostics);
            Assert.IsFalse(Missing(result, "denominator"), diagnostics);
        }

        [TestMethod]
        public void ClassFactoryReturnedObjectAssignmentsDeclareAttributes()
        {
            CompileResult result = VirtualCompiler.CompileSource(
                "class Matrix:\n" +
                "    @classmethod\n" +
                "    def _new(cls, value):\n" +
                "        obj = object.__new__(cls)\n" +
                "        obj.rep, obj.shape = value, (1, 1)\n" +
                "        return obj\n" +
                "    def rows(self):\n" +
                "        return self.rep, self.shape\n");

            Assert.IsFalse(Missing(result, "rep"));
            Assert.IsFalse(Missing(result, "shape"));
        }

        [TestMethod]
        public void UnreturnedOtherObjectDoesNotDeclareAnInstanceAttribute()
        {
            CompileResult result = VirtualCompiler.CompileSource(
                "class C:\n" +
                "    def helper(self, other):\n" +
                "        other.accidental = 1\n" +
                "    def read(self):\n" +
                "        return self.accidental\n");

            Assert.IsTrue(Missing(result, "accidental"));
        }

        [TestMethod]
        public void ClassMethodAssignmentsAreVisibleThroughInstances()
        {
            CompileResult result = VirtualCompiler.CompileSource(
                "class TestModel:\n" +
                "    @classmethod\n" +
                "    def setup_class(cls):\n" +
                "        cls.mass, cls.radius = 1, 2\n" +
                "    def check(self):\n" +
                "        return self.mass + self.radius\n");

            Assert.IsFalse(Missing(result, "mass"));
            Assert.IsFalse(Missing(result, "radius"));
        }

        [TestMethod]
        public void ArbitrarilyNamedMethodReceiverAssignmentsDeclareAttributes()
        {
            CompileResult result = VirtualCompiler.CompileSource(
                "class Record:\n" +
                "    def initialize(record):\n" +
                "        record.value = 1\n" +
                "    def read(self):\n" +
                "        return self.value\n");

            Assert.IsFalse(Missing(result, "value"));
        }

        [TestMethod]
        public void UnpackedClassAssignmentDeclaresEveryClassAttribute()
        {
            CompileResult result = VirtualCompiler.CompileSource(
                "class Pretty:\n" +
                "    ATOM, FUNC, DIV = range(3)\n" +
                "    def render(self):\n" +
                "        return self.ATOM + self.FUNC + self.DIV\n");

            Assert.IsFalse(Missing(result, "ATOM"));
            Assert.IsFalse(Missing(result, "FUNC"));
            Assert.IsFalse(Missing(result, "DIV"));
        }
    }
}
