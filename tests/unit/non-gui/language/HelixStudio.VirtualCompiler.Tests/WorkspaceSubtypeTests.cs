using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 401 (click stress-test): the stub-only SubtypeChecker cannot see WORKSPACE class
    /// inheritance, so returning/passing a subclass where its base is expected was false-flagged
    /// (click types.py: FuncParamType returned as ParamType[Any]). A workspace-subtype guard now
    /// walks the source class's base chain. Unrelated classes still mismatch.
    /// </summary>
    [TestClass]
    public class WorkspaceSubtypeTests
    {
        private static bool HasMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch
                                          || d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch
                                          || d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Hierarchy =
            "class Base: ...\n" +
            "class Derived(Base): ...\n" +
            "class GrandChild(Derived): ...\n";

        [TestMethod]
        public void ReturningSubclass_AgainstBase_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(Hierarchy +
                "def make() -> Base:\n" +
                "    return Derived()\n");
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        [TestMethod]
        public void PassingSubclassArgument_AgainstBase_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(Hierarchy +
                "def take(b: Base) -> None: ...\n" +
                "def f() -> None:\n" +
                "    take(GrandChild())\n");   // transitive: GrandChild <: Derived <: Base
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        [TestMethod]
        public void SubclassAgainstGenericBase_DoesNotFlag()
        {
            // The click shape: FuncParamType -> ParamType[Any].
            var r = VirtualCompiler.CompileSource(
                "from typing import Any\n" +
                "class ParamType: ...\n" +
                "class FuncParamType(ParamType): ...\n" +
                "def make() -> 'ParamType[Any]':\n" +
                "    return FuncParamType()\n");
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        [TestMethod]
        public void SubclassInUnionBase_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(Hierarchy +
                "from typing import Optional\n" +
                "def make() -> Optional[Base]:\n" +
                "    return Derived()\n");
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnrelatedClass_AgainstBase_StillFlags()
        {
            var r = VirtualCompiler.CompileSource(Hierarchy +
                "class Other: ...\n" +
                "def make() -> Base:\n" +
                "    return Other()\n");
            Assert.IsTrue(HasMismatch(r), Dump(r));
        }
    }
}
