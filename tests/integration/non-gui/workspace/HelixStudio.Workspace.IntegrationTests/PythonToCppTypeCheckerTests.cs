using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.PyToCpp;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Pass 1 (strict type checker + typed-Python rewriter) must: accept every program in
    /// the documented "rich static subset" and re-emit typed Python that behaves IDENTICALLY
    /// to the original (verified by real python.exe execution, not just re-parsing), and
    /// reject — with the right diagnostic code, never silently — everything explicitly out
    /// of scope per PythonToCppTypeChecker's class comment.
    /// </summary>
    [TestClass]
    public class PythonToCppTypeCheckerTests
    {
        private static TypedPythonResult CheckSource(string source)
        {
            var parsed = HelixPythonNamespace.Python.Python.ParseModule(source);
            Assert.IsFalse(parsed.HasErrors, "fixture must parse cleanly: " + string.Join("; ", parsed.Diagnostics));
            return new PythonToCppTypeChecker().Check(parsed.Module, "test.py");
        }

        private static string RunPython(string code)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_tc_" + System.Guid.NewGuid().ToString("N") + ".py");
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
                    Assert.AreEqual(0, p.ExitCode, "python execution failed:\n" + code + "\n---\n" + stderr);
                    return stdout;
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static void AssertAcceptedAndBehaviorPreserved(string source)
        {
            TypedPythonResult r = CheckSource(source);
            Assert.IsTrue(r.Success, "expected acceptance but got: " + string.Join("; ", r.Diagnostics));
            Assert.IsFalse(string.IsNullOrEmpty(r.TypedSourceText));
            string originalOutput = RunPython(source);
            string typedOutput = RunPython(r.TypedSourceText);
            Assert.AreEqual(originalOutput, typedOutput, "Pass 1 output must behave identically to the original:\n" + r.TypedSourceText);
        }

        private static void AssertRejectedWithCode(string source, string expectedCode)
        {
            TypedPythonResult r = CheckSource(source);
            Assert.IsFalse(r.Success, "expected rejection but Pass 1 accepted it");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == expectedCode),
                "expected diagnostic " + expectedCode + " but got: " + string.Join("; ", r.Diagnostics));
        }

        // ---------------------------- acceptance ----------------------------

        [TestMethod]
        public void Accepts_SimpleFunctionWithAnnotations()
        {
            AssertAcceptedAndBehaviorPreserved(
                "def add(a: int, b: int) -> int:\n    return a + b\n\ndef main() -> None:\n    print(add(2, 3))\n\nmain()\n");
        }

        [TestMethod]
        public void Accepts_ClassWithInheritanceAndMethods()
        {
            AssertAcceptedAndBehaviorPreserved(
                "class Animal:\n" +
                "    def __init__(self, name: str) -> None:\n        self.name = name\n" +
                "    def speak(self) -> str:\n        return self.name + \" makes a sound\"\n" +
                "\n" +
                "class Dog(Animal):\n" +
                "    def speak(self) -> str:\n        return self.name + \" barks\"\n" +
                "\n" +
                "def main() -> None:\n" +
                "    d = Dog(\"Rex\")\n" +
                "    print(d.speak())\n" +
                "\n" +
                "main()\n");
        }

        [TestMethod]
        public void Accepts_CollectionsAndComprehensions()
        {
            AssertAcceptedAndBehaviorPreserved(
                "def main() -> None:\n" +
                "    nums = [1, 2, 3, 4, 5]\n" +
                "    squares = [x * x for x in nums]\n" +
                "    print(squares)\n" +
                "    evens = [x for x in nums if x % 2 == 0]\n" +
                "    print(evens)\n" +
                "\n" +
                "main()\n");
        }

        [TestMethod]
        public void Accepts_TryExceptFinally()
        {
            AssertAcceptedAndBehaviorPreserved(
                "def divide(a: int, b: int) -> float:\n" +
                "    try:\n        return a / b\n" +
                "    except ZeroDivisionError:\n        return 0.0\n" +
                "    finally:\n        print(\"done\")\n" +
                "\n" +
                "def main() -> None:\n" +
                "    print(divide(10, 2))\n" +
                "    print(divide(10, 0))\n" +
                "\n" +
                "main()\n");
        }

        [TestMethod]
        public void Accepts_DeferredLambdaPattern()
        {
            AssertAcceptedAndBehaviorPreserved(
                "def main() -> None:\n" +
                "    double = lambda x: x * 2\n" +
                "    print(double(21))\n" +
                "\n" +
                "main()\n");
        }

        [TestMethod]
        public void Accepts_OptionalAndNoneCheck()
        {
            AssertAcceptedAndBehaviorPreserved(
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
                "\n" +
                "main()\n");
        }

        [TestMethod]
        public void Accepts_FStringWithConversionAndFormatSpec()
        {
            AssertAcceptedAndBehaviorPreserved(
                "def main() -> None:\n" +
                "    name: str = \"world\"\n" +
                "    pi: float = 3.14159\n" +
                "    print(f\"hello {name}\")\n" +
                "    print(f\"pi={pi:.2f}\")\n" +
                "    print(f\"repr={name!r}\")\n" +
                "\n" +
                "main()\n");
        }

        [TestMethod]
        public void Accepts_StaticmethodClassmethodProperty()
        {
            AssertAcceptedAndBehaviorPreserved(
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
                "\n" +
                "main()\n");
        }

        // ---------------------------- rejection ----------------------------

        [TestMethod]
        public void Rejects_Eval()
        {
            AssertRejectedWithCode("def main() -> None:\n    eval(\"1+1\")\n\nmain()\n", PyCppDiagnosticCodes.UnsupportedExpression);
        }

        [TestMethod]
        public void Rejects_MultipleInheritance()
        {
            AssertRejectedWithCode(
                "class A:\n    pass\nclass B:\n    pass\nclass C(A, B):\n    pass\n",
                PyCppDiagnosticCodes.MultipleInheritance);
        }

        [TestMethod]
        public void Rejects_MutableDefaultArgument()
        {
            AssertRejectedWithCode(
                "def f(x: list = []) -> None:\n    pass\n",
                PyCppDiagnosticCodes.MutableDefaultArgument);
        }

        [TestMethod]
        public void Rejects_Generator()
        {
            AssertRejectedWithCode(
                "def gen() -> int:\n    yield 1\n",
                PyCppDiagnosticCodes.UnsupportedStatement);
        }

        [TestMethod]
        public void Rejects_AsyncAwait()
        {
            // Calling an async function synchronously in real Python returns a coroutine
            // object, not the computed value — this must be rejected even when the function
            // body never uses `await`, or accepting it would silently mistranslate every
            // caller (asyncio.run(f()) vs. a synchronous call are not interchangeable).
            AssertRejectedWithCode(
                "async def f() -> int:\n    return 1\n",
                PyCppDiagnosticCodes.UnsupportedStatement);
        }

        [TestMethod]
        public void Rejects_WithStatement()
        {
            AssertRejectedWithCode(
                "def main() -> None:\n    with open(\"x\") as f:\n        pass\n\nmain()\n",
                PyCppDiagnosticCodes.UnsupportedStatement);
        }

        [TestMethod]
        public void Rejects_MatchStatement()
        {
            AssertRejectedWithCode(
                "def main() -> None:\n    x = 1\n    match x:\n        case 1:\n            pass\n\nmain()\n",
                PyCppDiagnosticCodes.UnsupportedStatement);
        }

        [TestMethod]
        public void Rejects_DynamicGetattr()
        {
            AssertRejectedWithCode(
                "class Foo:\n    def __init__(self) -> None:\n        self.x = 1\n" +
                "\n" +
                "def main() -> None:\n    f = Foo()\n    print(getattr(f, \"x\"))\n\nmain()\n",
                PyCppDiagnosticCodes.DynamicAttributeAccess);
        }

        [TestMethod]
        public void Rejects_NonlocalClosureCapture()
        {
            AssertRejectedWithCode(
                "def outer() -> None:\n" +
                "    total: int = 0\n" +
                "    def add(n: int) -> None:\n" +
                "        nonlocal total\n" +
                "        total += n\n" +
                "    add(5)\n",
                PyCppDiagnosticCodes.NestedFunctionCapture);
        }

        [TestMethod]
        public void Rejects_UnannotatedFunctionParameter()
        {
            AssertRejectedWithCode("def f(x) -> int:\n    return x\n", PyCppDiagnosticCodes.UnresolvableType);
        }

        [TestMethod]
        public void Rejects_HeterogeneousListLiteral()
        {
            AssertRejectedWithCode("x = [1, \"two\", 3]\n", PyCppDiagnosticCodes.TypeMismatch);
        }

        [TestMethod]
        public void Rejects_UnknownName()
        {
            AssertRejectedWithCode("def main() -> None:\n    print(totally_undefined_name)\n\nmain()\n", PyCppDiagnosticCodes.UnknownName);
        }

        [TestMethod]
        public void Rejects_UnsupportedDecorator()
        {
            AssertRejectedWithCode(
                "def my_decorator(f):\n    return f\n\n@my_decorator\ndef f() -> None:\n    pass\n",
                PyCppDiagnosticCodes.UnsupportedDecorator);
        }

        [TestMethod]
        public void Rejects_DynamicFormatSpec()
        {
            AssertRejectedWithCode(
                "def main() -> None:\n    x: float = 3.14\n    width: int = 10\n    print(f\"{x:{width}}\")\n\nmain()\n",
                PyCppDiagnosticCodes.DynamicFormatSpec);
        }
    }
}
