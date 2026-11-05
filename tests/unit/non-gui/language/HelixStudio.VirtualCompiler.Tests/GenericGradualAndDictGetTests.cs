using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class GenericGradualAndDictGetTests
    {
        [TestMethod]
        public void NestedUnresolvedTypeVarIsNotJudgedAsConcreteMismatch()
        {
            const string source =
                "from typing import Generic, Iterable, TypeVar\n" +
                "T = TypeVar('T')\n" +
                "class Decorator(Generic[T]):\n" +
                "    pass\n" +
                "class ValidatorInfo:\n" +
                "    pass\n" +
                "def consume(values: Iterable[Decorator[ValidatorInfo]]) -> None: ...\n" +
                "def forward(values: list[Decorator[T]]) -> None:\n" +
                "    consume(values)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "generic.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6003"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void DictGetPreservesLiteralValueUnionAcrossNoneGuard()
        {
            const string source =
                "from typing import Literal\n" +
                "Mode = Literal['positional_only', 'keyword_only', 'uniform']\n" +
                "def consume(mode: Mode) -> None: ...\n" +
                "def build(kind: int) -> None:\n" +
                "    lookup: dict[int, Literal['positional_only', 'keyword_only']] = {\n" +
                "        1: 'positional_only', 2: 'keyword_only'}\n" +
                "    mode = lookup.get(kind)\n" +
                "    if mode is None:\n" +
                "        mode = 'uniform'\n" +
                "    consume(mode)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "modes.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6003"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }
    }
}
