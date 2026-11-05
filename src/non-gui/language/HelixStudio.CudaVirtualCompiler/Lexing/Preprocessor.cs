namespace HelixPythonNamespace.CudaVirtualCompiler.Lexing
{
    using System.Collections.Generic;

    /// <summary>
    /// Deliberately shallow: NOT a conforming C preprocessor. It scans the source
    /// for object-like `#define NAME VALUE` directives and records simple integer /
    /// identifier replacement values so the launch-config analyzer can constant-fold
    /// `&lt;&lt;&lt;grid, BLOCK&gt;&gt;&gt;` where BLOCK is `#define BLOCK 256`. `#include` is not
    /// followed (no filesystem / external dependency); function-like and conditional
    /// macros are ignored for now (the token survives unmodified). This keeps the
    /// engine self-contained while still resolving the common configuration idiom.
    /// Port of the C++ helix::cuda_vcompiler::Preprocessor
    /// (cuda_vcompiler/lexer/preprocessor.{h,cpp}).
    /// </summary>
    /// <summary>One <c>#include</c> directive: the written path and whether it used angle brackets.</summary>
    public sealed class IncludeDirective
    {
        public string Path;
        public bool IsSystem;   // <...> vs "..."
    }

    public sealed class Preprocessor
    {
        private readonly Dictionary<string, string> defines_ = new Dictionary<string, string>();
        private readonly List<IncludeDirective> includes_ = new List<IncludeDirective>();

        public IReadOnlyDictionary<string, string> Defines { get { return defines_; } }

        /// <summary>The <c>#include</c> directives seen by the last <see cref="Scan"/>, in order.</summary>
        public IReadOnlyList<IncludeDirective> Includes { get { return includes_; } }

        /// <summary>Look up a defined object-like macro; returns null if undefined.</summary>
        public string Lookup(string name)
        {
            string v;
            if (name != null && defines_.TryGetValue(name, out v)) return v;
            return null;
        }

        /// <summary>Scan <paramref name="source"/> and collect object-like defines.</summary>
        public void Scan(string source)
        {
            if (source == null) source = string.Empty;
            int i = 0;
            int n = source.Length;
            while (i < n)
            {
                // Find start of logical line; skip leading horizontal whitespace.
                int lineStart = i;
                while (i < n && (source[i] == ' ' || source[i] == '\t')) ++i;

                if (i < n && source[i] == '#')
                {
                    // Gather the full logical line, honoring backslash-newline.
                    System.Text.StringBuilder line = new System.Text.StringBuilder();
                    int j = i + 1;
                    while (j < n)
                    {
                        char c = source[j];
                        if (c == '\\' && j + 1 < n && (source[j + 1] == '\n' ||
                                (source[j + 1] == '\r' && j + 2 < n && source[j + 2] == '\n')))
                        {
                            line.Append(' ');
                            j += (source[j + 1] == '\r') ? 3 : 2;
                            continue;
                        }
                        if (c == '\n') break;
                        line.Append(c);
                        ++j;
                    }
                    // line == directive without the leading '#'. Parse `define` / `include`.
                    string body = Trim(line.ToString());
                    const string inckw = "include";
                    if (body.Length > inckw.Length && string.CompareOrdinal(body, 0, inckw, 0, inckw.Length) == 0 &&
                        (IsSpace(body[inckw.Length]) || body[inckw.Length] == '"' || body[inckw.Length] == '<'))
                    {
                        string rest = Trim(body.Substring(inckw.Length));
                        if (rest.Length >= 2)
                        {
                            if (rest[0] == '"')
                            {
                                int end = rest.IndexOf('"', 1);
                                if (end > 1) includes_.Add(new IncludeDirective { Path = rest.Substring(1, end - 1), IsSystem = false });
                            }
                            else if (rest[0] == '<')
                            {
                                int end = rest.IndexOf('>', 1);
                                if (end > 1) includes_.Add(new IncludeDirective { Path = rest.Substring(1, end - 1), IsSystem = true });
                            }
                        }
                        i = (j < n) ? j + 1 : n;
                        continue;
                    }
                    const string kw = "define";
                    if (body.Length >= kw.Length && string.CompareOrdinal(body, 0, kw, 0, kw.Length) == 0 &&
                        (body.Length == kw.Length || IsSpace(body[kw.Length])))
                    {
                        int k = kw.Length;
                        while (k < body.Length && IsSpace(body[k])) ++k;
                        // Macro name.
                        if (k < body.Length && IsIdentStart(body[k]))
                        {
                            int nameStart = k;
                            while (k < body.Length && IsIdentPart(body[k])) ++k;
                            string name = body.Substring(nameStart, k - nameStart);
                            // Object-like only: reject function-like `NAME(` with no
                            // space (a '(' immediately after the name).
                            if (k < body.Length && body[k] == '(')
                            {
                                i = (j < n) ? j + 1 : n;
                                continue;
                            }
                            string value = Trim(body.Substring(k));
                            // Strip a trailing line comment if present.
                            int cpos = value.IndexOf("//", System.StringComparison.Ordinal);
                            if (cpos >= 0) value = Trim(value.Substring(0, cpos));
                            defines_[name] = value;
                        }
                    }
                    i = (j < n) ? j + 1 : n;
                    continue;
                }

                // Not a directive line — advance to next newline.
                i = lineStart;
                while (i < n && source[i] != '\n') ++i;
                if (i < n) ++i;
            }
        }

        // Trim leading/trailing ASCII whitespace.
        private static string Trim(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && IsSpace(s[a])) ++a;
            while (b > a && IsSpace(s[b - 1])) --b;
            return s.Substring(a, b - a);
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v';
        }

        private static bool IsAlpha(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static bool IsIdentStart(char c)
        {
            return IsAlpha(c) || c == '_';
        }

        private static bool IsIdentPart(char c)
        {
            return IsAlpha(c) || IsDigit(c) || c == '_';
        }
    }
}
