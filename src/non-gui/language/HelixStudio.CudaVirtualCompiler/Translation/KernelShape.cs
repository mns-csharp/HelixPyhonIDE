using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.CudaVirtualCompiler.Translation
{
    /// <summary>
    /// The problem SHAPE of a parallel region, per the CUDA decision model: reach, then
    /// collision. The shape — not the syntax — decides whether and how a region converts.
    /// </summary>
    public enum KernelShape
    {
        /// <summary>Not analysable, or nothing parallel was found.</summary>
        Unknown = 0,
        /// <summary>Private elementwise work: output[i] reads only input[i].</summary>
        Map,
        /// <summary>Computed reads, private writes: output[i] reads input[f(i)].</summary>
        Gather,
        /// <summary>Fixed neighbourhood reads: output[i] reads input[i±k].</summary>
        Stencil,
        /// <summary>Every iteration accumulates into ONE location.</summary>
        Reduce,
        /// <summary>Writes to a computed index — collisions possible.</summary>
        Scatter,
        /// <summary>Scatter with accumulation into bins.</summary>
        Histogram,
        /// <summary>Quadratic inner reach with independent outputs.</summary>
        AllPairs,
        /// <summary>Sum-of-products over a shared dimension.</summary>
        MatrixMultiply,
        /// <summary>Carries a dependence on the previous iteration: prefix/recurrence.</summary>
        Scan,
        /// <summary>Independent random streams plus a reduction.</summary>
        MonteCarlo,
        /// <summary>A true sequential residual: must stay on the CPU.</summary>
        Sequential,
    }

    /// <summary>What a translation decided, and why. A refusal is a first-class result.</summary>
    public sealed class TranslationResult
    {
        public bool Success { get; private set; }
        public string Code { get; private set; }
        public KernelShape Shape { get; private set; }
        public string ShapeReason { get; private set; }

        /// <summary>Concrete reason a conversion was refused (never null when !Success).</summary>
        public string RefusalReason { get; private set; }

        /// <summary>Read/write sets and hazards surfaced for the refactoring UI.</summary>
        public List<string> Reads { get; private set; }
        public List<string> Writes { get; private set; }
        public List<string> Hazards { get; private set; }
        public List<string> Notes { get; private set; }

        private TranslationResult()
        {
            Reads = new List<string>();
            Writes = new List<string>();
            Hazards = new List<string>();
            Notes = new List<string>();
        }

        public static TranslationResult Ok(string code, KernelShape shape, string shapeReason)
        {
            return new TranslationResult { Success = true, Code = code, Shape = shape, ShapeReason = shapeReason };
        }

        public static TranslationResult Refuse(KernelShape shape, string shapeReason, string reason)
        {
            return new TranslationResult
            {
                Success = false,
                Shape = shape,
                ShapeReason = shapeReason,
                RefusalReason = reason ?? "unsupported construct",
            };
        }

        public override string ToString()
        {
            return Success ? "OK (" + Shape + ")" : "REFUSED (" + Shape + "): " + RefusalReason;
        }
    }
}
