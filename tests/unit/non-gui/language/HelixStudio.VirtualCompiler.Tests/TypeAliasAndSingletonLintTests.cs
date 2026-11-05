using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class TypeAliasAndSingletonLintTests
    {
        [TestMethod]
        public void ImportsUsedInsideStringTypeAliasAreNotUnused()
        {
            const string source =
                "from functools import partial, partialmethod\n" +
                "from typing import Any, TypeAlias\n" +
                "_Partial: TypeAlias = 'partial[Any] | partialmethod[Any]'\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "serializers.py");

            Assert.IsFalse(result.Diagnostics.Any(
                d => d.Code == "PY5001" &&
                    (d.Message.Contains("'partial'") || d.Message.Contains("'partialmethod'"))),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void EllipsisIdentityComparisonDoesNotWarn()
        {
            const string source =
                "def is_missing(value: object) -> bool:\n" +
                "    return value is ...\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "sentinel.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "PY4013"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void ImportUsedInStringCastTypeIsNotUnused()
        {
            const string source =
                "from typing import TYPE_CHECKING, cast\n" +
                "if TYPE_CHECKING:\n" +
                "    from package import CoreMetadata\n" +
                "metadata = cast('CoreMetadata', {})\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "metadata.py");

            Assert.IsFalse(result.Diagnostics.Any(
                d => d.Code == "PY5001" && d.Message.Contains("'CoreMetadata'")),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void NonSingletonStringIdentityComparisonStillWarns()
        {
            const string source =
                "def is_mode(value: object) -> bool:\n" +
                "    return value is 'mode'\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "sentinel.py");

            Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "PY4013"),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }
    }
}
