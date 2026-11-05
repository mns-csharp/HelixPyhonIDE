using System;
using System.Text;
using HelixPythonNamespace.CudaVirtualCompiler.Translation;

namespace HelixPythonNamespace.Editor
{
    /// <summary>Which form the user asked for from the editor's context menu.</summary>
    public enum CudaConversionKind
    {
        /// <summary>C/C++ selection → a raw `__global__` kernel plus its launch scaffold.</summary>
        ToCudaKernel,
        /// <summary>C/C++ selection → a `gpu::for_each` call on the helix_gpu framework.</summary>
        ToCudaFramework,
        /// <summary>CUDA kernel → an equivalent sequential C function.</summary>
        ToC,
        /// <summary>CUDA kernel → an equivalent sequential C++ function.</summary>
        ToCpp,
    }

    /// <summary>
    /// What a conversion produced: the replacement text, the shape that was detected, and — when
    /// it was declined — the concrete dependence that made the conversion unsafe. A refusal is a
    /// normal outcome and carries a reason the user can act on.
    /// </summary>
    public sealed class CudaConversionOutcome
    {
        public bool Succeeded { get; internal set; }
        public string Replacement { get; internal set; }
        public string Message { get; internal set; }
        public string Shape { get; internal set; }
        public string ShapeReason { get; internal set; }
        public string[] Reads { get; internal set; }
        public string[] Writes { get; internal set; }
        public string[] Hazards { get; internal set; }
        public string[] Notes { get; internal set; }
    }

    /// <summary>
    /// The editor-facing commands behind "Convert to CUDA" / "Convert to C" / "Convert to C++".
    ///
    /// All four run the SAME analysis the batch engine uses (<see cref="CudaTranslator"/>): the
    /// region's shape is classified by reach-then-collision, and anything that cannot be proved
    /// safe is refused with the dependence named. Nothing here re-implements translation — the
    /// menu is a thin, honest surface over one engine, so the answer a user gets by clicking is
    /// the answer the test suite pins.
    /// </summary>
    public static class CudaConversionCommands
    {
        /// <summary>
        /// True when the command should appear for this file. The menu is language-aware: CUDA
        /// files offer the reverse direction, C/C++ files offer the forward one.
        /// </summary>
        public static bool AppliesTo(CudaConversionKind kind, string filePath)
        {
            string ext = string.IsNullOrEmpty(filePath)
                ? string.Empty
                : (System.IO.Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant();
            bool isCuda = ext == ".cu" || ext == ".cuh";
            bool isC = ext == ".c" || ext == ".h";
            bool isCpp = ext == ".cpp" || ext == ".cc" || ext == ".cxx" || ext == ".hpp" || ext == ".hxx";

            switch (kind)
            {
                case CudaConversionKind.ToCudaKernel:
                case CudaConversionKind.ToCudaFramework:
                    return isC || isCpp || isCuda;   // a .cu may still hold a host loop to lift
                case CudaConversionKind.ToC:
                case CudaConversionKind.ToCpp:
                    return isCuda;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Converts <paramref name="selection"/> (or the whole document when nothing is selected).
        /// The caller replaces the selected range with <see cref="CudaConversionOutcome.Replacement"/>.
        /// </summary>
        public static CudaConversionOutcome Convert(CudaConversionKind kind, string source,
            int selectionStart, int selectionLength)
        {
            if (string.IsNullOrEmpty(source)) return Fail("There is no source to convert.");

            int start = selectionLength > 0 ? selectionStart : 0;
            int length = selectionLength > 0 ? selectionLength : source.Length;
            if (start < 0 || length < 0 || start + length > source.Length)
                return Fail("The selected source range is invalid.");
            string region = source.Substring(start, length);

            switch (kind)
            {
                case CudaConversionKind.ToCudaKernel:
                    return ToCuda(region, framework: false);
                case CudaConversionKind.ToCudaFramework:
                    return ToCuda(region, framework: true);
                case CudaConversionKind.ToC:
                    return ToHost(region, "C");
                case CudaConversionKind.ToCpp:
                    return ToHost(region, "C++");
                default:
                    return Fail("Unknown conversion.");
            }
        }

        private static CudaConversionOutcome ToCuda(string region, bool framework)
        {
            TranslationResult r = CudaTranslator.CpuToCuda(region, null, emitLaunchScaffold: !framework);
            if (!r.Success) return Refused(r, "This selection cannot become a kernel.");

            string code = framework ? RewriteAsFramework(region, r) : r.Code;
            if (code == null)
                return Refused(r, "The selection could not be expressed with gpu::for_each; "
                                  + "use \"Convert to CUDA kernel\" for the explicit form.");

            var outcome = Ok(r, code);
            outcome.Message = framework
                ? "Converted to a helix_gpu element body (" + r.Shape + "). No launch configuration, "
                  + "memory transfer or index arithmetic is needed — and the same source still builds on the CPU."
                : "Converted to a CUDA kernel (" + r.Shape + "). Keep the original function as the CPU oracle.";
            return outcome;
        }

        private static CudaConversionOutcome ToHost(string region, string language)
        {
            TranslationResult r = CudaTranslator.CudaToCpu(region, null);
            if (!r.Success) return Refused(r, "This kernel cannot become a sequential " + language + " function.");

            string code = r.Code;
            if (language == "C")
            {
                // C has no `bool`/references in the C++ sense; the generated body is already
                // plain, so the only adjustment is the comment banner.
                code = code.Replace("// sequential reference for kernel",
                                    "// sequential C reference for kernel");
            }
            var outcome = Ok(r, code);
            outcome.Message = "Converted to a sequential " + language + " function (" + r.Shape
                              + "). Use it as the reference oracle for the kernel.";
            return outcome;
        }

        /// <summary>
        /// Rewrites a generated kernel into the framework form: the guarded body becomes the
        /// element body of a `gpu::for_each`, which needs no launch configuration and no
        /// index arithmetic from the user.
        /// </summary>
        private static string RewriteAsFramework(string region, TranslationResult r)
        {
            string kernel = r.Code;
            int brace = kernel.IndexOf('{');
            if (brace < 0) return null;

            // The generated kernel always has the shape:
            //   __global__ void NAME(params) { int i = …; if (i < N) { BODY } }
            int idxLine = kernel.IndexOf("int ", brace, StringComparison.Ordinal);
            if (idxLine < 0) return null;
            int idxNameEnd = kernel.IndexOf(' ', idxLine + 4);
            if (idxNameEnd < 0) return null;
            string indexVar = kernel.Substring(idxLine + 4, idxNameEnd - idxLine - 4).Trim();

            int guard = kernel.IndexOf("if (", idxLine, StringComparison.Ordinal);
            if (guard < 0) return null;
            int guardClose = kernel.IndexOf(')', guard);
            if (guardClose < 0) return null;
            string condition = kernel.Substring(guard + 4, guardClose - guard - 4).Trim();

            // The count is the right-hand side of the FIRST `var < count` term of the guard.
            int lt = condition.IndexOf('<');
            if (lt < 0) return null;
            string count = condition.Substring(lt + 1).Trim();
            int amp = count.IndexOf("&&", StringComparison.Ordinal);
            if (amp >= 0) count = count.Substring(0, amp).Trim();

            int bodyOpen = kernel.IndexOf('{', guardClose);
            int bodyClose = MatchingBrace(kernel, bodyOpen);
            if (bodyOpen < 0 || bodyClose < 0) return null;
            string body = kernel.Substring(bodyOpen + 1, bodyClose - bodyOpen - 1).Trim();

            var sb = new StringBuilder();
            sb.Append("// ").Append(r.Shape).Append(" — ").Append(r.ShapeReason).Append('\n');
            sb.Append("// helix_gpu: no launch configuration, no memory transfer, no index arithmetic.\n");
            sb.Append("// The same source compiles for the CPU (the reference path) without nvcc.\n");
            sb.Append("gpu::for_each(").Append(count).Append(", [=] HELIX_GPU (int ").Append(indexVar).Append(") {\n");
            foreach (string line in body.Replace("\r\n", "\n").Split('\n'))
                sb.Append("    ").Append(line.Trim().Length == 0 ? string.Empty : line.Trim()).Append('\n');
            sb.Append("});\n");
            return sb.ToString();
        }

        private static int MatchingBrace(string s, int open)
        {
            if (open < 0 || open >= s.Length || s[open] != '{') return -1;
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static CudaConversionOutcome Ok(TranslationResult r, string code)
        {
            return new CudaConversionOutcome
            {
                Succeeded = true,
                Replacement = code,
                Shape = r.Shape.ToString(),
                ShapeReason = r.ShapeReason,
                Reads = r.Reads.ToArray(),
                Writes = r.Writes.ToArray(),
                Hazards = r.Hazards.ToArray(),
                Notes = r.Notes.ToArray(),
                Message = string.Empty,
            };
        }

        private static CudaConversionOutcome Refused(TranslationResult r, string headline)
        {
            return new CudaConversionOutcome
            {
                Succeeded = false,
                Replacement = null,
                Shape = r.Shape.ToString(),
                ShapeReason = r.ShapeReason,
                Reads = r.Reads.ToArray(),
                Writes = r.Writes.ToArray(),
                Hazards = r.Hazards.ToArray(),
                Notes = r.Notes.ToArray(),
                Message = headline + "\n\n" + r.RefusalReason,
            };
        }

        private static CudaConversionOutcome Fail(string message)
        {
            return new CudaConversionOutcome
            {
                Succeeded = false,
                Message = message,
                Shape = "Unknown",
                ShapeReason = string.Empty,
                Reads = new string[0],
                Writes = new string[0],
                Hazards = new string[0],
                Notes = new string[0],
            };
        }
    }
}
