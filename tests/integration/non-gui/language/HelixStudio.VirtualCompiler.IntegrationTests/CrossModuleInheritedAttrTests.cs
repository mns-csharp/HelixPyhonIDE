using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 476 (django stress-test): a mixin's self.&lt;attr&gt; is provided by a base the concrete
    /// class imports from ANOTHER module — Django's TupleLookupMixin reads self.lhs, and
    /// `class TupleExact(TupleLookupMixin, Exact)` inherits lhs from Exact -> Lookup, both in
    /// db.models.lookups. The hierarchy walk now follows the import cross-module (via WorkspaceIndex),
    /// so the mixin must not flag PY6001. A member no class in the whole hierarchy provides still flags.
    /// </summary>
    [TestClass]
    public class CrossModuleInheritedAttrTests
    {
        private sealed class Src { public string Rel; public string Fqn; public string Text; }

        private static IReadOnlyList<CompileDiagnostic> Compile(params Src[] files)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_xmod_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var wfiles = new List<WorkspaceFile>();
            foreach (Src f in files)
            {
                string full = Path.Combine(root, f.Rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, f.Text);
                wfiles.Add(new WorkspaceFile(full, f.Fqn));
            }
            try { return VirtualCompiler.CompileSolution(wfiles).SelectMany(r => r.Result.Diagnostics).ToList(); }
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
        public void MixinAttributeFromABaseImportedFromAnotherModule_NotFlagged()
        {
            var diags = Compile(
                new Src { Rel = "lookups.py", Fqn = "lookups", Text =
                    "class Lookup:\n" +
                    "    def __init__(self, lhs: object, rhs: object) -> None:\n" +
                    "        self.lhs, self.rhs = lhs, rhs\n" +
                    "class BuiltinLookup(Lookup):\n" +
                    "    pass\n" +
                    "class Exact(BuiltinLookup):\n" +
                    "    pass\n" },
                new Src { Rel = "tuple_lookups.py", Fqn = "tuple_lookups", Text =
                    "from lookups import Exact\n" +
                    "class TupleLookupMixin:\n" +
                    "    def process(self) -> object:\n" +
                    "        return self.lhs\n" +
                    "class TupleExact(TupleLookupMixin, Exact):\n" +
                    "    pass\n" });

            Assert.IsFalse(HasAttrNotFound(diags, "lhs"), Dump(diags));
        }

        [TestMethod]
        public void AttributeNoHierarchyProvides_IsStillFlagged()
        {
            var diags = Compile(
                new Src { Rel = "lookups.py", Fqn = "lookups", Text =
                    "class Lookup:\n" +
                    "    def __init__(self, lhs: object) -> None:\n" +
                    "        self.lhs = lhs\n" +
                    "class Exact(Lookup):\n" +
                    "    pass\n" },
                new Src { Rel = "tuple_lookups.py", Fqn = "tuple_lookups", Text =
                    "from lookups import Exact\n" +
                    "class TupleLookupMixin:\n" +
                    "    def process(self) -> object:\n" +
                    "        return self.ghost\n" +
                    "class TupleExact(TupleLookupMixin, Exact):\n" +
                    "    pass\n" });

            Assert.IsTrue(HasAttrNotFound(diags, "ghost"), Dump(diags));
        }

        [TestMethod]
        public void RelativeImportedUniqueSiblingBaseProvidesMixinMember()
        {
            var diags = Compile(
                new Src { Rel = "pkg\\base.py", Fqn = "pkg.base", Text =
                    "class Printer:\n" +
                    "    def _print(self, value: object) -> str:\n" +
                    "        return str(value)\n" },
                new Src { Rel = "pkg\\mixin.py", Fqn = "pkg.mixin", Text =
                    "class PrintMixin:\n" +
                    "    def render(self, value: object) -> str:\n" +
                    "        return self._print(value)\n" },
                new Src { Rel = "pkg\\concrete.py", Fqn = "pkg.concrete", Text =
                    "from .base import Printer\n" +
                    "from .mixin import PrintMixin\n" +
                    "class Concrete(PrintMixin, Printer):\n" +
                    "    pass\n" });

            Assert.IsFalse(HasAttrNotFound(diags, "_print"), Dump(diags));
        }

        [TestMethod]
        public void SignatureTransformingDecoratorRemainsGradualAcrossModules()
        {
            var diags = Compile(
                new Src { Rel = "dispatch.py", Fqn = "dispatch", Text =
                    "def singledispatch(fn):\n" +
                    "    return fn\n" +
                    "@singledispatch\n" +
                    "def convert(value):\n" +
                    "    return value\n" },
                new Src { Rel = "consumer.py", Fqn = "consumer", Text =
                    "from dispatch import convert\n" +
                    "convert(1, 2)\n" });

            Assert.IsFalse(diags.Any(d =>
                    d.Code == VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                Dump(diags));
        }
    }
}
