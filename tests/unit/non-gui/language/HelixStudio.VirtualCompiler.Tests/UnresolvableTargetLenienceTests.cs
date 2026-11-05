using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 371 (rich stress-test): a type-mismatch is only trustworthy when the EXPECTED type
    /// resolves to a real type. A bare annotation that is an unexpanded alias (rich's StyleType =
    /// str | Style, RenderableType, TextType) or an un-modelled TypeVar (`expected 'T'`) cannot be
    /// judged, so passing a str/instance to it must NOT false-flag PY6003 — while a genuinely
    /// resolvable target (list, str) is still checked.
    /// </summary>
    [TestClass]
    public class UnresolvableTargetLenienceTests
    {
        private static bool HasArgMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void UnresolvableAliasParam_DoesNotFlag()
        {
            // StyleType is not defined/expandable here — an opaque alias, so str-vs-StyleType is unjudgeable.
            var r = VirtualCompiler.CompileSource(
                "def f(style: StyleType) -> None: ...\n" +
                "f('red')\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnresolvableAliasUnionParam_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(style: StyleType | None) -> None: ...\n" +
                "f('red')\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void TypeVarParam_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeVar\n" +
                "T = TypeVar('T')\n" +
                "class User: ...\n" +
                "def ok(value: T) -> None: ...\n" +
                "ok(User())\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        /// <summary>The leniency is targeted: a RESOLVABLE param type still catches a real mismatch.</summary>
        [TestMethod]
        public void ResolvableParam_StillFlagsAMismatch()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(shapes: list) -> None: ...\n" +
                "f('not a list')\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }

        /// <summary>A generic whose ARGUMENT is an alias is still structurally checkable by its container.</summary>
        [TestMethod]
        public void GenericWithAliasArg_StillFlagsAStructuralMismatch()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(items: list[StyleType]) -> None: ...\n" +
                "f('not a list')\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
