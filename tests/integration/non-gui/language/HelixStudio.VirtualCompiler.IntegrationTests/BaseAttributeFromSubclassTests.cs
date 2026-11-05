using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 470 (django stress-test): a base / mixin class routinely reads an attribute that a concrete
    /// SUBCLASS provides — Django's BaseModelAdmin methods read self.admin_site, which
    /// ModelAdmin(BaseModelAdmin).__init__ sets. Checked in isolation the base "has no attribute", but
    /// the subclass supplies it at runtime, so it must not flag PY6001. A member no subclass provides is
    /// still flagged.
    /// </summary>
    [TestClass]
    public class BaseAttributeFromSubclassTests
    {
        private static IReadOnlyList<CompileDiagnostic> Compile(string moduleFqn, string text)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_baseattr_" + System.Guid.NewGuid().ToString("N"));
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
        public void BaseReadsAnAttributeASubclassSetsOnSelf_NotFlagged()
        {
            var diags = Compile("m",
                "class BaseAdmin:\n" +
                "    def title(self) -> str:\n" +
                "        return self.admin_site.name\n" +
                "class ModelAdmin(BaseAdmin):\n" +
                "    def __init__(self, admin_site: str) -> None:\n" +
                "        self.admin_site = admin_site\n");
            Assert.IsFalse(HasAttrNotFound(diags, "admin_site"), Dump(diags));
        }

        [TestMethod]
        public void BaseReadsAnAttributeASubclassDeclares_NotFlagged()
        {
            var diags = Compile("m",
                "class Mixin:\n" +
                "    def go(self) -> object:\n" +
                "        return self.opts\n" +
                "class Concrete(Mixin):\n" +
                "    opts = {}\n");
            Assert.IsFalse(HasAttrNotFound(diags, "opts"), Dump(diags));
        }

        [TestMethod]
        public void AnAttributeNoSubclassProvides_IsStillFlagged()
        {
            var diags = Compile("m",
                "class BaseAdmin:\n" +
                "    def title(self) -> str:\n" +
                "        return self.nonexistent_attr\n" +
                "class ModelAdmin(BaseAdmin):\n" +
                "    def __init__(self, admin_site: str) -> None:\n" +
                "        self.admin_site = admin_site\n");
            Assert.IsTrue(HasAttrNotFound(diags, "nonexistent_attr"), Dump(diags));
        }
    }
}
