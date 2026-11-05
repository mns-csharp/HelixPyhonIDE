namespace HelixPythonNamespace.CppAsmCompiler
{
    /// <summary>
    /// Outcome of reading NASM back into C++.
    ///
    /// Like the forward direction, refusal is a first-class result. General decompilation of
    /// arbitrary machine code is undecidable in practice — a <c>while</c> and an <c>if</c> around a
    /// backward jump are the same instructions — so this reader recognises the shapes HelixPython's
    /// own emitter produces and declines everything else instead of guessing.
    /// </summary>
    public sealed class DecompileResult
    {
        public bool Succeeded { get; }
        /// <summary>Recovered C++, or empty when refused.</summary>
        public string Source { get; }
        public string RefusalReason { get; }
        /// <summary>1-based line of the assembly that could not be read, or 0.</summary>
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
            return new DecompileResult(false, string.Empty, reason, line);
        }

        public override string ToString()
        {
            return Succeeded ? "ok" : "refused" + (Line > 0 ? " (asm line " + Line + ")" : "") + ": " + RefusalReason;
        }
    }
}
