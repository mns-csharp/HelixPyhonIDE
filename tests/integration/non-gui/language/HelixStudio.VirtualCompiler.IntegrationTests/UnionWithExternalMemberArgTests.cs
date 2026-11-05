using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.IntegrationTests
{
    /// <summary>
    /// Iter 464 (fastapi stress-test): an argument whose type is a UNION containing an external /
    /// unresolvable member cannot be proven incompatible, so it must not flag PY6003 — fastapi passes
    /// `Mapping[str, Any] | QueryParams | Headers` (both starlette Mappings we can't see) where a
    /// `Mapping[str, Any]` is expected. A union whose members ALL resolve is still judged, so a
    /// genuine mismatch (`str | None` vs `str`) is still caught.
    /// </summary>
    [TestClass]
    public class UnionWithExternalMemberArgTests
    {
        private static IReadOnlyList<CompileDiagnostic> Compile(string moduleFqn, string text)
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_unionext_" + System.Guid.NewGuid().ToString("N"));
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
        public void UnionWithAnUnresolvableMember_IsNotFlagged()
        {
            // QueryParams / Headers come from an unresolved external module — we can't prove they are
            // Mappings, so the union arg must stay silent rather than false-flag.
            var diags = Compile("m",
                "from typing import Any, Mapping\n" +
                "from starlette.datastructures import QueryParams, Headers\n" +
                "def take(values: Mapping[str, Any]) -> None:\n" +
                "    pass\n" +
                "def use(received: Mapping[str, Any] | QueryParams | Headers) -> None:\n" +
                "    take(received)\n");
            Assert.IsFalse(HasArgMismatch(diags), Dump(diags));
        }

        [TestMethod]
        public void UnionWhoseMembersAllResolve_IsStillJudged()
        {
            // str | None vs str: None genuinely fails and both members resolve, so it must still flag.
            var diags = Compile("m",
                "def take(x: str) -> None:\n" +
                "    pass\n" +
                "def use(s: str | None) -> None:\n" +
                "    take(s)\n");
            Assert.IsTrue(HasArgMismatch(diags), Dump(diags));
        }
    }
}
