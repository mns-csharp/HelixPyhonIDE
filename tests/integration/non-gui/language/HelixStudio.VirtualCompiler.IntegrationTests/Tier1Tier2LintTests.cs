using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Coverage for the cross-module resolution (Tier 1) and four CPython-parity
    /// lints added in the same batch (Tier 2): duplicate def/class (PY3007),
    /// mutable default arg (PY2108), 'is' with literal (PY4013), control flow
    /// in finally (PY4014).
    /// </summary>
    [TestClass]
    public class Tier1Tier2LintTests
    {
        // ---------- Tier 2: PY3007 duplicate def/class ----------

        [TestMethod]
        public void DuplicateFunction_Emits_PY3007()
        {
            const string src = "def foo():\n    pass\n\ndef foo():\n    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY3007"),
                "expected PY3007 for duplicate function definition");
        }

        [TestMethod]
        public void OverloadedFunctions_Do_Not_Emit_PY3007()
        {
            const string src =
                "from typing import overload\n" +
                "@overload\n" +
                "def foo(x: int) -> int: ...\n" +
                "@overload\n" +
                "def foo(x: str) -> str: ...\n" +
                "def foo(x):\n    return x\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY3007"),
                "@overload sequences are legal and must not trigger PY3007");
        }

        [TestMethod]
        public void DuplicateClass_Emits_PY3007()
        {
            const string src = "class C:\n    pass\n\nclass C:\n    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY3007"),
                "expected PY3007 for duplicate class definition");
        }

        [TestMethod]
        public void DuplicateMethodsInSameClass_Emits_PY3007()
        {
            const string src =
                "class C:\n" +
                "    def m(self): pass\n" +
                "    def m(self): pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY3007"));
        }

        [TestMethod]
        public void SameNameInSeparateScopes_Does_Not_Emit_PY3007()
        {
            const string src =
                "def f():\n    pass\n" +
                "class C:\n" +
                "    def f(self): pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY3007"));
        }

        // ---------- Tier 2: PY2108 mutable default arg ----------

        [TestMethod]
        public void ListLiteralDefault_Emits_PY2108()
        {
            const string src = "def f(x=[]):\n    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY2108"));
        }

        [TestMethod]
        public void DictLiteralDefault_Emits_PY2108()
        {
            const string src = "def f(x={}):\n    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY2108"));
        }

        [TestMethod]
        public void ListCallDefault_Emits_PY2108()
        {
            const string src = "def f(x=list()):\n    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY2108"));
        }

        [TestMethod]
        public void NoneDefault_Does_Not_Emit_PY2108()
        {
            const string src = "def f(x=None):\n    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY2108"));
        }

        [TestMethod]
        public void TupleDefault_Does_Not_Emit_PY2108()
        {
            // Tuples are immutable, so they don't suffer from the shared-default bug.
            const string src = "def f(x=()):\n    pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY2108"));
        }

        // ---------- Tier 2: PY4013 'is' with literal ----------

        [TestMethod]
        public void IsWithIntLiteral_Emits_PY4013()
        {
            const string src = "x = 1\nif x is 42: pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4013"));
        }

        [TestMethod]
        public void IsWithStringLiteral_Emits_PY4013()
        {
            const string src = "x = \"a\"\nif x is \"b\": pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4013"));
        }

        [TestMethod]
        public void IsNone_Does_Not_Emit_PY4013()
        {
            const string src = "x = None\nif x is None: pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY4013"));
        }

        [TestMethod]
        public void IsTrue_Does_Not_Emit_PY4013()
        {
            const string src = "x = True\nif x is True: pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY4013"));
        }

        [TestMethod]
        public void IsNotWithLiteral_Emits_PY4013()
        {
            const string src = "x = 1\nif x is not 42: pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4013"));
        }

        // ---------- Tier 2: PY4014 control flow in finally ----------

        [TestMethod]
        public void ReturnInFinally_Emits_PY4014()
        {
            const string src =
                "def f():\n" +
                "    try:\n" +
                "        pass\n" +
                "    finally:\n" +
                "        return 1\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4014"));
        }

        [TestMethod]
        public void BreakInFinally_Emits_PY4014()
        {
            const string src =
                "for i in range(3):\n" +
                "    try:\n" +
                "        pass\n" +
                "    finally:\n" +
                "        break\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4014"));
        }

        [TestMethod]
        public void ContinueInFinally_Emits_PY4014()
        {
            const string src =
                "for i in range(3):\n" +
                "    try:\n" +
                "        pass\n" +
                "    finally:\n" +
                "        continue\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "PY4014"));
        }

        [TestMethod]
        public void BreakInInnerLoopInFinally_Does_Not_Emit_PY4014()
        {
            // break is absorbed by the inner for-loop — doesn't escape the finally.
            const string src =
                "def f():\n" +
                "    try:\n" +
                "        pass\n" +
                "    finally:\n" +
                "        for j in range(3):\n" +
                "            break\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY4014"));
        }

        [TestMethod]
        public void ReturnInNestedFunctionInFinally_Does_Not_Emit_PY4014()
        {
            // return is for the nested def, not the enclosing finally.
            const string src =
                "def f():\n" +
                "    try:\n" +
                "        pass\n" +
                "    finally:\n" +
                "        def inner():\n" +
                "            return 1\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY4014"));
        }

        [TestMethod]
        public void PlainFinally_WithoutControlFlow_Does_Not_Emit_PY4014()
        {
            const string src =
                "def f():\n" +
                "    try:\n" +
                "        pass\n" +
                "    finally:\n" +
                "        pass\n";
            var r = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY4014"));
        }

        // ---------- Tier 1: WorkspaceIndex + CompileSolution ----------

        [TestMethod]
        public void CompileSolution_Resolves_CrossModule_Call_Type_Mismatch()
        {
            // Two files: lib_utils.geometry.total_area(shapes: list) -> float
            // app_cli.main calls total_area("not a list") — must emit PY6003.
            string tmp = Path.Combine(Path.GetTempPath(),
                "helix_vc_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string geom = Path.Combine(tmp, "geometry.py");
                string main = Path.Combine(tmp, "main.py");
                File.WriteAllText(geom,
                    "def total_area(shapes: list) -> float:\n" +
                    "    return 0.0\n");
                File.WriteAllText(main,
                    "from geometry import total_area\n" +
                    "print(total_area(\"not a list\"))\n");

                var files = new[]
                {
                    new WorkspaceFile(geom, "geometry"),
                    new WorkspaceFile(main, "main"),
                };
                var results = VirtualCompiler.CompileSolution(files);
                var allDiags = results.SelectMany(r => r.Result.Diagnostics).ToList();
                Assert.IsTrue(allDiags.Any(d => d.Code == "PY6003"),
                    "expected PY6003 arg-type mismatch when passing str where list is required; "
                    + "got: " + string.Join("; ", allDiags.Select(d => d.Code + ":" + d.Message)));
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        [TestMethod]
        public void CompileSolution_Resolves_CrossModule_Call_Valid_Argument_Stays_Clean()
        {
            string tmp = Path.Combine(Path.GetTempPath(),
                "helix_vc_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string geom = Path.Combine(tmp, "geometry.py");
                string main = Path.Combine(tmp, "main.py");
                File.WriteAllText(geom,
                    "def total_area(shapes: list) -> float:\n" +
                    "    return 0.0\n");
                File.WriteAllText(main,
                    "from geometry import total_area\n" +
                    "total_area([1, 2, 3])\n");

                var files = new[]
                {
                    new WorkspaceFile(geom, "geometry"),
                    new WorkspaceFile(main, "main"),
                };
                var results = VirtualCompiler.CompileSolution(files);
                var mainDiags = results.First(r => r.FilePath == main).Result.Diagnostics;
                Assert.IsFalse(mainDiags.Any(d => d.Code == "PY6003"),
                    "list argument should type-check cleanly against list param");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        [TestMethod]
        public void CompileSolution_Without_WorkspaceIndex_CallsRemain_UnknownRef()
        {
            // Baseline: single-file compile doesn't know the imported symbol —
            // call should not emit PY6003 (but also shouldn't emit spurious errors).
            const string src =
                "from geometry import total_area\n" +
                "print(total_area(\"not a list\"))\n";
            var r = VirtualCompiler.CompileSource(src, "main.py");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "PY6003"),
                "single-file compile can't know the remote sig — no spurious PY6003");
        }

        [TestMethod]
        public void CompileSolution_On_PythonTestSample_Catches_Fixture_Bug()
        {
            // End-to-end: the real fixture tests/fixtures/python/python-test-sample-oop contains
            // `total_area("not a list")` in app_cli/main.py (marked "noqa: PY6003 — intentional").
            // After Tier-1 wiring, compiling the whole workspace must emit PY6003. The fixture
            // silences the line with `# noqa` (so the IDE shows it clean), so this runs on a copy
            // with that one comment removed.
            string fixtureRoot = FixtureCopyWithTheBugUnsilenced();

            var files = new System.Collections.Generic.List<WorkspaceFile>();
            foreach (string dir in Directory.GetDirectories(fixtureRoot))
            {
                string pkgName = Path.GetFileName(dir);
                foreach (string py in Directory.GetFiles(dir, "*.py", SearchOption.AllDirectories))
                {
                    string rel = py.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    string stem = Path.GetFileNameWithoutExtension(rel);
                    string relDir = Path.GetDirectoryName(rel) ?? string.Empty;
                    string dotted = relDir.Replace(Path.DirectorySeparatorChar, '.')
                                          .Replace(Path.AltDirectorySeparatorChar, '.');
                    string fqn;
                    if (stem == "__init__")
                        fqn = string.IsNullOrEmpty(dotted) ? pkgName : pkgName + "." + dotted;
                    else
                        fqn = string.IsNullOrEmpty(dotted) ? pkgName + "." + stem : pkgName + "." + dotted + "." + stem;
                    files.Add(new WorkspaceFile(py, fqn));
                }
            }

            var results = VirtualCompiler.CompileSolution(files);
            var mainResult = results.FirstOrDefault(r =>
                r.FilePath.Replace('\\', '/').EndsWith("/app_cli/main.py"));
            Assert.IsNotNull(mainResult, "app_cli/main.py not found among compiled files");
            Assert.IsTrue(mainResult.Result.Diagnostics.Any(d => d.Code == "PY6003"),
                "expected PY6003 on app_cli/main.py line 24 (total_area(\"not a list\")); got: " +
                string.Join("; ", mainResult.Result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }

        /// <summary>
        /// A %TEMP% copy of the fixture's Python files in which the intentional
        /// <c>total_area("not a list")</c> call has lost its <c>  # noqa: PY6003 …</c> comment.
        /// The fixture keeps the comment so the IDE shows it clean; the fault is reintroduced in a
        /// copy, never in the fixture.
        /// </summary>
        private static string FixtureCopyWithTheBugUnsilenced()
        {
            string fixture = FindFixtureSrc();
            Assert.IsNotNull(fixture, "fixture tests/fixtures/python/python-test-sample-oop not found");
            string copy = Path.Combine(Path.GetTempPath(), "helix_t1t2_" + System.Guid.NewGuid().ToString("N"));
            foreach (string py in Directory.GetFiles(fixture, "*.py", SearchOption.AllDirectories))
            {
                if (py.IndexOf("__pycache__", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string target = Path.Combine(copy, py.Substring(fixture.Length).TrimStart('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(py, target);
            }

            string main = Path.Combine(copy, "app_cli", "main.py");
            string[] lines = File.ReadAllLines(main);
            int changed = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                int noqa = lines[i].IndexOf("  # noqa: PY6003", System.StringComparison.Ordinal);
                if (noqa < 0 || lines[i].IndexOf("total_area(\"not a list\")", System.StringComparison.Ordinal) < 0) continue;
                lines[i] = lines[i].Substring(0, noqa);
                changed++;
            }
            Assert.AreEqual(1, changed, "the fixture's silenced total_area(\"not a list\") line was not found in " + main);
            File.WriteAllLines(main, lines);
            return copy;
        }

        /// <summary>
        /// The package root of the reference Python solution. It used to be
        /// <c>multi-package-workspace/src</c>; the fixture is now <c>python-test-sample-oop</c>, with
        /// its packages (<c>lib_utils</c>, <c>app_cli</c>, …) directly at the root (flat layout).
        /// </summary>
        private static string FindFixtureSrc()
        {
            // Walk up from bin/Debug/net472 until we find the repo root.
            string dir = System.AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "tests", "fixtures", "python", "python-test-sample-oop");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        [TestMethod]
        public void PythonModuleFqn_Derive_LibUtilsGeometry_YieldsCanonical()
        {
            string fixture = FindFixtureSrc();
            Assert.IsNotNull(fixture, "fixture tests/fixtures/python/python-test-sample-oop not found");
            Assert.AreEqual("lib_utils.geometry",
                HelixPythonNamespace.VirtualCompiler.PythonModuleFqn.Derive(
                    Path.Combine(fixture, "lib_utils", "geometry.py")));
        }

        [TestMethod]
        public void PythonModuleFqn_Derive_LibUtilsInit_YieldsPackageRoot()
        {
            string fixture = FindFixtureSrc();
            Assert.IsNotNull(fixture, "fixture tests/fixtures/python/python-test-sample-oop not found");
            Assert.AreEqual("lib_utils",
                HelixPythonNamespace.VirtualCompiler.PythonModuleFqn.Derive(
                    Path.Combine(fixture, "lib_utils", "__init__.py")));
        }

        [TestMethod]
        public void CompileSolution_UsingCanonicalFqn_Catches_Fixture_Bug()
        {
            // Guards against the exact regression this commit fixes:
            // MainForm's DeriveModuleFqn was dropping the package name when each
            // uv workspace member has its own pyproject. PythonModuleFqn.Derive
            // walks __init__.py ancestors so FQNs match Python's import path.
            string fixture = FixtureCopyWithTheBugUnsilenced();

            var files = new System.Collections.Generic.List<WorkspaceFile>();
            foreach (string py in Directory.GetFiles(fixture, "*.py", SearchOption.AllDirectories))
            {
                files.Add(new WorkspaceFile(py,
                    HelixPythonNamespace.VirtualCompiler.PythonModuleFqn.Derive(py)));
            }

            var results = VirtualCompiler.CompileSolution(files);
            var mainResult = results.FirstOrDefault(r =>
                r.FilePath.Replace('\\', '/').EndsWith("/app_cli/main.py"));
            Assert.IsNotNull(mainResult, "app_cli/main.py not in compile results");
            Assert.IsTrue(mainResult.Result.Diagnostics.Any(d => d.Code == "PY6003"),
                "canonical FQN derivation must catch total_area(str) mismatch; got: "
                + string.Join(", ", mainResult.Result.Diagnostics.Select(d => d.Code)));
        }

        [TestMethod]
        public void WorkspaceIndex_RegistersTopLevelDecls()
        {
            var idx = new HelixPythonNamespace.VirtualCompiler.Typing.WorkspaceIndex();
            idx.Register("pkg.mod.foo", HelixPythonNamespace.VirtualCompiler.Typing.AnyRef.Instance);
            Assert.IsNotNull(idx.Resolve("pkg.mod.foo"));
            Assert.IsNull(idx.Resolve("pkg.mod.missing"));
        }
    }
}
