using System.Collections.Generic;

namespace HelixPythonNamespace.ImportEngine.Analysis
{
    /// <summary>One parameter of a detected CUDA <c>__global__</c> kernel signature.</summary>
    public sealed class CudaKernelParam
    {
        public CudaKernelParam(string typeText, string name)
        {
            TypeText = typeText;
            Name = name;
        }

        /// <summary>Everything before the parameter name, verbatim (e.g. "const int* __restrict__").</summary>
        public string TypeText { get; }

        public string Name { get; }

        public bool IsPointer
        {
            get { return TypeText.IndexOf('*') >= 0; }
        }
    }

    /// <summary>Where a detected kernel's CUDA source was written.</summary>
    public enum CudaKernelSourceKind
    {
        /// <summary>A triple-quoted string literal inside the .py file itself.</summary>
        InlineString,

        /// <summary>
        /// A separate file the Python program reads at runtime — <c>.cu</c>, <c>.cuh</c> or
        /// <c>.txt</c>. Both spellings are common in the wild and neither is detectable by
        /// looking at the Python source alone: the kernel is not in it.
        /// </summary>
        ExternalFile,
    }

    /// <summary>
    /// One CUDA kernel a Python program compiles at runtime (the <c>cp.RawKernel</c>/NVRTC
    /// pattern), found either as a string literal in the .py or in a separate file the .py reads.
    /// </summary>
    public sealed class DetectedCudaKernel
    {
        public DetectedCudaKernel()
        {
            Parameters = new List<CudaKernelParam>();
        }

        public string KernelName { get; set; }

        /// <summary>The full <c>extern "C" __global__ void name(...) { ... }</c> text, braces balanced.</summary>
        public string Body { get; set; }

        public List<CudaKernelParam> Parameters { get; }

        /// <summary>Absolute path of the .py file the literal was found in.</summary>
        public string SourceFile { get; set; }

        /// <summary>The owning package's directory (matches a PackageDescriptor.DirectoryPath).</summary>
        public string PackageDirectory { get; set; }

        /// <summary>True when the literal's assignment was <c>&lt;COMMON-var&gt; + r"""..."""</c> — the body should be emitted with a <c>#include "common.cuh"</c> instead of a duplicated prelude.</summary>
        public bool UsesCommonPrelude { get; set; }

        /// <summary>The Python identifier the kernel source literal was assigned to (e.g. "_PROPOSE_SRC"), for the loader-rewrite pass. Null if it wasn't a simple "&lt;name&gt; = ..." assignment.</summary>
        public string AssignmentVariableName { get; set; }

        /// <summary>Where the CUDA source was written — in the .py, or in a file it reads.</summary>
        public CudaKernelSourceKind SourceKind { get; set; }

        /// <summary>
        /// Absolute path of the <c>.cu</c>/<c>.cuh</c>/<c>.txt</c> the kernel was read from, or
        /// null for an inline literal.
        ///
        /// <para>
        /// Worth having even though <see cref="Body"/> already carries the text: a kernel that
        /// already lives in its own file does not need extracting, and telling the user "this one
        /// is already a file, debug it where it is" is a better answer than copying it somewhere
        /// else and leaving two versions to drift.
        /// </para>
        /// </summary>
        public string ExternalFilePath { get; set; }

        /// <summary>The Python expression that loaded the file, verbatim, for the report.</summary>
        public string LoadExpression { get; set; }

        /// <summary>
        /// True when the kernel is already a standalone <c>.cu</c>/<c>.cuh</c> and can be compiled,
        /// run and debugged exactly where it sits. A <c>.txt</c> is NOT: the emulator and every
        /// real CUDA toolchain dispatch on the extension, so it still has to be written out as a
        /// <c>.cu</c> first.
        /// </summary>
        public bool IsAlreadyStandalone
        {
            get
            {
                if (SourceKind != CudaKernelSourceKind.ExternalFile) return false;
                if (string.IsNullOrEmpty(ExternalFilePath)) return false;
                string ext = System.IO.Path.GetExtension(ExternalFilePath);
                return string.Equals(ext, ".cu", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".cuh", System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>File-safe name for the extracted .cu file, e.g. "propose_batch_kernel.cu".</summary>
        public string CuFileName
        {
            get { return KernelName + ".cu"; }
        }
    }

    /// <summary>Everything the Import Engine found about inline-CUDA-kernel usage in a source tree (read-only, like the rest of ImportAnalysis).</summary>
    public sealed class CudaScaffoldAnalysis
    {
        public CudaScaffoldAnalysis()
        {
            Kernels = new List<DetectedCudaKernel>();
        }

        public List<DetectedCudaKernel> Kernels { get; }

        /// <summary>The resolved shared device-code prelude (e.g. a `DEVICE_SRC` class attribute referenced via `COMMON = X.DEVICE_SRC`), or null when no kernel used one.</summary>
        public string CommonPreludeSource { get; set; }

        public bool HasKernels
        {
            get { return Kernels.Count > 0; }
        }
    }
}
