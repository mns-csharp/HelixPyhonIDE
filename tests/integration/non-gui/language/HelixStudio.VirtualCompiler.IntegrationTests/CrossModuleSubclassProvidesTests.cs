using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 478 (django stress-test): a mixin's self.&lt;attr&gt; can be provided by a concrete subclass
    /// that lives in ANOTHER module — Django's FileProxyMixin (core.files.utils) reads self.file, and
    /// File(FileProxyMixin) in core.files.base sets it. The global reverse subclass index finds such
    /// out-of-module subclasses, so the mixin must not flag PY6001. A member no subclass anywhere
    /// provides still flags.
    /// </summary>
    [TestClass]
    public class CrossModuleSubclassProvidesTests
    {
        private sealed class Src { public string Rel; public string Fqn; public string Text; }

        private static IReadOnlyList<CompileDiagnostic> Compile(params Src[] files)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_xsub_" + System.Guid.NewGuid().ToString("N"));
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
        public void MixinAttributeSetByAConcreteSubclassInAnotherModule_NotFlagged()
        {
            var diags = Compile(
                new Src { Rel = "utils.py", Fqn = "utils", Text =
                    "class FileProxyMixin:\n" +
                    "    def readable(self) -> bool:\n" +
                    "        return self.file.readable()\n" },
                new Src { Rel = "base.py", Fqn = "base", Text =
                    "from utils import FileProxyMixin\n" +
                    "class File(FileProxyMixin):\n" +
                    "    def __init__(self, f: object) -> None:\n" +
                    "        self.file = f\n" });

            Assert.IsFalse(HasAttrNotFound(diags, "file"), Dump(diags));
        }

        [TestMethod]
        public void MixinAttributeNoSubclassAnywhereProvides_IsStillFlagged()
        {
            var diags = Compile(
                new Src { Rel = "utils.py", Fqn = "utils", Text =
                    "class FileProxyMixin:\n" +
                    "    def readable(self) -> bool:\n" +
                    "        return self.ghost.readable()\n" },
                new Src { Rel = "base.py", Fqn = "base", Text =
                    "from utils import FileProxyMixin\n" +
                    "class File(FileProxyMixin):\n" +
                    "    def __init__(self, f: object) -> None:\n" +
                    "        self.file = f\n" });

            Assert.IsTrue(HasAttrNotFound(diags, "ghost"), Dump(diags));
        }
    }
}
