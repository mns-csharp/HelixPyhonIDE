namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// Pure helpers for Parameter Info (FR-KB-03): given a signature string and the current argument
    /// index, compute the character span of that parameter so the call tip can highlight it.
    /// </summary>
    public static class CallTipHighlighter
    {
        /// <summary>
        /// The char span [start,end) of the <paramref name="argIndex"/>-th parameter inside the
        /// signature's own <c>(...)</c>. Handles nested parens/brackets/angle-brackets so a comma inside
        /// a template or default value doesn't split a parameter. Leading whitespace is trimmed off the
        /// span start. Returns false when the index is out of range or the signature has no parameters.
        /// </summary>
        public static bool TryGetParamSpan(string sig, int argIndex, out int start, out int end)
        {
            start = end = 0;
            if (string.IsNullOrEmpty(sig) || argIndex < 0) return false;
            int lp = sig.IndexOf('(');
            if (lp < 0) return false;
            int rp = sig.LastIndexOf(')');
            if (rp <= lp) rp = sig.Length;

            int idx = 0, depth = 0, segStart = lp + 1;
            for (int i = lp + 1; i <= rp; i++)
            {
                char c = i < sig.Length ? sig[i] : ',';
                bool boundary = (i == rp) || (c == ',' && depth == 0);
                if (c == '(' || c == '[' || c == '<') depth++;
                else if (c == ')' || c == ']' || c == '>') { if (i < rp) depth--; }
                if (boundary)
                {
                    if (idx == argIndex)
                    {
                        int s = segStart;
                        while (s < i && char.IsWhiteSpace(sig[s])) s++;
                        start = s; end = i;
                        return end > start;
                    }
                    idx++;
                    segStart = i + 1;
                }
            }
            return false;
        }
    }
}
