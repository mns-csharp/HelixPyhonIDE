using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.CppAsmCompiler
{
    /// <summary>
    /// The parameter and return types of one emitted function, in the translator's own vocabulary.
    /// A caller needs these to know whether a value arrives in ECX or XMM0 and comes back in EAX
    /// or XMM0.
    /// </summary>
    public sealed class TranslatedFunction
    {
        public string Name { get; }
        public IReadOnlyList<string> Parameters { get; }
        public string Returns { get; }

        public TranslatedFunction(string name, IReadOnlyList<string> parameters, string returns)
        {
            Name = name ?? string.Empty;
            Parameters = parameters ?? Array.Empty<string>();
            Returns = returns ?? string.Empty;
        }

        public override string ToString()
        {
            return Returns + " " + Name + "(" + string.Join(", ", Parameters) + ")";
        }
    }

    /// <summary>
    /// Outcome of a C++-to-NASM translation.
    ///
    /// Refusal is a first-class result, not an error path — the same contract
    /// <c>PythonToNasm</c> and <c>CudaTranslator</c> follow. Most of C++ cannot be lowered to bare
    /// assembly without a runtime: classes need constructors and vtables, exceptions need unwind
    /// tables, templates need instantiation, the STL needs the heap. Code outside the supported
    /// subset is REFUSED with a concrete reason and line, because emitting plausible-but-wrong
    /// assembly for a construct the compiler does not really understand is far worse than
    /// declining it. A wrong compiler is discovered at run time, in someone else's code.
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
        /// <summary>Signature of each emitted function.</summary>
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
