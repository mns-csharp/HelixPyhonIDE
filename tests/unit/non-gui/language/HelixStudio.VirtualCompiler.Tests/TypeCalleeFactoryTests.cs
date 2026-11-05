using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 362 (requests stress-test): a value annotated `type` is an arbitrary class object.
    /// Calling it — `dict_class: type = OrderedDict; dict_class(data)` — constructs an instance of
    /// an unknown class. The checker instead arity-checked it against the metaclass type's own
    /// 3-arg (name, bases, dict) constructor ("1 given, 3 required") and typed the result `type`,
    /// so a follow-up `.update()` reported "'type' has no attribute 'update'". Both were false
    /// positives on requests/sessions.py:96-97.
    /// </summary>
    [TestClass]
    public class TypeCalleeFactoryTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Src =
            "def merge(data, dict_class: type = dict):\n" +
            "    merged = dict_class(data)\n" +
            "    merged.update({})\n" +
            "    return merged\n";

        [TestMethod]
        public void CallingATypeParam_IsNotArityCheckedAsTheMetaclass()
        {
            var r = VirtualCompiler.CompileSource(Src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch), Dump(r));
        }

        [TestMethod]
        public void ResultOfCallingATypeParam_IsNotTypedAsTheMetaclass()
        {
            var r = VirtualCompiler.CompileSource(Src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound), Dump(r));
        }

        /// <summary>The bare metaclass call type(x) (valid 1-arg form) must not be flagged either.</summary>
        [TestMethod]
        public void BareMetaclassCall_TypeOfX_IsAccepted()
        {
            var r = VirtualCompiler.CompileSource("def f(x):\n    return type(x)\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgCountMismatch), Dump(r));
        }
    }
}
