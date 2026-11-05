using System;

namespace HelixPythonNamespace.PythonAsmCompiler
{
    /// <summary>
    /// Outcome of reading NASM back into Python.
    ///
    /// Refusal is a first-class result here for a sharper reason than in the forward direction:
    /// compilation throws information away. Whether a jump was a <c>while</c> or an <c>if</c>
    /// wrapped around a <c>goto</c> is not recorded anywhere in the instruction stream, and a
    /// decompiler that guesses produces source that looks right and computes something else.
    /// This one recognises exactly the shapes <see cref="PythonToNasm"/> emits and declines the
    /// rest, rather than pretending to be a general x86 decompiler.
    /// </summary>
    public sealed class DecompileResult
    {
        public bool Succeeded { get; }
        /// <summary>The reconstructed Python module, or empty when refused.</summary>
        public string Source { get; }
        /// <summary>Why the assembly could not be read back, in terms the caller can act on.</summary>
        public string RefusalReason { get; }
        /// <summary>1-based line of the assembly that caused a refusal, or 0.</summary>
        public int Line { get; }

        private DecompileResult(bool ok, string source, string reason, int line)
        {
            Succeeded = ok;
            Source = source ?? string.Empty;
            RefusalReason = reason ?? string.Empty;
            Line = line;
        }

        public static DecompileResult Ok(string source) { return new DecompileResult(true, source, null, 0); }

        public static DecompileResult Refuse(string reason, int line = 0)
        {
            return new DecompileResult(false, null, reason, line);
        }

        public override string ToString()
        {
            return Succeeded
                ? "ok (" + Source.Length + " chars)"
                : "refused" + (Line > 0 ? " (asm line " + Line + ")" : "") + ": " + RefusalReason;
        }
    }
}
