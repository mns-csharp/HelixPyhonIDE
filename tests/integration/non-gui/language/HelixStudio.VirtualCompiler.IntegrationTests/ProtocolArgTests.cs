using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 466 (fastapi stress-test): a Protocol is a STRUCTURAL type — any class with the right
    /// members satisfies it, which the nominal stub-only checker cannot verify. So passing a concrete
    /// class where a Protocol parameter is expected must not flag PY6003 (fastapi routing.py: APIRoute
    /// passed to a `_RouteWithPath(Protocol)` parameter). A concrete (non-Protocol) class target is
    /// still judged, so a genuine mismatch is still caught.
    /// </summary>
    [TestClass]
    public class ProtocolArgTests
    {
        private static IReadOnlyList<CompileDiagnostic> Compile(string moduleFqn, string text)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_proto_" + System.Guid.NewGuid().ToString("N"));
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

        private static bool HasArgMismatch(IReadOnlyList<CompileDiagnostic> d)
        {
            return d.Any(x => x.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(IReadOnlyList<CompileDiagnostic> d)
        {
            return "diagnostics: " + string.Join(", ", d.Select(x => x.Code + " " + x.Message));
        }

        [TestMethod]
        public void ConcreteClass_ToAProtocolParameter_IsNotFlagged()
        {
            var diags = Compile("m",
                "from typing import Protocol\n" +
                "class HasPath(Protocol):\n" +
                "    path: str\n" +
                "class Route:\n" +
                "    path: str = ''\n" +
                "def path_for(r: HasPath) -> str:\n" +
                "    return r.path\n" +
                "def use(route: Route) -> str:\n" +
                "    return path_for(route)\n");
            Assert.IsFalse(HasArgMismatch(diags), Dump(diags));
        }

        [TestMethod]
        public void AGenuineConcreteMismatch_IsStillFlagged()
        {
            // The target here is a concrete class, not a Protocol, so an unrelated arg must still flag.
            var diags = Compile("m",
                "class A:\n" +
                "    pass\n" +
                "def take(a: A) -> None:\n" +
                "    pass\n" +
                "def use() -> None:\n" +
                "    take('not an A')\n");
            Assert.IsTrue(HasArgMismatch(diags), Dump(diags));
        }
    }
}
