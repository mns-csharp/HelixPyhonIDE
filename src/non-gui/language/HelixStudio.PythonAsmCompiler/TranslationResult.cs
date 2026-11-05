using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.PythonAsmCompiler
{
    /// <summary>
    /// Outcome of a Python-to-NASM translation.
    ///
    /// Refusal is a first-class result, not an error path. Python is dynamically typed and
    /// garbage-collected; only a small statically-typed subset can be compiled to bare assembly
    /// without a runtime. Anything outside that subset is REFUSED with a concrete reason, because
    /// emitting plausible-but-wrong assembly for code the compiler does not really understand is
    /// far worse than declining it — the same contract <c>CudaTranslator.Refuse</c> follows.
    /// </summary>
    public sealed class TranslationResult
    {
        public bool Succeeded { get; }
        /// <summary>Emitted NASM (win64), or empty when refused.</summary>
        public string Assembly { get; }
        /// <summary>Why the translation was refused, in terms the author can act on.</summary>
        public string RefusalReason { get; }
        /// <summary>1-based line of the construct that caused a refusal, or 0.</summary>
        public int Line { get; }
        /// <summary>Names of the functions successfully emitted.</summary>
        public IReadOnlyList<string> Functions { get; }

        /// <summary>
        /// The parameter and return types of each emitted function. A caller needs these to know
        /// whether a value arrives in RCX or XMM0 and comes back in RAX or XMM0.
        /// </summary>
        public IReadOnlyList<TranslatedFunction> Signatures { get; }

        private TranslationResult(bool ok, string asm, string reason, int line,
                                  IReadOnlyList<string> functions,
                                  IReadOnlyList<TranslatedFunction> signatures)
        {
            Succeeded = ok;
            Assembly = asm ?? string.Empty;
            RefusalReason = reason ?? string.Empty;
            Line = line;
            Functions = functions ?? Array.Empty<string>();
            Signatures = signatures ?? Array.Empty<TranslatedFunction>();
        }

        public static TranslationResult Ok(string asm, IReadOnlyList<string> functions,
                                           IReadOnlyList<TranslatedFunction> signatures = null)
        {
            return new TranslationResult(true, asm, null, 0, functions, signatures);
        }

        public static TranslationResult Refuse(string reason, int line = 0)
        {
            return new TranslationResult(false, string.Empty, reason, line, null, null);
        }

        public override string ToString()
        {
            return Succeeded
                ? "ok: " + string.Join(", ", Functions)
                : "refused" + (Line > 0 ? " (line " + Line + ")" : "") + ": " + RefusalReason;
        }
    }
}
