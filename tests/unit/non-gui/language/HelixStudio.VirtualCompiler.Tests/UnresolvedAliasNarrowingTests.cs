using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class UnresolvedAliasNarrowingTests
    {
        [TestMethod]
        public void IsinstanceDoesNotReplaceOpaqueAliasWithTestedUnion()
        {
            const string source =
                "from typing import Any, TypeAlias\n" +
                "ColorTuple: TypeAlias = tuple[int, int, int]\n" +
                "ColorType: TypeAlias = ColorTuple | str\n" +
                "def parse_tuple(value: tuple[Any, ...]) -> None: ...\n" +
                "def consume(value: ColorType) -> None:\n" +
                "    if isinstance(value, (tuple, list)):\n" +
                "        parse_tuple(value)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "color.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY6003"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }
    }
}
