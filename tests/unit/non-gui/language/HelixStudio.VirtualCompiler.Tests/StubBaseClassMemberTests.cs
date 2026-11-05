using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 359 (requests stress-test): attribute resolution on a STUB class checked only that
    /// class's own body and reported "has no attribute" without walking its stub base classes.
    /// So a MutableMapping field's .items()/.values() — declared on the Mapping base — false-flagged
    /// PY6001 on every use (self.adapters.items() in requests/sessions.py). The bundled Mapping/
    /// MutableMapping stubs were also missing the standard mapping methods (pop/update/get/...).
    /// </summary>
    [TestClass]
    public class StubBaseClassMemberTests
    {
        private static bool NoAttr(CompileResult r, string member)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound
                                          && d.Message.Contains("'" + member + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Header =
            "from typing import MutableMapping, Any\n" +
            "class S:\n" +
            "    m: MutableMapping[str, Any]\n" +
            "    def go(self) -> None:\n";

        [TestMethod]
        public void InheritedMappingMethods_AreVisibleOnMutableMapping()
        {
            // items/values/keys/get live on the Mapping base; MutableMapping must inherit them.
            var r = VirtualCompiler.CompileSource(Header +
                "        self.m.items()\n" +
                "        self.m.values()\n" +
                "        self.m.keys()\n" +
                "        self.m.get('k')\n");
            Assert.IsFalse(NoAttr(r, "items"), Dump(r));
            Assert.IsFalse(NoAttr(r, "values"), Dump(r));
            Assert.IsFalse(NoAttr(r, "keys"), Dump(r));
            Assert.IsFalse(NoAttr(r, "get"), Dump(r));
        }

        [TestMethod]
        public void MutableMappingOwnMethods_AreDeclared()
        {
            var r = VirtualCompiler.CompileSource(Header +
                "        self.m.pop('k')\n" +
                "        self.m.update({})\n" +
                "        self.m.setdefault('k', 1)\n" +
                "        self.m.clear()\n" +
                "        self.m['a'] = 1\n");
            Assert.IsFalse(NoAttr(r, "pop"), Dump(r));
            Assert.IsFalse(NoAttr(r, "update"), Dump(r));
            Assert.IsFalse(NoAttr(r, "setdefault"), Dump(r));
            Assert.IsFalse(NoAttr(r, "clear"), Dump(r));
        }

        /// <summary>Base-walking must not invent members: a truly absent one still reports.</summary>
        [TestMethod]
        public void AGenuinelyAbsentMappingMethod_StillReports()
        {
            var r = VirtualCompiler.CompileSource(Header + "        self.m.no_such_method()\n");
            Assert.IsTrue(NoAttr(r, "no_such_method"), Dump(r));
        }

        /// <summary>Sequence.append lives on MutableSequence's base chain too — same fix.</summary>
        [TestMethod]
        public void MutableSequence_InheritsSequenceMembers()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import MutableSequence, Any\n" +
                "class S:\n" +
                "    xs: MutableSequence[Any]\n" +
                "    def go(self) -> None:\n" +
                "        self.xs.count(1)\n" +   // on Sequence
                "        self.xs.append(1)\n");  // on MutableSequence
            Assert.IsFalse(NoAttr(r, "count"), Dump(r));
            Assert.IsFalse(NoAttr(r, "append"), Dump(r));
        }
    }
}
