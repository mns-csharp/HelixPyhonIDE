using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 410 (click stress-test): in `A and B`, a narrowing predicate in A refines the subject for
    /// B, because B is only evaluated when A is truthy. So `isinstance(ctx.command, Group) and
    /// ctx.command.chain` resolves `.chain` on Group (which declares it), not the declared Command
    /// (click core.py:1448). The narrowing is scoped to the expression and does not leak, and `or`
    /// keeps its distinct (un-narrowed) behaviour.
    /// </summary>
    [TestClass]
    public class AndExpressionNarrowingTests
    {
        private static bool HasAttrNotFound(CompileResult r, string member)
        {
            return r.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound &&
                d.Message.Contains("'" + member + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void IsInstanceInAnd_NarrowsSubjectForRightOperand()
        {
            var r = VirtualCompiler.CompileSource(
                "class Command:\n" +
                "    name: str\n" +
                "class Group(Command):\n" +
                "    chain: bool\n" +
                "def check(cmd: Command) -> bool:\n" +
                "    return isinstance(cmd, Group) and cmd.chain\n");
            Assert.IsFalse(HasAttrNotFound(r, "chain"), Dump(r));
        }

        [TestMethod]
        public void AttributePathIsInstanceInAnd_Narrows()
        {
            // The subject is a dotted path (ctx.command), as in click core.py.
            var r = VirtualCompiler.CompileSource(
                "class Command:\n" +
                "    name: str\n" +
                "class Group(Command):\n" +
                "    chain: bool\n" +
                "class Ctx:\n" +
                "    command: Command\n" +
                "def check(ctx: Ctx) -> bool:\n" +
                "    return isinstance(ctx.command, Group) and ctx.command.chain\n");
            Assert.IsFalse(HasAttrNotFound(r, "chain"), Dump(r));
        }

        [TestMethod]
        public void NarrowingDoesNotLeakPastTheAndExpression()
        {
            // After the `and` expression, cmd is back to its declared Command type, so accessing a
            // Group-only attribute on a plain statement must still be flagged.
            var r = VirtualCompiler.CompileSource(
                "class Command:\n" +
                "    name: str\n" +
                "class Group(Command):\n" +
                "    chain: bool\n" +
                "def check(cmd: Command) -> None:\n" +
                "    ok = isinstance(cmd, Group) and cmd.chain\n" +
                "    cmd.chain\n");
            Assert.IsTrue(HasAttrNotFound(r, "chain"), Dump(r));
        }
    }
}
