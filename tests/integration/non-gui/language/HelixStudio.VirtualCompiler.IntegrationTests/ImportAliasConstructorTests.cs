using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 458 (fastapi stress-test): a call on a name imported as a class alias
    /// (`from m import Model as ModelAlias; ModelAlias(field=...)`) must be treated as a CONSTRUCTOR,
    /// not an instance call to __call__ — even when a same-named class in another module defines a
    /// __call__ (fastapi: openapi.models.HTTPBase vs security.http.HTTPBase, which shadow each other by
    /// simple name). The bug reported the model's constructor kwargs as "unexpected keyword argument"
    /// against the security class's __call__(request) signature.
    /// </summary>
    [TestClass]
    public class ImportAliasConstructorTests
    {
        private sealed class Src { public string Rel; public string Fqn; public string Text; }

        private static IReadOnlyList<CompileDiagnostic> Compile(params Src[] files)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_impalias_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var wfiles = new List<WorkspaceFile>();
            foreach (Src f in files)
            {
                string full = Path.Combine(root, f.Rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, f.Text);
                wfiles.Add(new WorkspaceFile(full, f.Fqn));
            }
            try
            {
                var results = VirtualCompiler.CompileSolution(wfiles);
                return results.SelectMany(r => r.Result.Diagnostics).ToList();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void ClassAlias_WithSameNamedCallableInAnotherModule_IsConstructedNotCalled()
        {
            var diags = Compile(
                new Src { Rel = "models.py", Fqn = "models", Text =
                    "class Model:\n" +
                    "    def __init__(self, *, scheme: str, description: str | None = None) -> None:\n" +
                    "        self.scheme = scheme\n" },
                new Src { Rel = "security.py", Fqn = "security", Text =
                    "from models import Model as ModelAlias\n" +
                    "\n" +
                    "class Model:\n" +
                    "    def __call__(self, request: str) -> bool:\n" +
                    "        return True\n" +
                    "    def build(self) -> None:\n" +
                    "        self.m = ModelAlias(scheme='basic', description='d')\n" });

            bool hasKwErr = diags.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeUnknownKeywordArg);
            Assert.IsFalse(hasKwErr,
                "the class alias must be constructed, not checked against the shadowing __call__: "
                + string.Join(", ", diags.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void GenuineInstanceCall_StillDispatchesToCall()
        {
            var diags = Compile(
                new Src { Rel = "mod.py", Fqn = "mod", Text =
                    "class Handler:\n" +
                    "    def __call__(self, request: str) -> bool:\n" +
                    "        return True\n" +
                    "def use(h: Handler) -> None:\n" +
                    "    h(request='x', bogus=1)\n" });

            bool hasKwErr = diags.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeUnknownKeywordArg);
            Assert.IsTrue(hasKwErr, "a genuine instance call with an unknown kwarg should still flag");
        }
    }
}
