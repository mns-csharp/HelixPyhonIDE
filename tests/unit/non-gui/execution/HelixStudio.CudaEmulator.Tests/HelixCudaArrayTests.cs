using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.CudaEmulator.Tests
{
    /// <summary>
    /// The array half of the <c>helix_cuda</c> shim, driven the only way that proves anything: a
    /// real Python process running an unmodified CuPy-shaped program with the emulator switched on.
    ///
    /// <para>
    /// Every operation here compiles and launches a real CUDA kernel through the bridge — the
    /// elementwise ones, the fills, and the reduction, which uses shared memory and
    /// <c>__syncthreads</c>. That is the point of testing it end to end rather than unit-testing the
    /// Python: doing this arithmetic in Python would give the same numbers while quietly meaning the
    /// emulator was no longer involved, and this test would not notice.
    /// </para>
    /// </summary>
    [TestClass]
    public class HelixCudaArrayTests
    {
        private static string _repositoryRoot;
        private static Dictionary<string, string> _values;
        private static string _rawOutput;

        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// Run the fixture once for the whole class. It starts a Python interpreter and a bridge
        /// process; doing that per test method would multiply a two-second cost by a dozen for no
        /// extra coverage, since every assertion reads the same run.
        /// </summary>
        [ClassInitialize]
        public static void RunFixture(TestContext context)
        {
            _repositoryRoot = FindRepositoryRoot();
            if (_repositoryRoot == null) { _values = null; return; }

            string script = Path.Combine(_repositoryRoot, "tests", "fixtures", "python",
                "cuda_arrays", "main.py");
            string bridge = Path.Combine(_repositoryRoot, "artifacts", "bin",
                "HelixStudio.CudaBridge", "Debug", "net472", "helix_cuda_bridge.exe");
            string shim = Path.Combine(_repositoryRoot, "src", "python");
            if (!File.Exists(script) || !File.Exists(bridge)) { _values = null; return; }

            var info = new ProcessStartInfo("python", "-u \"" + script + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(script),
            };
            info.EnvironmentVariables["HELIX_CUDA_EMULATE"] = "1";
            info.EnvironmentVariables["HELIX_CUDA_BRIDGE"] = bridge;
            info.EnvironmentVariables["PYTHONPATH"] =
                shim + Path.PathSeparator + Path.Combine(shim, "helix_cuda_bootstrap");

            string output, errors;
            try
            {
                using (Process process = Process.Start(info))
                {
                    output = process.StandardOutput.ReadToEnd();
                    errors = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(120000)) { try { process.Kill(); } catch { } }
                }
            }
            catch (Exception) { _values = null; return; }

            _rawOutput = output + Environment.NewLine + errors;
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in output.Replace("\r", string.Empty).Split('\n'))
            {
                int separator = line.IndexOf(" = ", StringComparison.Ordinal);
                if (separator <= 0) continue;
                _values[line.Substring(0, separator).Trim()] = line.Substring(separator + 3).Trim();
            }
        }

        private static string Value(string name)
        {
            if (_values == null)
                Assert.Inconclusive("could not run the fixture (needs python and a built bridge)");
            string result;
            if (!_values.TryGetValue(name, out result))
                Assert.Fail("the program printed no '" + name + "' line. Output:"
                    + Environment.NewLine + _rawOutput);
            return result;
        }

        [TestMethod]
        public void ConstructorsFillOnTheDevice()
        {
            Assert.AreEqual("[0.0, 0.0, 0.0, 0.0]", Value("zeros"));
            Assert.AreEqual("[1, 1, 1, 1]", Value("ones"));
            Assert.AreEqual("[7, 7, 7]", Value("full"));
            Assert.AreEqual("[0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0]", Value("arange"));
            Assert.AreEqual("[2, 5, 8]", Value("arange3"), "arange(2, 11, 3)");
            Assert.AreEqual("[3, 1, 4, 1, 5]", Value("asarray"));
        }

        [TestMethod]
        public void ElementwiseArithmeticRunsAsKernels()
        {
            Assert.AreEqual("[2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0]", Value("combined"),
                "arange(10) + ones(10) * 2.0");
            Assert.AreEqual("[2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0]", Value("minus"));
            Assert.AreEqual("[1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5]", Value("divided"));
            Assert.AreEqual("[20.0, 19.0, 18.0, 17.0, 16.0, 15.0, 14.0, 13.0, 12.0, 11.0]",
                Value("rsub"), "a scalar on the LEFT reverses the operands");
        }

        /// <summary>
        /// The reduction is the one that could most easily be wrong and still look right: it uses
        /// shared memory and two <c>__syncthreads</c> barriers, so it exercises the emulator's
        /// threaded block path rather than its sequential one.
        /// </summary>
        [TestMethod]
        public void SumIsARealReductionOnTheEmulator()
        {
            Assert.AreEqual("65.0", Value("sum"), "2+3+...+11");
            Assert.AreEqual("14", Value("intsum"), "3+1+4+1+5");
        }

        [TestMethod]
        public void AnArrayGoesStraightIntoAHandWrittenKernel()
        {
            Assert.AreEqual("[20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0, 110.0]",
                Value("afterkernel"),
                "passing the array itself, not a pointer, is the ordinary CuPy spelling");
        }

        [TestMethod]
        public void TheSmallThingsRealCodeReads()
        {
            Assert.AreEqual("50.0", Value("item"), "c[3] is 5.0, and the kernel has since scaled by 10");
            Assert.AreEqual("10", Value("len"));
            Assert.AreEqual("(10,)", Value("shape"));
        }

        /// <summary>
        /// The limits have to fail loudly. A 2-D request that quietly produced a flat array, or a
        /// length mismatch that broadcast, would give a wrong answer with no way to notice — which
        /// is exactly the failure mode this whole shim exists to avoid.
        /// </summary>
        [TestMethod]
        public void TheLimitsAreRefusedRatherThanGuessed()
        {
            Assert.AreEqual("refused", Value("twod"), "two dimensions are not supported");
            Assert.AreEqual("refused", Value("mismatch"), "there is no broadcasting");
        }
    }
}
