using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.ImportEngine.Analysis;
using HelixPythonNamespace.Workspace.Python;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.ImportEngine.Tests
{
    /// <summary>
    /// The four ways a Python program can carry a CUDA kernel, and which of them the static
    /// analyser can and cannot see.
    ///
    /// <para>
    /// The analyser originally found only the first: a kernel written as a triple-quoted string
    /// inside the .py. The other two file-based styles are just as common — a program that reads
    /// <c>kernels/saxpy.cu</c>, or the same thing named <c>.txt</c> because somebody treated the
    /// kernel as data — and were invisible, so a project using them reported zero kernels and
    /// offered the user nothing.
    /// </para>
    /// <para>
    /// The fourth style is here to pin a LIMIT rather than a feature. When the kernel text is
    /// assembled at run time, the finished string exists only inside the running program, and no
    /// amount of reading the source will produce it. The test asserts the analyser stays silent
    /// about it: a wrong guess would send the user to debug a kernel that is not the one their
    /// program runs, which is worse than reporting nothing.
    /// </para>
    /// </summary>
    [TestClass]
    public class CudaKernelSourceStyleTests
    {
        private static string FixturePackage()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && Directory.GetFiles(dir.FullName, "*.sln").Length == 0)
                dir = dir.Parent;
            Assert.IsNotNull(dir, "could not locate the repo root from the test output folder");
            return Path.Combine(dir.FullName, "tests", "fixtures", "python",
                "cuda_kernel_sources", "gpu_pkg");
        }

        private static CudaScaffoldAnalysis Analyze()
        {
            string package = FixturePackage();
            Assert.IsTrue(Directory.Exists(package), "fixture missing: " + package);
            var descriptor = new PackageDescriptor("gpu_pkg", package);
            return CudaKernelScaffoldAnalyzer.Analyze(new List<PackageDescriptor> { descriptor });
        }

        private static DetectedCudaKernel Kernel(CudaScaffoldAnalysis analysis, string name)
        {
            return analysis.Kernels.FirstOrDefault(k => k.KernelName == name);
        }

        [TestMethod]
        public void AKernelWrittenInThePythonSource_IsFound()
        {
            DetectedCudaKernel kernel = Kernel(Analyze(), "add_vectors");
            Assert.IsNotNull(kernel, "the inline string-literal style regressed");
            Assert.AreEqual(CudaKernelSourceKind.InlineString, kernel.SourceKind);
            Assert.IsNull(kernel.ExternalFilePath);
            Assert.IsFalse(kernel.IsAlreadyStandalone);
            StringAssert.Contains(kernel.Body, "c[i] = a[i] + b[i]");
        }

        [TestMethod]
        public void AKernelReadFromACuFile_IsFound()
        {
            DetectedCudaKernel kernel = Kernel(Analyze(), "saxpy");
            Assert.IsNotNull(kernel, "open('kernels/saxpy.cu').read() was not followed");
            Assert.AreEqual(CudaKernelSourceKind.ExternalFile, kernel.SourceKind);
            StringAssert.EndsWith(kernel.ExternalFilePath, "saxpy.cu");
            Assert.IsTrue(kernel.IsAlreadyStandalone,
                "a .cu needs no extraction -- it can be debugged where it sits");
        }

        [TestMethod]
        public void AKernelReadFromATxtFile_IsFound_AndStillNeedsExtracting()
        {
            DetectedCudaKernel kernel = Kernel(Analyze(), "reduce_sum");
            Assert.IsNotNull(kernel, "pathlib.Path('kernels/reduce.txt').read_text() was not followed");
            Assert.AreEqual(CudaKernelSourceKind.ExternalFile, kernel.SourceKind);
            StringAssert.EndsWith(kernel.ExternalFilePath, "reduce.txt");
            // The emulator and every real CUDA toolchain dispatch on the extension, so a .txt has
            // to be written out as a .cu before anything will compile it.
            Assert.IsFalse(kernel.IsAlreadyStandalone);
            Assert.AreEqual("reduce_sum.cu", kernel.CuFileName);
        }

        [TestMethod]
        public void AFileKernelKeepsItsDeviceHelpers()
        {
            // saxpy.cu declares `__device__ float scale(...)` above the kernel. Extracting the
            // __global__ function alone would produce a .cu that fails on an undefined identifier.
            DetectedCudaKernel kernel = Kernel(Analyze(), "saxpy");
            Assert.IsNotNull(kernel);
            StringAssert.Contains(kernel.Body, "__device__ float scale");
        }

        [TestMethod]
        public void AFileKernelNeedsNoExternC()
        {
            // The relaxed signature applies only to text that came from a file. `extern "C"` stops
            // C++ name mangling so NVRTC can look the kernel up by string; a kernel compiled the
            // ordinary way does not need it, and most hand-written .cu files do not have it.
            string package = FixturePackage();
            string plain = Path.Combine(package, "kernels", "plain.cu");
            File.WriteAllText(plain,
                "__global__ void no_linkage_spec(int* out) { out[threadIdx.x] = 7; }\n");
            string loader = Path.Combine(package, "loads_plain.py");
            File.WriteAllText(loader,
                "import cupy as cp\n\n\nclass PlainLoader:\n"
                + "    @staticmethod\n"
                + "    def build():\n"
                + "        return cp.RawKernel(open('kernels/plain.cu').read(), 'no_linkage_spec')\n");
            try
            {
                Assert.IsNotNull(Kernel(Analyze(), "no_linkage_spec"));
            }
            finally
            {
                try { File.Delete(plain); } catch { }
                try { File.Delete(loader); } catch { }
            }
        }

        [TestMethod]
        public void AKernelAssembledAtRuntime_IsNotGuessedAt()
        {
            // kernels_built.py glues the kernel out of _HEADER + name + _BODY at call time. The
            // finished text never appears in the source, so the analyser must report nothing
            // rather than invent a kernel the program does not actually run.
            CudaScaffoldAnalysis analysis = Analyze();
            foreach (DetectedCudaKernel kernel in analysis.Kernels)
                Assert.AreNotEqual("loads_plain", kernel.SourceFile,
                    "unexpected kernel from the runtime-assembly fixture");

            bool anyFromBuilt = analysis.Kernels.Any(
                k => (k.SourceFile ?? string.Empty).EndsWith("kernels_built.py", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(anyFromBuilt,
                "a runtime-assembled kernel cannot be read statically and must not be reported");
        }

        [TestMethod]
        public void APathLiteralLeadingOutOfThePackage_IsNotRead()
        {
            // The path comes from a string in somebody else's project. Following one that escapes
            // the package would let a scan read arbitrary files off the machine.
            string package = FixturePackage();
            string loader = Path.Combine(package, "escapes.py");
            File.WriteAllText(loader,
                "import cupy as cp\n\n\nclass Escapes:\n"
                + "    @staticmethod\n"
                + "    def build():\n"
                + "        return cp.RawKernel(open('../../../../secrets.cu').read(), 'x')\n");
            try
            {
                foreach (DetectedCudaKernel kernel in Analyze().Kernels)
                    Assert.IsFalse(
                        (kernel.ExternalFilePath ?? string.Empty).IndexOf("secrets", StringComparison.OrdinalIgnoreCase) >= 0,
                        "resolved a path outside the package");
            }
            finally
            {
                try { File.Delete(loader); } catch { }
            }
        }

        [TestMethod]
        public void EveryStyleThatCanBeFound_IsFound()
        {
            CudaScaffoldAnalysis analysis = Analyze();
            var names = analysis.Kernels.Select(k => k.KernelName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "add_vectors", "reduce_sum", "saxpy" }, names,
                "expected exactly the three statically-visible kernels, got: " + string.Join(", ", names));
        }
    }
}
