using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.PyToCpp;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The pretty-printer materializes Pass 1's typed-Python intermediate — its whole purpose
    /// is a real, re-parseable, re-runnable .py file, so "looks right" isn't good enough.
    /// Every case here: parses the original, pretty-prints it, re-parses the printed output
    /// (must be valid Python), then actually runs BOTH the original and the printed source
    /// through the real python.exe interpreter and diffs stdout — proving semantic identity
    /// via real execution, the verification standard established for this whole project.
    /// </summary>
    [TestClass]
    public class PythonPrettyPrinterTests
    {
        private static string RunPython(string code)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_pp_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, code);
            try
            {
                var psi = new ProcessStartInfo("python.exe", "\"" + tmp + "\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    Assert.AreEqual(0, p.ExitCode, "python execution of:\n" + code + "\nfailed:\n" + stderr);
                    return stdout;
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static void AssertRoundTripsIdentically(string source)
        {
            var parsed = HelixPythonNamespace.Python.Python.ParseModule(source);
            Assert.IsFalse(parsed.HasErrors, "original source must parse cleanly:\n" + string.Join("; ", parsed.Diagnostics));

            string printed = new PythonPrettyPrinter().Print(parsed.Module);

            var reparsed = HelixPythonNamespace.Python.Python.ParseModule(printed);
            Assert.IsFalse(reparsed.HasErrors,
                "pretty-printed output must itself re-parse cleanly:\n" + printed + "\n---\n" + string.Join("; ", reparsed.Diagnostics));

            string originalOutput = RunPython(source);
            string printedOutput = RunPython(printed);
            Assert.AreEqual(originalOutput, printedOutput,
                "pretty-printed output must run identically to the original.\nPrinted:\n" + printed);
        }

        [TestMethod]
        public void FunctionsAndPrint()
        {
            AssertRoundTripsIdentically(
                "def add(a: int, b: int) -> int:\n" +
                "    return a + b\n" +
                "\n" +
                "def main() -> None:\n" +
                "    print(add(2, 3))\n" +
                "    print(add(-5, 10))\n" +
                "\n" +
                "if __name__ == \"__main__\":\n" +
                "    main()\n");
        }

        [TestMethod]
        public void ClassesWithInheritanceAndMethods()
        {
            AssertRoundTripsIdentically(
                "class Animal:\n" +
                "    def __init__(self, name: str) -> None:\n" +
                "        self.name = name\n" +
                "    def speak(self) -> str:\n" +
                "        return self.name + \" makes a sound\"\n" +
                "\n" +
                "class Dog(Animal):\n" +
                "    def speak(self) -> str:\n" +
                "        return self.name + \" barks\"\n" +
                "\n" +
                "a = Animal(\"Cat\")\n" +
                "d = Dog(\"Rex\")\n" +
                "print(a.speak())\n" +
                "print(d.speak())\n");
        }

        [TestMethod]
        public void ControlFlow_IfElifElseWhileFor()
        {
            AssertRoundTripsIdentically(
                "def classify(n: int) -> str:\n" +
                "    if n < 0:\n" +
                "        return \"negative\"\n" +
                "    elif n == 0:\n" +
                "        return \"zero\"\n" +
                "    else:\n" +
                "        return \"positive\"\n" +
                "\n" +
                "for i in range(-1, 2):\n" +
                "    print(classify(i))\n" +
                "\n" +
                "total = 0\n" +
                "i = 0\n" +
                "while i < 5:\n" +
                "    total += i\n" +
                "    i += 1\n" +
                "print(total)\n");
        }

        [TestMethod]
        public void CollectionsListDictSetTuple()
        {
            AssertRoundTripsIdentically(
                "nums = [1, 2, 3]\n" +
                "nums.append(4)\n" +
                "print(nums)\n" +
                "\n" +
                "d = {\"a\": 1, \"b\": 2}\n" +
                "d[\"c\"] = 3\n" +
                "print(d)\n" +
                "\n" +
                "s = {1, 2, 2, 3}\n" +
                "print(len(s))\n" +
                "\n" +
                "t = (1, 2, 3)\n" +
                "print(t)\n");
        }

        [TestMethod]
        public void Comprehensions()
        {
            AssertRoundTripsIdentically(
                "squares = [x * x for x in range(5)]\n" +
                "print(squares)\n" +
                "evens = [x for x in range(10) if x % 2 == 0]\n" +
                "print(evens)\n" +
                "pairs = {x: x * x for x in range(4)}\n" +
                "print(pairs)\n" +
                "unique = {x % 3 for x in range(10)}\n" +
                "print(sorted(unique))\n");
        }

        [TestMethod]
        public void FStrings_ConversionAndFormatSpec()
        {
            AssertRoundTripsIdentically(
                "name = \"world\"\n" +
                "pi = 3.14159\n" +
                "print(f\"hello {name}\")\n" +
                "print(f\"pi = {pi:.2f}\")\n" +
                "print(f\"repr = {name!r}\")\n" +
                "print(f\"combined {pi!s:>10}\")\n" +
                "print(f\"a\" f\"b{name}c\")\n");
        }

        [TestMethod]
        public void ExceptionsTryExceptElseFinally()
        {
            AssertRoundTripsIdentically(
                "def divide(a: int, b: int) -> float:\n" +
                "    try:\n" +
                "        result = a / b\n" +
                "    except ZeroDivisionError as e:\n" +
                "        print(\"caught:\", str(e))\n" +
                "        return 0.0\n" +
                "    else:\n" +
                "        print(\"no error\")\n" +
                "        return result\n" +
                "    finally:\n" +
                "        print(\"cleanup\")\n" +
                "\n" +
                "print(divide(10, 2))\n" +
                "print(divide(10, 0))\n");
        }

        [TestMethod]
        public void CustomExceptionClassAndRaise()
        {
            AssertRoundTripsIdentically(
                "class MyError(Exception):\n" +
                "    pass\n" +
                "\n" +
                "def check(x: int) -> None:\n" +
                "    if x < 0:\n" +
                "        raise MyError(\"negative!\")\n" +
                "    print(\"ok\", x)\n" +
                "\n" +
                "try:\n" +
                "    check(5)\n" +
                "    check(-1)\n" +
                "except MyError as e:\n" +
                "    print(\"caught:\", str(e))\n");
        }

        [TestMethod]
        public void LambdaTernaryWalrus()
        {
            AssertRoundTripsIdentically(
                "double = lambda x: x * 2\n" +
                "print(double(21))\n" +
                "y = 5\n" +
                "label = \"big\" if y > 3 else \"small\"\n" +
                "print(label)\n" +
                "data = [1, 2, 3, 4, 5]\n" +
                "results = [y for x in data if (y := x * x) > 5]\n" +
                "print(results)\n");
        }

        [TestMethod]
        public void SlicingAndSubscript()
        {
            AssertRoundTripsIdentically(
                "s = \"hello world\"\n" +
                "print(s[0:5])\n" +
                "print(s[-5:])\n" +
                "print(s[::-1])\n" +
                "lst = [0, 1, 2, 3, 4, 5]\n" +
                "print(lst[1:4])\n" +
                "print(lst[::2])\n");
        }

        [TestMethod]
        public void AugmentedAssignmentAllOperators()
        {
            AssertRoundTripsIdentically(
                "x = 10\n" +
                "x += 5\n" +
                "x -= 2\n" +
                "x *= 3\n" +
                "x //= 4\n" +
                "x %= 5\n" +
                "print(x)\n");
        }

        [TestMethod]
        public void MultipleAssignmentAndUnpacking()
        {
            AssertRoundTripsIdentically(
                "a = b = 5\n" +
                "print(a, b)\n" +
                "x, y = 1, 2\n" +
                "x, y = y, x\n" +
                "print(x, y)\n");
        }

        [TestMethod]
        public void StarArgsAndKwargs()
        {
            AssertRoundTripsIdentically(
                "def total(*args: int, **kwargs: int) -> int:\n" +
                "    s = 0\n" +
                "    for a in args:\n" +
                "        s += a\n" +
                "    for k in kwargs:\n" +
                "        s += kwargs[k]\n" +
                "    return s\n" +
                "\n" +
                "print(total(1, 2, 3, x=4, y=5))\n");
        }

        [TestMethod]
        public void GlobalAndNonlocal()
        {
            AssertRoundTripsIdentically(
                "counter = 0\n" +
                "\n" +
                "def increment() -> None:\n" +
                "    global counter\n" +
                "    counter += 1\n" +
                "\n" +
                "def make_adder():\n" +
                "    total = 0\n" +
                "    def add(n: int) -> int:\n" +
                "        nonlocal total\n" +
                "        total += n\n" +
                "        return total\n" +
                "    return add\n" +
                "\n" +
                "increment()\n" +
                "increment()\n" +
                "print(counter)\n" +
                "adder = make_adder()\n" +
                "print(adder(3))\n" +
                "print(adder(4))\n");
        }

        [TestMethod]
        public void BooleanAndBitwiseOperators()
        {
            AssertRoundTripsIdentically(
                "a = True\n" +
                "b = False\n" +
                "print(a and b)\n" +
                "print(a or b)\n" +
                "print(not a)\n" +
                "x = 6\n" +
                "y = 3\n" +
                "print(x & y, x | y, x ^ y, x << 1, x >> 1)\n");
        }

        [TestMethod]
        public void FloorDivAndModuloNegativeSignSemantics()
        {
            AssertRoundTripsIdentically(
                "print(-7 // 2)\n" +
                "print(-7 % 2)\n" +
                "print(7 // -2)\n" +
                "print(7 % -2)\n");
        }
    }
}
