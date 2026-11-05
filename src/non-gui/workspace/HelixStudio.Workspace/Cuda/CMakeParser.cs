using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Cuda
{
    internal enum CMakeTokKind { Word, LParen, RParen, Quoted, Bracket, Comment, Eof }

    internal sealed class CMakeTok
    {
        public CMakeTokKind Kind;
        public string Text = "";   // logical text (quotes/brackets stripped for Quoted/Bracket)
        public string Raw = "";    // as written
        public int Line;
        public int Column;
    }

    /// <summary>
    /// A real CMake lexer + parser producing a <see cref="CMakeFile"/> AST (FR-CORE-08). Handles line and
    /// bracket comments, quoted / bracket / unquoted arguments (with nested parens inside an argument list),
    /// and builds the block-nesting tree for <c>if/elseif/else/endif</c>, <c>foreach/endforeach</c>,
    /// <c>while/endwhile</c>, <c>function/endfunction</c>, <c>macro/endmacro</c>, <c>block/endblock</c> —
    /// reporting unbalanced or mismatched scopes. Follows the cmake-language(7) grammar pragmatically.
    /// </summary>
    public static class CMakeParser
    {
        private static readonly HashSet<string> BlockOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "if", "foreach", "while", "function", "macro", "block" };

        // close command -> the open command it must match
        private static readonly Dictionary<string, string> BlockClose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["endif"] = "if", ["endforeach"] = "foreach", ["endwhile"] = "while",
            ["endfunction"] = "function", ["endmacro"] = "macro", ["endblock"] = "block",
        };

        private static readonly HashSet<string> BlockMiddle = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "else", "elseif" };

        public static CMakeFile ParseFile(string path)
        {
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            return Parse(text);
        }

        public static CMakeFile Parse(string text)
        {
            var file = new CMakeFile();
            List<CMakeTok> toks = Lex(text, file.Diagnostics);

            int i = 0;
            // A scope frame: the open command whose Body we are currently filling.
            var stack = new Stack<CMakeCommand>();
            var rootNodes = file.Nodes;

            Action<CMakeNode> add = node =>
            {
                if (stack.Count > 0) stack.Peek().Body.Add(node);
                else rootNodes.Add(node);
            };

            while (i < toks.Count && toks[i].Kind != CMakeTokKind.Eof)
            {
                CMakeTok t = toks[i];

                if (t.Kind == CMakeTokKind.Comment)
                {
                    add(new CMakeComment { Line = t.Line, Column = t.Column, Text = t.Text });
                    i++;
                    continue;
                }

                if (t.Kind == CMakeTokKind.LParen || t.Kind == CMakeTokKind.RParen
                    || t.Kind == CMakeTokKind.Quoted || t.Kind == CMakeTokKind.Bracket)
                {
                    file.Diagnostics.Add(Err(t.Line, t.Column, "CMP001", "Expected a command name, found '" + t.Raw + "'"));
                    i++;
                    continue;
                }

                // t is a Word → command name; the next significant token must be '('.
                var cmd = new CMakeCommand { Name = t.Text, Line = t.Line, Column = t.Column };
                i++;
                // skip comments between name and '('
                while (i < toks.Count && toks[i].Kind == CMakeTokKind.Comment) i++;
                if (i >= toks.Count || toks[i].Kind != CMakeTokKind.LParen)
                {
                    file.Diagnostics.Add(Err(cmd.Line, cmd.Column, "CMP002", "Expected '(' after command '" + cmd.Name + "'"));
                    continue;
                }
                i++; // consume '('

                // Collect arguments until the matching ')', honoring nested parens.
                int depth = 1;
                while (i < toks.Count && toks[i].Kind != CMakeTokKind.Eof && depth > 0)
                {
                    CMakeTok a = toks[i];
                    if (a.Kind == CMakeTokKind.LParen) { depth++; i++; continue; }
                    if (a.Kind == CMakeTokKind.RParen) { depth--; i++; continue; }
                    if (a.Kind == CMakeTokKind.Comment) { i++; continue; }
                    var arg = new CMakeArgument
                    {
                        Text = a.Text,
                        Raw = a.Raw,
                        Line = a.Line,
                        Column = a.Column,
                        Kind = a.Kind == CMakeTokKind.Quoted ? CMakeArgKind.Quoted
                             : a.Kind == CMakeTokKind.Bracket ? CMakeArgKind.Bracket : CMakeArgKind.Unquoted,
                    };
                    ExtractReferences(arg);
                    cmd.Arguments.Add(arg);
                    i++;
                }
                if (depth > 0)
                    file.Diagnostics.Add(Err(cmd.Line, cmd.Column, "CMP003", "Unterminated argument list for '" + cmd.Name + "'"));

                file.Commands.Add(cmd);
                ClassifyBlockRole(cmd);

                // Block structure.
                if (cmd.IsBlockClose)
                {
                    string expectedOpen = BlockClose[cmd.Name];
                    if (stack.Count == 0)
                        file.Diagnostics.Add(Err(cmd.Line, cmd.Column, "CMP004", cmd.Name + "() without a matching " + expectedOpen + "()"));
                    else
                    {
                        CMakeCommand open = stack.Peek();
                        if (!string.Equals(open.Name, expectedOpen, StringComparison.OrdinalIgnoreCase))
                            file.Diagnostics.Add(Err(cmd.Line, cmd.Column, "CMP005",
                                cmd.Name + "() does not match the open " + open.Name + "() at line " + open.Line));
                        else
                        {
                            open.EndCommand = cmd;
                            stack.Pop();
                        }
                    }
                    // The close command itself is a node in the enclosing scope.
                    add(cmd);
                }
                else if (cmd.IsBlockOpen)
                {
                    add(cmd);                 // the open node lives in the current scope
                    cmd.Body = new List<CMakeNode>();
                    stack.Push(cmd);          // subsequent nodes go into its Body
                }
                else
                {
                    add(cmd);                 // ordinary command (incl. else/elseif) in current scope
                }
            }

            // Any scopes left open at EOF are unbalanced.
            while (stack.Count > 0)
            {
                CMakeCommand open = stack.Pop();
                file.Diagnostics.Add(Err(open.Line, open.Column, "CMP006", open.Name + "() is never closed (missing end" + open.Name.ToLowerInvariant() + "())"));
            }

            return file;
        }

        private static void ClassifyBlockRole(CMakeCommand cmd)
        {
            if (BlockOpen.Contains(cmd.Name)) cmd.IsBlockOpen = true;
            else if (BlockClose.ContainsKey(cmd.Name)) cmd.IsBlockClose = true;
            else if (BlockMiddle.Contains(cmd.Name)) cmd.IsBlockMiddle = true;
        }

        // Scan an argument's text for ${VAR}, $ENV{VAR}, $CACHE{VAR}, and generator expressions $<...>.
        private static void ExtractReferences(CMakeArgument arg)
        {
            string s = arg.Text;
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i + 1 < s.Length; i++)
            {
                if (s[i] != '$') continue;
                char n = s[i + 1];
                if (n == '<')
                {
                    var r = new CMakeReference { IsGeneratorExpression = true, Offset = i, Scope = "" };
                    int j = i + 2, nameEnd = j;
                    while (nameEnd < s.Length && (char.IsLetterOrDigit(s[nameEnd]) || s[nameEnd] == '_' || s[nameEnd] == ':')) nameEnd++;
                    r.Name = s.Substring(j, nameEnd - j);
                    arg.References.Add(r);
                }
                else if (n == '{' || (IsScopeWord(s, i + 1)))
                {
                    string scope = "";
                    int brace = i + 1;
                    if (n != '{')
                    {
                        int ws = i + 1, we = ws;
                        while (we < s.Length && s[we] != '{') we++;
                        scope = s.Substring(ws, we - ws); // ENV / CACHE
                        brace = we;
                    }
                    if (brace < s.Length && s[brace] == '{')
                    {
                        int j = brace + 1, nameEnd = j;
                        while (nameEnd < s.Length && s[nameEnd] != '}') nameEnd++;
                        arg.References.Add(new CMakeReference
                        {
                            IsGeneratorExpression = false,
                            Scope = scope,
                            Name = s.Substring(j, nameEnd - j),
                            Offset = i,
                        });
                    }
                }
            }
        }

        private static bool IsScopeWord(string s, int at)
        {
            return s.Length - at >= 4 && (s.Substring(at, 4) == "ENV{"
                || (s.Length - at >= 6 && s.Substring(at, 6) == "CACHE{"));
        }

        private static CMakeParseDiagnostic Err(int line, int col, string code, string msg)
        {
            return new CMakeParseDiagnostic { Line = line, Column = col, Code = code, Message = msg, IsError = true };
        }

        // ---- Lexer ----
        private static List<CMakeTok> Lex(string text, List<CMakeParseDiagnostic> diags)
        {
            var toks = new List<CMakeTok>();
            int n = text == null ? 0 : text.Length;
            int i = 0, line = 1, col = 1;
            Action<int> advance = count =>
            {
                for (int k = 0; k < count; k++)
                {
                    if (i < n && text[i] == '\n') { line++; col = 1; } else col++;
                    i++;
                }
            };

            while (i < n)
            {
                char c = text[i];
                if (c == '\n' || c == '\r' || c == ' ' || c == '\t') { advance(1); continue; }

                int startLine = line, startCol = col;

                if (c == '#')
                {
                    // Bracket comment #[[ ... ]] (with optional = signs) or line comment.
                    if (i + 1 < n && text[i + 1] == '[')
                    {
                        int eq = 0, p = i + 2;
                        while (p < n && text[p] == '=') { eq++; p++; }
                        if (p < n && text[p] == '[')
                        {
                            string close = "]" + new string('=', eq) + "]";
                            int end = text.IndexOf(close, p + 1, StringComparison.Ordinal);
                            int rawEnd = end < 0 ? n : end + close.Length;
                            var raw = text.Substring(i, rawEnd - i);
                            toks.Add(new CMakeTok { Kind = CMakeTokKind.Comment, Raw = raw, Text = raw, Line = startLine, Column = startCol });
                            advance(rawEnd - i);
                            continue;
                        }
                    }
                    int le = i; while (le < n && text[le] != '\n') le++;
                    var lc = text.Substring(i, le - i);
                    toks.Add(new CMakeTok { Kind = CMakeTokKind.Comment, Raw = lc, Text = lc.TrimStart('#').Trim(), Line = startLine, Column = startCol });
                    advance(le - i);
                    continue;
                }

                if (c == '(') { toks.Add(new CMakeTok { Kind = CMakeTokKind.LParen, Raw = "(", Text = "(", Line = startLine, Column = startCol }); advance(1); continue; }
                if (c == ')') { toks.Add(new CMakeTok { Kind = CMakeTokKind.RParen, Raw = ")", Text = ")", Line = startLine, Column = startCol }); advance(1); continue; }

                if (c == '"')
                {
                    var sb = new StringBuilder();
                    int p = i + 1;
                    while (p < n && text[p] != '"')
                    {
                        if (text[p] == '\\' && p + 1 < n) { sb.Append(text[p + 1]); p += 2; continue; }
                        sb.Append(text[p]); p++;
                    }
                    int rawEnd = p < n ? p + 1 : n;
                    toks.Add(new CMakeTok { Kind = CMakeTokKind.Quoted, Raw = text.Substring(i, rawEnd - i), Text = sb.ToString(), Line = startLine, Column = startCol });
                    if (p >= n) diags.Add(new CMakeParseDiagnostic { Line = startLine, Column = startCol, Code = "CMP007", Message = "Unterminated quoted argument", IsError = true });
                    advance(rawEnd - i);
                    continue;
                }

                if (c == '[' && (i + 1 < n && (text[i + 1] == '[' || text[i + 1] == '=')))
                {
                    // Bracket argument [[ ... ]] / [=[ ... ]=]
                    int eq = 0, p = i + 1;
                    while (p < n && text[p] == '=') { eq++; p++; }
                    if (p < n && text[p] == '[')
                    {
                        string close = "]" + new string('=', eq) + "]";
                        int end = text.IndexOf(close, p + 1, StringComparison.Ordinal);
                        int rawEnd = end < 0 ? n : end + close.Length;
                        string raw = text.Substring(i, rawEnd - i);
                        string inner = end < 0 ? text.Substring(p + 1) : text.Substring(p + 1, end - (p + 1));
                        toks.Add(new CMakeTok { Kind = CMakeTokKind.Bracket, Raw = raw, Text = inner, Line = startLine, Column = startCol });
                        if (end < 0) diags.Add(new CMakeParseDiagnostic { Line = startLine, Column = startCol, Code = "CMP008", Message = "Unterminated bracket argument", IsError = true });
                        advance(rawEnd - i);
                        continue;
                    }
                }

                // Unquoted argument / command name: run until whitespace or ( ) or # (but keep ${...} intact).
                int s0 = i;
                var w = new StringBuilder();
                while (i < n)
                {
                    char d = text[i];
                    if (d == ' ' || d == '\t' || d == '\n' || d == '\r' || d == '(' || d == ')') break;
                    if (d == '#') break;
                    if (d == '"') break;
                    if (d == '\\' && i + 1 < n) { w.Append(text[i + 1]); advance(2); continue; }
                    w.Append(d);
                    advance(1);
                }
                if (w.Length > 0)
                    toks.Add(new CMakeTok { Kind = CMakeTokKind.Word, Raw = text.Substring(s0, i - s0), Text = w.ToString(), Line = startLine, Column = startCol });
            }

            toks.Add(new CMakeTok { Kind = CMakeTokKind.Eof, Line = line, Column = col });
            return toks;
        }
    }
}
