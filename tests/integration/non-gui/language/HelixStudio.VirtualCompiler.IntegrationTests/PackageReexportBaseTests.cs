using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 484 (django stress-test): a base class imported as `pkg.Cls` but actually defined in a
    /// SUBMODULE and re-exported through the package __init__ (Django imports FileSystemStorage from
    /// django.core.files.storage, defined in ...storage.filesystem) is now followed for attribute
    /// resolution — so a mixin's self.exists / self._save, supplied by such a base, does not flag
    /// PY6001. A genuinely-absent member still flags.
    /// </summary>
    [TestClass]
    public class PackageReexportBaseTests
    {
        private sealed class Src { public string Rel; public string Fqn; public string Text; }

        private static IReadOnlyList<CompileDiagnostic> Compile(params Src[] files)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_reexp_" + System.Guid.NewGuid().ToString("N"));
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
        public void BaseImportedAsPackageDotClass_ButDefinedInSubmodule_ResolvesForAttributes()
        {
            // storage.filesystem.FileSystemStorage defines exists(); it is imported as
            // `storage.FileSystemStorage` (package re-export). The mixin's self.exists must resolve.
            var diags = Compile(
                new Src { Rel = "storage/filesystem.py", Fqn = "storage.filesystem", Text =
                    "class FileSystemStorage:\n" +
                    "    def exists(self, name: str) -> bool:\n" +
                    "        return True\n" },
                new Src { Rel = "staticfiles.py", Fqn = "staticfiles", Text =
                    "from storage import FileSystemStorage\n" +
                    "class StaticFilesStorage(FileSystemStorage):\n" +
                    "    pass\n" +
                    "class HashedFilesMixin:\n" +
                    "    def process(self, name: str) -> bool:\n" +
                    "        return self.exists(name)\n" +
                    "class Manifest(HashedFilesMixin, StaticFilesStorage):\n" +
                    "    pass\n" });

            Assert.IsFalse(HasAttrNotFound(diags, "exists"), Dump(diags));
        }

        [TestMethod]
        public void MemberNoReexportedBaseProvides_IsStillFlagged()
        {
            var diags = Compile(
                new Src { Rel = "storage/filesystem.py", Fqn = "storage.filesystem", Text =
                    "class FileSystemStorage:\n" +
                    "    def exists(self, name: str) -> bool:\n" +
                    "        return True\n" },
                new Src { Rel = "staticfiles.py", Fqn = "staticfiles", Text =
                    "from storage import FileSystemStorage\n" +
                    "class StaticFilesStorage(FileSystemStorage):\n" +
                    "    pass\n" +
                    "class HashedFilesMixin:\n" +
                    "    def process(self, name: str) -> bool:\n" +
                    "        return self.ghost(name)\n" +
                    "class Manifest(HashedFilesMixin, StaticFilesStorage):\n" +
                    "    pass\n" });

            Assert.IsTrue(HasAttrNotFound(diags, "ghost"), Dump(diags));
        }
    }
}
