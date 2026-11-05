using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 472 (django stress-test): a MIXIN reads self.&lt;attr&gt; that is supplied not by the mixin
    /// or its subclass directly, but by the SIBLING base the concrete class combines it with — Django's
    /// FieldGetDbPrepValueMixin reads self.lhs, and `class IntegerFieldExact(FieldGetDbPrepValueMixin,
    /// Lookup)` gets lhs from Lookup.__init__ (self.lhs, self.rhs = ...). Walking the concrete class's
    /// whole workspace hierarchy proves the attribute is present, so the mixin must not flag PY6001.
    /// </summary>
    [TestClass]
    public class MixinAttributeFromSiblingBaseTests
    {
        private static IReadOnlyList<CompileDiagnostic> Compile(string moduleFqn, string text)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_mixin_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string full = Path.Combine(root, moduleFqn + ".py");
            File.WriteAllText(full, text);
            try
            {
                var results = VirtualCompiler.CompileSolution(new[] { new WorkspaceFile(full, moduleFqn) });
                return results.SelectMany(r => r.Result.Diagnostics).ToList();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static bool HasAttrNotFound(IReadOnlyList<CompileDiagnostic> d, string attr)
        {
            return d.Any(x => x.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound
                              && x.Message.Contains("'" + attr + "'"));
        }

        private static string Dump(IReadOnlyList<CompileDiagnostic> d)
        {
            return "diagnostics: " + string.Join(", ", d.Select(x => x.Code + " " + x.Message));
        }

        [TestMethod]
        public void MixinReadsAttributeProvidedBySiblingBaseOfConcreteClass_NotFlagged()
        {
            var diags = Compile("m",
                "class Lookup:\n" +
                "    def __init__(self, lhs: object, rhs: object) -> None:\n" +
                "        self.lhs, self.rhs = lhs, rhs\n" +
                "class FieldGetDbPrepValueMixin:\n" +
                "    def process(self) -> object:\n" +
                "        return self.lhs\n" +
                "class IntegerFieldExact(FieldGetDbPrepValueMixin, Lookup):\n" +
                "    pass\n");
            Assert.IsFalse(HasAttrNotFound(diags, "lhs"), Dump(diags));
        }

        [TestMethod]
        public void MixinAttributeNoConcreteClassEverProvides_IsStillFlagged()
        {
            // No concrete subclass's hierarchy supplies 'ghost', so the mixin access is a genuine miss.
            var diags = Compile("m",
                "class Lookup:\n" +
                "    def __init__(self, lhs: object) -> None:\n" +
                "        self.lhs = lhs\n" +
                "class Mixin:\n" +
                "    def process(self) -> object:\n" +
                "        return self.ghost\n" +
                "class Concrete(Mixin, Lookup):\n" +
                "    pass\n");
            Assert.IsTrue(HasAttrNotFound(diags, "ghost"), Dump(diags));
        }
    }
}
