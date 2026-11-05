using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Coverage for Tier-3 lints: unused imports (PY5001), invalid string
    /// escapes (PY1104), and unreachable code after return/raise/break/continue
    /// (PY4015). Ports of CPython's <c>ast_preprocess.c</c> /
    /// <c>string_parser.c</c> warnings plus pyflakes-style F401.
    /// </summary>
    [TestClass]
    public class Tier3LintTests
    {
        // ---------- PY5001 unused-import ----------

        [TestMethod]
        public void UnusedImport_AtModuleScope_Emits_PY5001()
        {
            const string src = "import os\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY5001" && d.Message.Contains("os")));
        }

        [TestMethod]
        public void UsedImport_AtModuleScope_Does_Not_Emit_PY5001()
        {
            const string src = "import os\nprint(os.getcwd())\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY5001"));
        }

        [TestMethod]
        public void UnusedFromImport_Emits_PY5001()
        {
            const string src = "from typing import List\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY5001" && d.Message.Contains("List")));
        }

        [TestMethod]
        public void ImportUsedInNestedFunction_Does_Not_Emit_PY5001()
        {
            // Use lives in a descendant scope — the checker must walk children.
            const string src =
                "from typing import List\n" +
                "def f(xs: List):\n" +
                "    return xs\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY5001"));
        }

        [TestMethod]
        public void StarImport_Does_Not_Emit_PY5001()
        {
            // `from X import *` is unanalyzable; SymbolTableBuilder skips those
            // entries entirely, so there's no symbol to flag.
            const string src = "from os.path import *\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY5001"));
        }

        [TestMethod]
        public void ImportInAllList_Does_Not_Emit_PY5001()
        {
            const string src =
                "from util import foo\n" +
                "__all__ = [\"foo\"]\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY5001"),
                "names re-exported via __all__ must suppress PY5001");
        }

        [TestMethod]
        public void FutureImport_Annotations_Does_Not_Emit_PY5001()
        {
            // `from __future__ import annotations` is a compile-time directive that is
            // never referenced by name — pyflakes/CPython never flag it. It was the
            // dominant PY5001 false positive on both fixtures.
            const string src =
                "from __future__ import annotations\n" +
                "x = 1\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY5001"),
                "__future__ imports must never be flagged unused; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void FutureImport_MultipleFeatures_Does_Not_Emit_PY5001()
        {
            const string src =
                "from __future__ import annotations, division\n" +
                "y = 2\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY5001"),
                "no __future__ feature import may be flagged unused; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void NonFuture_AnnotationsImport_StillFlaggedWhenUnused()
        {
            // Guard: the exemption is scoped to __future__ ONLY. An `annotations` name
            // imported from a real module and left unused must still flag.
            const string src =
                "from mypkg import annotations\n" +
                "z = 3\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY5001" && d.Message.Contains("annotations")),
                "a non-__future__ unused import named 'annotations' must still flag; diagnostics: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void OpaqueAll_Suppresses_ModuleLevel_PY5001()
        {
            // When __all__ has computed members, we can't prove an import isn't
            // re-exported. Conservative: suppress all module-level PY5001.
            const string src =
                "import os\n" +
                "__all__ = list(names())\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY5001"));
        }

        [TestMethod]
        public void UnusedImport_InsideFunction_Emits_PY5001()
        {
            // Scope walk visits children too — function-local unused imports also flag.
            const string src =
                "def f():\n" +
                "    import math\n" +
                "    return 1\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY5001" && d.Message.Contains("math")));
        }

        // ---------- PY1104 invalid-escape ----------

        [TestMethod]
        public void InvalidEscape_InPlainString_Emits_PY1104()
        {
            const string src = "x = \"\\d\"\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY1104"));
        }

        [TestMethod]
        public void RawString_Does_Not_Emit_PY1104()
        {
            const string src = "x = r\"\\d\"\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY1104"));
        }

        [TestMethod]
        public void ValidEscape_Newline_Does_Not_Emit_PY1104()
        {
            const string src = "x = \"\\n\"\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY1104"));
        }

        [TestMethod]
        public void UnicodeEscape_InByteString_Emits_PY1104()
        {
            const string src = "x = b\"\\u0041\"\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY1104"),
                "\\u is invalid in byte strings");
        }

        [TestMethod]
        public void TripleQuoted_InvalidEscape_Emits_PY1104()
        {
            const string src = "x = \"\"\"triple \\q end\"\"\"\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY1104"));
        }

        [TestMethod]
        public void EscapedBackslash_Does_Not_Emit_PY1104()
        {
            // "\\d" is a valid escape (\\ consumes the backslash; d is just a letter).
            const string src = "x = \"\\\\d\"\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY1104"));
        }

        [TestMethod]
        public void ValidHexEscape_Does_Not_Emit_PY1104()
        {
            const string src = "x = \"\\x41\"\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY1104"));
        }

        // ---------- PY4015 unreachable-code ----------

        [TestMethod]
        public void StatementAfterReturn_Emits_PY4015()
        {
            const string src =
                "def f():\n" +
                "    return 1\n" +
                "    x = 2\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4015"));
        }

        [TestMethod]
        public void StatementAfterRaise_Emits_PY4015()
        {
            const string src =
                "def f():\n" +
                "    raise ValueError()\n" +
                "    print(\"dead\")\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4015"));
        }

        [TestMethod]
        public void StatementAfterBreak_Emits_PY4015()
        {
            const string src =
                "for i in range(3):\n" +
                "    break\n" +
                "    print(\"dead\")\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4015"));
        }

        [TestMethod]
        public void StatementAfterContinue_Emits_PY4015()
        {
            const string src =
                "for i in range(3):\n" +
                "    continue\n" +
                "    print(\"dead\")\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4015"));
        }

        [TestMethod]
        public void ReturnInsideIf_Does_Not_Emit_PY4015_For_Code_After_If()
        {
            // Only the unconditional `if` branch returns; the orelse path reaches
            // the subsequent statement. We don't do flow analysis — conservative.
            const string src =
                "def f(c):\n" +
                "    if c:\n" +
                "        return 1\n" +
                "    print(\"reachable\")\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY4015"));
        }

        [TestMethod]
        public void TerminatorAsLastStatement_Does_Not_Emit_PY4015()
        {
            const string src =
                "def f():\n" +
                "    return 1\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY4015"));
        }

        [TestMethod]
        public void TwoDefsInModule_After_Return_InFirstFunc_Only_Flags_Inner()
        {
            // `def g` follows `def f` at module scope — that's fine. The `x = 2`
            // inside f's body following return is the real target.
            const string src =
                "def f():\n" +
                "    return 1\n" +
                "    x = 2\n" +
                "\n" +
                "def g():\n" +
                "    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            var py4015s = r.Diagnostics.Where(d => d.Code == "PY4015").ToList();
            Assert.AreEqual(1, py4015s.Count, "only one unreachable block expected");
        }
    }
}
