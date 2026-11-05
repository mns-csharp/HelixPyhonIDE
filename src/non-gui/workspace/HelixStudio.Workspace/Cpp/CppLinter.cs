namespace HelixPythonNamespace.Workspace.Cpp
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    public enum CppLintSeverity { Info, Warning, Error }

    public sealed class CppLintFinding
    {
        public int Line;
        public int Column;
        public string Code = "";
        public string Message = "";
        public CppLintSeverity Severity = CppLintSeverity.Warning;

        public override string ToString()
        {
            return "(" + Line + "," + Column + ") " + Severity.ToString().ToLowerInvariant() + " " + Code + ": " + Message;
        }
    }

    /// <summary>
    /// FR-LINT (C++): the native linter for plain C++ — the counterpart to RustLinter / CSharpLinter /
    /// JavaLinter / JuliaLinter, and the piece C++ was missing while every other language had one.
    ///
    /// Scope note: the C++/CUDA front end (HelixPythonNamespace.CudaVirtualCompiler) already contributes the
    /// language-GENERAL body rules to a C++ file — unreachable code after return/break/continue
    /// (CU6020), unused locals (CU6021) and static array out-of-bounds (CU6030/CU6031) — because
    /// those passes are not CUDA-specific. This class adds the rules that need C++ *declaration and
    /// header* semantics, which no existing pass covers:
    ///
    ///   CPL001  header with no include guard and no #pragma once
    ///   CPL002  `using namespace` at file scope in a header (leaks into every translation unit
    ///           that includes it)
    ///   CPL003  C-style cast — prefer static_cast/reinterpret_cast/const_cast
    ///   CPL004  NULL — prefer nullptr
    ///   CPL005  mutable global variable at namespace scope
    ///
    /// CPL006-013 implement the categories the fixture corpus already specifies. Every language's
    /// fixture carries a <c>lint_demo</c> file mirroring <c>lib_utils/_lint_demo.py</c>, and the C++
    /// one (<c>CppTestSample/src/lib_utils/diagnostics/lint_demo.cpp</c>) labels each block with the
    /// rule it is meant to trigger — that file is the acceptance corpus for these:
    ///
    ///   CPL006  unused #include            (CXX-L-UNUSED)
    ///   CPL007  file-scope `using namespace` in a .cpp   (CXX-L-USING-NAMESPACE; CPL002 covers
    ///           headers, where it is the more serious leak)
    ///   CPL008  type name not PascalCase   (CXX-L-NAMING)
    ///   CPL009  function name not camelCase/snake_case   (CXX-L-NAMING)
    ///   CPL010  function-local mutable static — hidden state across calls (CXX-L-MUTABLE-STATIC)
    ///   CPL011  bare `catch (...)`         (CXX-L-CATCH-ALL)
    ///   CPL012  several statements on one line            (CXX-L-MULTI-STMT)
    ///   CPL013  unexplained numeric literal (CXX-L-MAGIC-NUMBER)
    ///
    /// Deliberately lexical rather than AST-based: each rule is a property of the raw text (or of a
    /// declaration line), all five are decidable without name resolution, and staying off the AST
    /// means a file that does not fully parse still gets linted instead of reporting nothing.
    /// Comments and string literals are stripped first so they cannot produce phantom findings.
    /// </summary>
    public static class CppLinter
    {
        public static bool IsCppFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string ext;
            try { ext = (System.IO.Path.GetExtension(filePath) ?? "").ToLowerInvariant(); }
            catch { return false; }
            switch (ext)
            {
                case ".cpp": case ".cc": case ".cxx": case ".c":
                case ".h": case ".hh": case ".hpp": case ".hxx":
                    return true;
                default: return false;
            }
        }

        private static bool IsHeader(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string ext;
            try { ext = (System.IO.Path.GetExtension(filePath) ?? "").ToLowerInvariant(); }
            catch { return false; }
            return ext == ".h" || ext == ".hh" || ext == ".hpp" || ext == ".hxx";
        }

        // A C-style cast: "(Type)expr". Whether "(x) y" is a cast or a parenthesised expression is
        // genuinely undecidable without knowing whether x names a type, so this rule is deliberately
        // HIGH-PRECISION rather than broad — it fires only when the parenthesised text cannot be
        // anything but a type:
        //   * a builtin type name (with optional const/unsigned/... qualifiers), or
        //   * anything carrying a pointer/reference marker: (Foo*), (const Bar&).
        // A first, looser version matched `if (ok) return ...` as "a cast to type ok" and produced
        // 406 false positives across the 274 fixture files.
        private static readonly Regex CStyleCast = new Regex(
            @"(?<pre>^|[^\w\)\]])\(\s*" +
            @"(?<type>(?:(?:const|volatile|unsigned|signed|struct|class|enum)\s+)*" +
            @"[A-Za-z_]\w*(?:\s*::\s*[A-Za-z_]\w*)*(?:\s*<[^<>()]*>)?" +
            @"(?<ptr>(?:\s*[\*&])+)?)\s*\)\s*(?=[A-Za-z_(][\w:(]*)",
            RegexOptions.Compiled);

        // Types a "(name)expr" cast is unambiguous for even without a * or &.
        private static readonly HashSet<string> BuiltinTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "void", "bool", "char", "wchar_t", "char8_t", "char16_t", "char32_t",
            "short", "int", "long", "float", "double", "size_t", "ssize_t", "ptrdiff_t",
            "int8_t", "int16_t", "int32_t", "int64_t",
            "uint8_t", "uint16_t", "uint32_t", "uint64_t",
            "unsigned", "signed", "intptr_t", "uintptr_t",
        };

        /// <summary>
        /// Keywords whose parenthesised operand IS a type, so "(int)" after them is not a cast:
        /// <c>sizeof(int)</c>, <c>catch (const E&amp;)</c>, <c>decltype(x)</c>. Plus the control-flow
        /// keywords, whose condition can never be a cast.
        ///
        /// Deliberately does NOT list <c>return</c>/<c>case</c>/<c>throw</c> — <c>return (int)d;</c>
        /// is a genuine cast, and excluding those made the rule miss the most common shape of all.
        /// Ambiguous cases like <c>return (n);</c> are already excluded by the builtin/pointer
        /// requirement below, so they need no keyword rule.
        /// </summary>
        private static readonly HashSet<string> NotACastContext = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "catch",
            "sizeof", "alignof", "decltype", "noexcept", "static_assert",
        };

        private static readonly Regex UsingNamespace = new Regex(
            @"^\s*using\s+namespace\s+[\w:]+\s*;", RegexOptions.Compiled);

        // A namespace-scope variable definition that is not const/constexpr/extern/static-inline.
        private static readonly Regex GlobalVar = new Regex(
            @"^(?<decl>[A-Za-z_][\w:<>,\s\*&]*?)\s+(?<name>[A-Za-z_]\w*)\s*(=[^;]*)?;\s*$",
            RegexOptions.Compiled);

        public static IReadOnlyList<CppLintFinding> Lint(string source, string filePath = null)
        {
            var findings = new List<CppLintFinding>();
            if (source == null) return findings;

            string[] rawLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string[] lines = StripCommentsAndStrings(rawLines);

            if (IsHeader(filePath)) CheckHeaderHygiene(lines, findings);
            CheckLines(lines, IsHeader(filePath), findings);
            CheckUnusedIncludes(lines, findings);
            return findings;
        }

        /// <summary>
        /// CPL006. Deliberately narrow: an <c>#include &lt;x&gt;</c> is only reported when x is a
        /// standard header whose primary symbol is unambiguous and that symbol never appears in the
        /// file. Anything cleverer needs real name resolution, and guessing produces exactly the
        /// kind of noise that makes people switch a linter off.
        /// </summary>
        private static readonly Dictionary<string, string> StdHeaderSymbol =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "map", "map" }, { "set", "set" }, { "vector", "vector" }, { "string", "string" },
                { "deque", "deque" }, { "list", "list" }, { "array", "array" },
                { "unordered_map", "unordered_map" }, { "unordered_set", "unordered_set" },
                { "optional", "optional" }, { "variant", "variant" }, { "tuple", "tuple" },
            };

        private static void CheckUnusedIncludes(string[] lines, List<CppLintFinding> findings)
        {
            string body = string.Join("\n", lines);
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = Regex.Match(lines[i], @"^\s*#\s*include\s*<(?<h>[A-Za-z_]\w*)>");
                if (!m.Success) continue;
                string header = m.Groups["h"].Value;
                string symbol;
                if (!StdHeaderSymbol.TryGetValue(header, out symbol)) continue;
                // Look for the symbol anywhere outside this very line.
                string withoutThisLine = body.Remove(0, 0);
                bool used = Regex.IsMatch(withoutThisLine, @"(?<![\w])" + Regex.Escape(symbol) + @"(?![\w])");
                // The include line itself contains the word, so require a SECOND occurrence.
                int occurrences = Regex.Matches(withoutThisLine, @"(?<![\w])" + Regex.Escape(symbol) + @"(?![\w])").Count;
                if (used && occurrences > 1) continue;
                findings.Add(Make(i + 1, 1, "CPL006", CppLintSeverity.Info,
                    "'<" + header + ">' is included but '" + symbol + "' is never used in this file"));
            }
        }

        // CPL001: a header must be include-guarded, or every second inclusion redefines everything.
        private static void CheckHeaderHygiene(string[] lines, List<CppLintFinding> findings)
        {
            bool pragmaOnce = false, guard = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("#pragma", StringComparison.Ordinal) &&
                    t.IndexOf("once", StringComparison.Ordinal) > 0) { pragmaOnce = true; break; }
                // #ifndef X / #define X in the first directives = a classic guard.
                if (t.StartsWith("#ifndef", StringComparison.Ordinal))
                {
                    for (int j = i + 1; j < lines.Length && j < i + 4; j++)
                        if (lines[j].TrimStart().StartsWith("#define", StringComparison.Ordinal)) { guard = true; break; }
                    if (guard) break;
                }
            }
            if (!pragmaOnce && !guard)
                findings.Add(new CppLintFinding
                {
                    Line = 1, Column = 1, Code = "CPL001", Severity = CppLintSeverity.Warning,
                    Message = "header has no include guard and no '#pragma once' — including it twice will redefine its contents",
                });
        }

        private static void CheckLines(string[] lines, bool header, List<CppLintFinding> findings)
        {
            int braceDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                int lineNo = i + 1;
                bool atFileScope = braceDepth == 0;

                if (trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    // CPL002 — only in headers, and only at file scope: inside a function or a .cpp
                    // a using-directive is a local convenience, not a leak.
                    if (header && atFileScope && UsingNamespace.IsMatch(line))
                        findings.Add(Make(lineNo, IndexOfTrimmed(line) + 1, "CPL002", CppLintSeverity.Warning,
                            "'using namespace' at file scope in a header leaks into every file that includes it"));

                    // CPL004 — NULL is a macro for 0; nullptr is type-safe and overload-correct.
                    Match nul = Regex.Match(line, @"(?<![\w:])NULL(?![\w])");
                    if (nul.Success)
                        findings.Add(Make(lineNo, nul.Index + 1, "CPL004", CppLintSeverity.Info,
                            "prefer 'nullptr' to the NULL macro"));

                    // CPL003
                    foreach (Match m in CStyleCast.Matches(line))
                    {
                        string type = Regex.Replace(m.Groups["type"].Value, @"\s+", " ").Trim();
                        bool hasPointer = m.Groups["ptr"].Success;
                        // The word right before "(" — `if (`, `return (`, `foo(` are never casts.
                        string pre = m.Groups["pre"].Value;
                        int end = m.Index + pre.Length;
                        Match prevWord = Regex.Match(line.Substring(0, end), @"([A-Za-z_]\w*)\s*$");
                        if (prevWord.Success && NotACastContext.Contains(prevWord.Groups[1].Value)) continue;

                        string bare = type.Replace("*", "").Replace("&", "").Trim();
                        string lastWord = bare.Contains(" ") ? bare.Substring(bare.LastIndexOf(' ') + 1) : bare;
                        // Unambiguous only as a builtin, or when a * / & proves it is a type.
                        if (!hasPointer && !BuiltinTypes.Contains(lastWord)) continue;
                        // `(void)x;` is the standard "deliberately unused" discard, not a cast a
                        // reader should replace with static_cast. Flagging it would fire on nearly
                        // every C++ codebase for no benefit.
                        if (!hasPointer && lastWord == "void") continue;

                        findings.Add(Make(lineNo, m.Index + pre.Length + 1, "CPL003", CppLintSeverity.Info,
                            "C-style cast to '" + type + "' — prefer static_cast/reinterpret_cast/const_cast"));
                        break;   // one per line is enough to make the point
                    }

                    // CPL005 — mutable namespace-scope state.
                    if (atFileScope && trimmed.EndsWith(";", StringComparison.Ordinal))
                    {
                        Match g = GlobalVar.Match(trimmed);
                        if (g.Success && IsMutableGlobal(g.Groups["decl"].Value))
                            findings.Add(Make(lineNo, 1, "CPL005", CppLintSeverity.Warning,
                                "mutable global '" + g.Groups["name"].Value +
                                "' at namespace scope — prefer a constant, or function-local state"));
                    }

                    // CPL007 — the .cpp counterpart of CPL002. In a header it is a leak (CPL002);
                    // in a .cpp it is a style call, so it is reported at Info.
                    if (!header && atFileScope && UsingNamespace.IsMatch(line))
                        findings.Add(Make(lineNo, IndexOfTrimmed(line) + 1, "CPL007", CppLintSeverity.Info,
                            "'using namespace' at file scope pulls a whole namespace into this translation unit"));

                    // CPL008 — type names are PascalCase by convention.
                    Match typeDecl = Regex.Match(trimmed, @"^(class|struct)\s+(?<name>[A-Za-z_]\w*)");
                    if (typeDecl.Success)
                    {
                        string name = typeDecl.Groups["name"].Value;
                        if (!IsPascalCase(name))
                            findings.Add(Make(lineNo, 1, "CPL008", CppLintSeverity.Info,
                                "type '" + name + "' is not PascalCase"));
                    }

                    // CPL009 — free function names are camelCase or snake_case, not PascalCase.
                    Match fnDecl = FunctionDef.Match(trimmed);
                    if (atFileScope && fnDecl.Success)
                    {
                        string name = fnDecl.Groups["name"].Value;
                        if (IsPascalCase(name) && name.Length > 1)
                            findings.Add(Make(lineNo, 1, "CPL009", CppLintSeverity.Info,
                                "function '" + name + "' is PascalCase — prefer camelCase or snake_case"));
                    }

                    // CPL010 — a function-local `static` that is not const keeps state between
                    // calls, which callers do not see in the signature.
                    if (!atFileScope && Regex.IsMatch(trimmed, @"^static\s+") &&
                        !Regex.IsMatch(trimmed, @"\b(const|constexpr)\b") &&
                        trimmed.EndsWith(";", StringComparison.Ordinal))
                        findings.Add(Make(lineNo, 1, "CPL010", CppLintSeverity.Warning,
                            "function-local mutable 'static' persists across calls — surprising aliasing"));

                    // CPL011 — a bare catch-all swallows everything, std::bad_alloc included.
                    if (Regex.IsMatch(line, @"catch\s*\(\s*\.\.\.\s*\)"))
                        findings.Add(Make(lineNo, 1, "CPL011", CppLintSeverity.Warning,
                            "bare 'catch (...)' swallows every exception, including ones the caller needs"));

                    // CPL012 — two or more statements crammed onto one line. `for (;;)` and a
                    // trailing comment-free one-liner body are not what this is about, so only
                    // count semicolons outside parentheses.
                    if (CountStatementSeparators(trimmed) >= 2 && !trimmed.StartsWith("for", StringComparison.Ordinal))
                        findings.Add(Make(lineNo, 1, "CPL012", CppLintSeverity.Info,
                            "several statements on one line — one statement per line reads better"));

                    // CPL013 — a long unexplained numeric literal. Restricted to high-precision
                    // floats that are NOT being bound to a named constant, which is the shape of a
                    // magic number someone will later fail to recognise.
                    Match magic = Regex.Match(trimmed, @"(?<![\w.])\d+\.\d{5,}(?![\w.])");
                    if (magic.Success && !Regex.IsMatch(trimmed, @"\b(const|constexpr|#define)\b"))
                        findings.Add(Make(lineNo, magic.Index + 1, "CPL013", CppLintSeverity.Info,
                            "unexplained numeric literal '" + magic.Value + "' — name it as a constant"));
                }

                braceDepth += CountChar(line, '{') - CountChar(line, '}');
                if (braceDepth < 0) braceDepth = 0;
            }
        }

        // A free-function definition/declaration at file scope: "ret name(args)".
        private static readonly Regex FunctionDef = new Regex(
            @"^(?:[A-Za-z_][\w:<>,\s\*&]*?)\s+(?<name>[A-Za-z_]\w*)\s*\([^;]*\)\s*(const\s*)?\{?\s*$",
            RegexOptions.Compiled);

        private static bool IsPascalCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (!char.IsUpper(name[0])) return false;
            // ALL_CAPS is a macro/constant convention, not a PascalCase violation.
            if (name.IndexOf('_') >= 0 && name.ToUpperInvariant() == name) return true;
            return name.IndexOf('_') < 0;
        }

        /// <summary>Semicolons that actually separate statements — ignoring any inside parentheses,
        /// so a <c>for (a; b; c)</c> header is not mistaken for three statements.</summary>
        private static int CountStatementSeparators(string line)
        {
            int depth = 0, n = 0;
            foreach (char c in line)
            {
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ';' && depth <= 0) n++;
            }
            return n;
        }

        private static bool IsMutableGlobal(string decl)
        {
            string d = " " + decl.Trim() + " ";
            // Anything that is not actually a mutable object definition.
            foreach (string skip in new[]
            {
                " const ", " constexpr ", " consteval ", " constinit ", " extern ", " typedef ",
                " using ", " namespace ", " class ", " struct ", " enum ", " union ", " template ",
                " return ", " friend ", " public ", " private ", " protected ", " inline ",
            })
                if (d.IndexOf(skip, StringComparison.Ordinal) >= 0) return false;
            // A declaration needs a type AND a name; a bare "foo()" call line has neither.
            return d.Trim().Length > 0 && d.IndexOf('(') < 0;
        }

        private static CppLintFinding Make(int line, int col, string code, CppLintSeverity sev, string message)
        {
            return new CppLintFinding { Line = line, Column = col, Code = code, Severity = sev, Message = message };
        }

        private static int IndexOfTrimmed(string line)
        {
            for (int i = 0; i < line.Length; i++) if (!char.IsWhiteSpace(line[i])) return i;
            return 0;
        }

        private static int CountChar(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
            return n;
        }

        /// <summary>
        /// Blank out comments and string/char literals (preserving line structure and length) so no
        /// rule can fire on a word inside a comment or a quoted string — the classic false-positive
        /// source for text-based linters.
        /// </summary>
        private static string[] StripCommentsAndStrings(string[] lines)
        {
            var outLines = new string[lines.Length];
            bool inBlockComment = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var sb = new System.Text.StringBuilder(line.Length);
                bool inString = false, inChar = false;
                for (int j = 0; j < line.Length; j++)
                {
                    char c = line[j];
                    char next = j + 1 < line.Length ? line[j + 1] : '\0';

                    if (inBlockComment)
                    {
                        if (c == '*' && next == '/') { inBlockComment = false; sb.Append("  "); j++; }
                        else sb.Append(' ');
                        continue;
                    }
                    if (inString || inChar)
                    {
                        sb.Append(' ');
                        if (c == '\\') { if (j + 1 < line.Length) { sb.Append(' '); j++; } continue; }
                        if (inString && c == '"') inString = false;
                        else if (inChar && c == '\'') inChar = false;
                        continue;
                    }
                    if (c == '/' && next == '/') { while (sb.Length < line.Length) sb.Append(' '); break; }
                    if (c == '/' && next == '*') { inBlockComment = true; sb.Append("  "); j++; continue; }
                    if (c == '"') { inString = true; sb.Append(' '); continue; }
                    if (c == '\'') { inChar = true; sb.Append(' '); continue; }
                    sb.Append(c);
                }
                outLines[i] = sb.ToString();
            }
            return outLines;
        }
    }
}
