using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 446 (pydantic stress-test): calling an INSTANCE (`self()`, a variable, a parameter)
    /// dispatches to __call__, not the constructor. The callee is the class object only when it is a
    /// known class name. `self()` on a class with `def __call__(self)` was mis-checked against __init__
    /// arity (pydantic _PydanticWeakRef). Constructing the class directly is still arity-checked.
    /// </summary>
    [TestClass]
    public class CallableInstanceTests
    {
        private static bool HasArity(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgCountMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void SelfCall_DispatchesToDunderCall_NotConstructor()
        {
            var r = VirtualCompiler.CompileSource(
                "class WeakRef:\n" +
                "    def __init__(self, target: object) -> None:\n" +
                "        self._t = target\n" +
                "    def __call__(self) -> object:\n" +
                "        return self._t\n" +
                "    def deref(self) -> object:\n" +
                "        return self()\n");
            Assert.IsFalse(HasArity(r), Dump(r));
        }

        [TestMethod]
        public void VariableCall_DispatchesToDunderCall()
        {
            var r = VirtualCompiler.CompileSource(
                "class WeakRef:\n" +
                "    def __init__(self, target: object) -> None: ...\n" +
                "    def __call__(self) -> object: ...\n" +
                "def deref(v: WeakRef) -> object:\n" +
                "    return v()\n");
            Assert.IsFalse(HasArity(r), Dump(r));
        }

        [TestMethod]
        public void ConstructingTheClass_IsStillArityChecked()
        {
            var r = VirtualCompiler.CompileSource(
                "class WeakRef:\n" +
                "    def __init__(self, target: object) -> None: ...\n" +
                "    def __call__(self) -> object: ...\n" +
                "def make() -> WeakRef:\n" +
                "    return WeakRef()  # missing required 'target'\n");
            Assert.IsTrue(HasArity(r), Dump(r));
        }
    }
}
