namespace HelixPythonNamespace.CudaEmulator
{
    using System;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Thrown when emulated execution hits a fault the program is responsible for
    /// (out-of-bounds access, null/use-after-free deref, divide-by-zero, a divergent
    /// __syncthreads) OR when the interpreter meets a C++/CUDA construct it does not
    /// model. Always carries the offending AST node's source span. The interpreter
    /// NEVER asserts/crashes on unsupported input — it throws one of these instead.
    /// Port of the C++ helix::cuda_emulator::RuntimeError (cuda_emulator/runtime_error.h).
    /// </summary>
    public sealed class RuntimeError : Exception
    {
        public enum Kind
        {
            OutOfBounds,
            NullDeref,
            UseAfterFree,
            DoubleFree,
            DivideByZero,
            DivergentBarrier,
            Unsupported,
            BadLaunch,
            Other,
        }

        private readonly Kind kind_;
        private readonly string message_;
        private readonly SourceSpan span_;

        public RuntimeError(Kind kind, string message, SourceSpan span) : base(message)
        {
            kind_ = kind;
            message_ = message ?? string.Empty;
            span_ = span;
        }

        public Kind ErrorKind { get { return kind_; } }
        public string ErrorMessage { get { return message_; } }
        public SourceSpan Span { get { return span_; } }

        /// <summary>Convenience for the many default arms that reject unmodeled constructs.</summary>
        public static void Unsupported(string what, SourceSpan span)
        {
            throw new RuntimeError(Kind.Unsupported, "unsupported construct: " + what, span);
        }
    }
}
