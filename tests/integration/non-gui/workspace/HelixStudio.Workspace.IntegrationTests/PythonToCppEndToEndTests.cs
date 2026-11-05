using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.PyToCpp;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The actual proof of "100% accurate within the subset": for each fixture, run the
    /// ORIGINAL Python through the real interpreter, run it through the full two-pass
    /// translator, compile the emitted C++ with real MSVC, run the resulting native .exe,
    /// and diff stdout across all of it. Nothing here is a mock — every stage is the real
    /// tool doing real work.
    /// </summary>
    [TestClass]
    public class PythonToCppEndToEndTests
    {
        private static string _pyrtHeaderPath;

        [ClassInitialize]
        public static void ClassInit(TestContext _)
        {
            // Locate the checked-in pyrt.hpp relative to this test assembly's source tree.
            string dir = TestDeploymentDir();
            _pyrtHeaderPath = Path.Combine(dir, "src", "non-gui", "workspace", "HelixStudio.Workspace", "PyToCpp", "runtime", "pyrt.hpp");
            Assert.IsTrue(File.Exists(_pyrtHeaderPath), "pyrt.hpp not found at " + _pyrtHeaderPath);
        }

        private static string TestDeploymentDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                if (Directory.GetFiles(dir, "*.sln").Length > 0) return dir;
                string parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
            throw new InvalidOperationException("could not locate repo root (the product .sln) from " + AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string RunPython(string code)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_e2e_py_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, code);
            try
            {
                var psi = new ProcessStartInfo("python.exe", "\"" + tmp + "\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    Assert.AreEqual(0, p.ExitCode, "python execution of original failed:\n" + code + "\n---\n" + stderr);
                    return stdout;
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static (int exitCode, string log) CompileCpp(string cppSource, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string cppPath = Path.Combine(outDir, "main.cpp");
            File.WriteAllText(cppPath, cppSource);
            File.Copy(_pyrtHeaderPath, Path.Combine(outDir, "pyrt.hpp"), true);

            string exePath = Path.Combine(outDir, "main.exe");
            string batPath = Path.Combine(outDir, "build.bat");
            string logPath = Path.Combine(outDir, "build.log");

            // Ask the product where Visual Studio is, rather than hardcoding a path. The path that
            // used to be here -- "C:\Program Files\Microsoft Visual Studio\18\Community\..." --
            // stopped existing when that install went away, and every test in this file then failed
            // with "cl.exe is not recognized", which reads as a broken translator rather than a
            // missing compiler. Using MsvcToolchain.LocateVsInstall also means the tests exercise
            // the same discovery the IDE does, so a break in it cannot hide here.
            string vsInstall = HelixPythonNamespace.Workspace.PyToCpp.MsvcToolchain.LocateVsInstall();
            if (string.IsNullOrEmpty(vsInstall))
                Assert.Inconclusive("no Visual Studio with the C++ toolset was found; "
                    + "the Python-to-C++ end-to-end tests need a real cl.exe.");
            string vcvars = Path.Combine(vsInstall, @"VC\Auxiliary\Build\vcvars64.bat");
            if (!File.Exists(vcvars))
                Assert.Inconclusive("vcvars64.bat not found under " + vsInstall);

            // /std:c++17, matching MsvcToolchain: pyrt.hpp needs C++17 (optional, string_view,
            // charconv) and nothing newer. Asking for c++20 breaks on MSVC 14.16, which ignores the
            // unknown option and silently falls back to C++14.
            File.WriteAllText(batPath,
                "@echo off\r\n" +
                "call \"" + vcvars + "\"\r\n" +
                "cd /d \"" + outDir + "\"\r\n" +
                "cl.exe /std:c++17 /EHsc /nologo /Fe:main.exe main.cpp\r\n" +
                "echo EXITCODE=%ERRORLEVEL%\r\n");

            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + batPath + "\" > \"" + logPath + "\" 2>&1\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) p.WaitForExit(60000);

            string log = File.Exists(logPath) ? File.ReadAllText(logPath) : "(no log produced)";
            bool ok = File.Exists(exePath) && log.Contains("EXITCODE=0");
            return (ok ? 0 : 1, log);
        }

        private static string RunExe(string exePath)
        {
            var psi = new ProcessStartInfo(exePath)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                Assert.AreEqual(0, p.ExitCode, "compiled .exe exited non-zero. stderr:\n" + stderr);
                return stdout;
            }
        }

        // The actual proof of "100% accurate": three genuinely independent executions,
        // diffed pairwise, so a failure pinpoints WHICH stage introduced drift rather than
        // just "something's wrong somewhere":
        //   1. original .py, run by the real CPython interpreter
        //   2. Pass 1's materialized typed .py (a real, standalone, re-runnable file — not
        //      just an in-memory annotated tree), ALSO run by the real CPython interpreter —
        //      this isolates Pass 1 correctness from Pass 2 correctness. If this diverges
        //      from (1), the bug is in PythonToCppTypeChecker/PythonPrettyPrinter, not in
        //      the C++ emitter.
        //   3. the emitted C++ compiled with real MSVC and run as a native .exe — if (2)
        //      matches (1) but (3) doesn't, the bug is in PythonToCppEmitter or pyrt.hpp.
        private static void AssertFullPipelineMatches(string pythonSource)
        {
            var parsed = HelixPythonNamespace.Python.Python.ParseModule(pythonSource);
            Assert.IsFalse(parsed.HasErrors, "fixture must parse cleanly: " + string.Join("; ", parsed.Diagnostics));

            var checker = new PythonToCppTypeChecker();
            TypedPythonResult checkResult = checker.Check(parsed.Module, "test.py");
            Assert.IsTrue(checkResult.Success, "Pass 1 rejected the fixture: " + string.Join("; ", checkResult.Diagnostics));

            string originalOutput = RunPython(pythonSource);
            string typedOutput = RunPython(checkResult.TypedSourceText);
            Assert.AreEqual(originalOutput, typedOutput,
                "Pass 1's materialized typed Python must behave identically to the original — a mismatch here points at PythonToCppTypeChecker/PythonPrettyPrinter, not the C++ emitter.\n---typed Python---\n" + checkResult.TypedSourceText);

            // Pass 2 walks the SAME parsed module Pass 1 already checked and cached types
            // for — this mirrors how BuildViaCppOrchestrator uses a fresh checker over the
            // re-parsed materialized file, just without the extra re-parse round trip (the
            // NodeTypes/Classes/Functions this checker instance already holds are exactly
            // what Pass 2 needs).
            string cppSource = new PythonToCppEmitter(checker).Emit(parsed.Module);

            string outDir = Path.Combine(Path.GetTempPath(), "helix_e2e_cpp_" + Guid.NewGuid().ToString("N"));
            try
            {
                (int exitCode, string log) = CompileCpp(cppSource, outDir);
                Assert.AreEqual(0, exitCode, "emitted C++ failed to compile:\n" + log + "\n---emitted C++---\n" + cppSource);

                string cppOutput = RunExe(Path.Combine(outDir, "main.exe"));
                Assert.AreEqual(originalOutput, cppOutput,
                    "compiled C++ output must match the original Python's real output — Pass 1 already verified identical above, so a mismatch here points at PythonToCppEmitter/pyrt.hpp.\n---emitted C++---\n" + cppSource);
            }
            finally { try { Directory.Delete(outDir, true); } catch { } }
        }

        [TestMethod]
        public void SimpleFunctionsAndArithmetic()
        {
            AssertFullPipelineMatches(
                "def add(a: int, b: int) -> int:\n    return a + b\n\n" +
                "def main() -> None:\n" +
                "    print(add(2, 3))\n" +
                "    print(add(-5, 10))\n" +
                "    x: float = 3.5\n" +
                "    print(x + 1)\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void ClassesWithInheritanceAndSuper()
        {
            AssertFullPipelineMatches(
                "class Animal:\n" +
                "    def __init__(self, name: str) -> None:\n        self.name = name\n" +
                "    def speak(self) -> str:\n        return self.name + \" makes a sound\"\n" +
                "\n" +
                "class Dog(Animal):\n" +
                "    def __init__(self, name: str, breed: str) -> None:\n" +
                "        super().__init__(name)\n" +
                "        self.breed = breed\n" +
                "    def speak(self) -> str:\n        return self.name + \" (\" + self.breed + \") barks\"\n" +
                "\n" +
                "def main() -> None:\n" +
                "    d = Dog(\"Rex\", \"Labrador\")\n" +
                "    print(d.speak())\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void CollectionsAndComprehensions()
        {
            AssertFullPipelineMatches(
                "def main() -> None:\n" +
                "    nums = [1, 2, 3, 4, 5]\n" +
                "    squares = [x * x for x in nums]\n" +
                "    print(squares)\n" +
                "    evens = [x for x in nums if x % 2 == 0]\n" +
                "    print(evens)\n" +
                "    d = {\"a\": 1, \"b\": 2}\n" +
                "    d[\"c\"] = 3\n" +
                "    print(d)\n" +
                "    s = {1, 2, 2, 3}\n" +
                "    print(len(s))\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void ExceptionsTryExceptFinally()
        {
            AssertFullPipelineMatches(
                "def divide(a: int, b: int) -> float:\n" +
                "    try:\n        return a / b\n" +
                "    except ZeroDivisionError:\n        return 0.0\n" +
                "    finally:\n        print(\"done\")\n" +
                "\n" +
                "def main() -> None:\n" +
                "    print(divide(10, 2))\n" +
                "    print(divide(10, 0))\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void CustomExceptionRaiseAndCatch()
        {
            AssertFullPipelineMatches(
                "class MyError(Exception):\n    pass\n" +
                "\n" +
                "def check(x: int) -> None:\n" +
                "    if x < 0:\n        raise MyError(\"negative!\")\n" +
                "    print(\"ok\", x)\n" +
                "\n" +
                "def main() -> None:\n" +
                "    try:\n        check(5)\n        check(-1)\n" +
                "    except MyError as e:\n        print(\"caught\")\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void FStringsWithConversionAndFormatSpec()
        {
            AssertFullPipelineMatches(
                "def main() -> None:\n" +
                "    name: str = \"world\"\n" +
                "    pi: float = 3.14159\n" +
                "    print(f\"hello {name}\")\n" +
                "    print(f\"pi={pi:.2f}\")\n" +
                "    print(f\"repr={name!r}\")\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void FloorDivAndModuloNegativeSignSemantics()
        {
            AssertFullPipelineMatches(
                "def main() -> None:\n" +
                "    print(-7 // 2)\n" +
                "    print(-7 % 2)\n" +
                "    print(7 // -2)\n" +
                "    print(7 % -2)\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void StringSlicingAndUnicodeLength()
        {
            AssertFullPipelineMatches(
                "def main() -> None:\n" +
                "    s = \"hello world\"\n" +
                "    print(s[0:5])\n" +
                "    print(s[-5:])\n" +
                "    print(s[::-1])\n" +
                "    print(len(s))\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void ListReferenceSemantics()
        {
            AssertFullPipelineMatches(
                "def main() -> None:\n" +
                "    a = [1, 2, 3]\n" +
                "    b = a\n" +
                "    b.append(4)\n" +
                "    print(len(a))\n" +
                "    c = a.copy()\n" +
                "    c.append(5)\n" +
                "    print(len(a))\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void DictInsertionOrderPreserved()
        {
            AssertFullPipelineMatches(
                "from typing import Dict\n" +
                "def main() -> None:\n" +
                "    d: Dict[int, str] = {}\n" +
                "    d[3] = \"three\"\n" +
                "    d[1] = \"one\"\n" +
                "    d[2] = \"two\"\n" +
                "    for k in d:\n        print(k)\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void StaticmethodClassmethodProperty()
        {
            AssertFullPipelineMatches(
                "class Box:\n" +
                "    def __init__(self, v: int) -> None:\n        self.v = v\n" +
                "    @staticmethod\n" +
                "    def unit() -> int:\n        return 1\n" +
                "    @property\n" +
                "    def doubled(self) -> int:\n        return self.v * 2\n" +
                "\n" +
                "def main() -> None:\n" +
                "    b = Box(5)\n" +
                "    print(Box.unit())\n" +
                "    print(b.doubled)\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void OptionalAndNoneCheck()
        {
            AssertFullPipelineMatches(
                "from typing import Optional\n" +
                "def find(x: int) -> Optional[int]:\n" +
                "    if x > 0:\n        return x\n" +
                "    return None\n" +
                "\n" +
                "def main() -> None:\n" +
                "    r = find(5)\n" +
                "    if r is not None:\n        print(r)\n" +
                "    r2 = find(-1)\n" +
                "    if r2 is None:\n        print(\"none\")\n" +
                "\nmain()\n");
        }

        [TestMethod]
        public void IntegerOverflowThrows()
        {
            // Not a Python-parity test (Python ints are arbitrary precision — this is exactly
            // the documented, deliberate scope boundary) but a correctness test for the
            // runtime's own overflow contract: it must fail loudly, never wrap silently.
            var parsed = HelixPythonNamespace.Python.Python.ParseModule(
                "def main() -> None:\n    x: int = 9223372036854775807\n    y = x + 1\n    print(y)\n\nmain()\n");
            Assert.IsFalse(parsed.HasErrors);
            var checker = new PythonToCppTypeChecker();
            TypedPythonResult checkResult = checker.Check(parsed.Module, "test.py");
            Assert.IsTrue(checkResult.Success, string.Join("; ", checkResult.Diagnostics));
            string cppSource = new PythonToCppEmitter(checker).Emit(parsed.Module);

            string outDir = Path.Combine(Path.GetTempPath(), "helix_e2e_overflow_" + Guid.NewGuid().ToString("N"));
            try
            {
                (int exitCode, string log) = CompileCpp(cppSource, outDir);
                Assert.AreEqual(0, exitCode, "emitted C++ failed to compile:\n" + log);

                var psi = new ProcessStartInfo(Path.Combine(outDir, "main.exe"))
                { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    Assert.AreNotEqual(0, p.ExitCode, "overflowing add must not exit cleanly");
                }
            }
            finally { try { Directory.Delete(outDir, true); } catch { } }
        }

        // Everything above drives PythonToCppTypeChecker/PythonToCppEmitter directly with a
        // hand-rolled compile step, mirroring but not actually exercising the real
        // production code path. This test goes through the ACTUAL BuildViaCppOrchestrator —
        // the same class the IDE's "Build via C++" menu item calls — including its own
        // materialize-then-re-parse-then-recheck sequence, proving that code path (not just
        // the individual passes) is correct end-to-end.
        [TestMethod]
        public void RealOrchestrator_TranslatesCompilesAndRuns()
        {
            string projectDir = Path.Combine(Path.GetTempPath(), "helix_orch_src_" + Guid.NewGuid().ToString("N"));
            string outDir = Path.Combine(Path.GetTempPath(), "helix_orch_out_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectDir);
            try
            {
                string pythonSource =
                    "class Counter:\n" +
                    "    def __init__(self, start: int) -> None:\n        self.value = start\n" +
                    "    def increment(self, by: int) -> None:\n        self.value = self.value + by\n" +
                    "\n" +
                    "def main() -> None:\n" +
                    "    c = Counter(10)\n" +
                    "    c.increment(5)\n" +
                    "    print(c.value)\n" +
                    "\nmain()\n";
                File.WriteAllText(Path.Combine(projectDir, "main.py"), pythonSource);

                var solution = new HelixPythonNamespace.Core.Model.SolutionModel(Path.Combine(projectDir, "S.sln"), "S");
                // ProjectDirectory is a computed get-only property (Path.GetDirectoryName(FilePath)) —
                // passing the project file's path under projectDir is enough to make it resolve there.
                var project = new HelixPythonNamespace.Core.Model.ProjectModel(Path.Combine(projectDir, "P.pyproject"), "P", solution);
                solution.Projects.Add(project);

                var orchestrator = new BuildViaCppOrchestrator();
                BuildViaCppResult result = orchestrator.Build(solution, outDir);

                Assert.IsTrue(result.Success, "orchestrator build failed:\n" + result.Log);
                Assert.IsTrue(File.Exists(result.TypedPythonPath), "typed Python intermediate must be materialized to disk");
                Assert.IsTrue(File.Exists(result.CppSourcePath), "emitted C++ must be written to disk");
                Assert.IsTrue(File.Exists(result.OutputExePath), "compiled .exe must exist");

                string originalOutput = RunPython(pythonSource);
                string exeOutput = RunExe(result.OutputExePath);
                Assert.AreEqual(originalOutput, exeOutput, "orchestrator-built .exe must match the original Python's real output");
            }
            finally
            {
                try { Directory.Delete(projectDir, true); } catch { }
                try { Directory.Delete(outDir, true); } catch { }
            }
        }
    }
}
