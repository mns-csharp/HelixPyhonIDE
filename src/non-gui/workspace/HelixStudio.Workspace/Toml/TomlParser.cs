using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Toml
{
    /// <summary>
    /// A TOML v1.0.0 parser producing a <see cref="TomlDocument"/> AST (FR-CORE-05). Handles bare/quoted/
    /// dotted keys, <c>[table]</c> and <c>[[array of tables]]</c> headers, inline tables, arrays (nested,
    /// multi-line), all scalar types (basic/literal/multi-line strings, sign/underscore/hex/oct/bin ints,
    /// floats incl. inf/nan/exp, booleans, and RFC-3339 date-times), and comments. Reports duplicate keys,
    /// redefined tables, and syntax errors. Supersedes the regex subset in <c>PyProjectReader</c>.
    /// </summary>
    public static class TomlParser
    {
        public static TomlDocument ParseFile(string path)
        {
            return Parse(File.Exists(path) ? File.ReadAllText(path) : "");
        }

        public static TomlDocument Parse(string text)
        {
            var doc = new TomlDocument();
            new Impl(text ?? "", doc).Run();
            return doc;
        }

        private sealed class Impl
        {
            private readonly string _s;
            private readonly int _n;
            private int _i, _line = 1, _col = 1;
            private readonly TomlDocument _doc;
            private TomlTable _current;
            private readonly List<string> _pendingComments = new List<string>();  // standalone comment lines awaiting the next item
            private TomlItem _lastItem;                                            // last value/table, for trailing-comment attachment

            public Impl(string s, TomlDocument doc) { _s = s; _n = s.Length; _doc = doc; _current = doc.Root; }

            private char Cur { get { return _i < _n ? _s[_i] : '\0'; } }
            private char Peek(int k) { return _i + k < _n ? _s[_i + k] : '\0'; }

            private void Adv()
            {
                if (_i < _n) { if (_s[_i] == '\n') { _line++; _col = 1; } else _col++; _i++; }
            }
            private void Adv(int k) { for (int j = 0; j < k; j++) Adv(); }

            private void Err(int line, int col, string code, string msg)
            {
                _doc.Diagnostics.Add(new TomlDiagnostic { Line = line, Column = col, Code = code, Message = msg, IsError = true });
            }

            public void Run()
            {
                while (_i < _n)
                {
                    SkipInlineWs();
                    char c = Cur;
                    if (c == '\0') break;
                    if (c == '\n' || c == '\r') { Adv(); continue; }
                    if (c == '#') { _pendingComments.Add(CaptureComment()); continue; }   // standalone comment line → buffer for next item
                    if (c == '[') { ParseHeader(); continue; }
                    ParseKeyValue(_current);
                    SkipInlineWs();
                    if (Cur == '#') { string tc = CaptureComment(); if (_lastItem != null) _lastItem.TrailingComment = tc; }
                    if (Cur == '\n' || Cur == '\r') Adv();
                    else if (Cur != '\0') { Err(_line, _col, "TML003", "Expected end of line after value, found '" + Cur + "'"); SkipToLineEnd(); }
                }
            }

            private void SkipInlineWs() { while (Cur == ' ' || Cur == '\t') Adv(); }
            private void SkipComment() { while (Cur != '\n' && Cur != '\0') Adv(); }
            // Capture a comment run (from '#' to end of line, not consuming the newline), trimmed of a trailing '\r'.
            private string CaptureComment() { int start = _i; while (Cur != '\n' && Cur != '\0') Adv(); return _s.Substring(start, _i - start).TrimEnd('\r', ' ', '\t'); }
            private void DrainPendingTo(TomlItem item) { if (item != null && _pendingComments.Count > 0) { foreach (string c in _pendingComments) item.AddLeadingComment(c); _pendingComments.Clear(); } }
            private void SkipToLineEnd() { while (Cur != '\n' && Cur != '\0') Adv(); }

            // ---- headers: [table] / [[array of tables]] ----
            private void ParseHeader()
            {
                int hl = _line, hc = _col;
                bool arrayOfTables = Peek(1) == '[';
                Adv(arrayOfTables ? 2 : 1);
                SkipInlineWs();
                List<string> key = ParseKeyPath();
                SkipInlineWs();
                int close = arrayOfTables ? 2 : 1;
                if (Cur == ']' && (!arrayOfTables || Peek(1) == ']')) Adv(close);
                else { Err(_line, _col, "TML003", "Unterminated table header"); SkipToLineEnd(); return; }

                if (key.Count == 0) { Err(hl, hc, "TML003", "Empty table header"); return; }

                if (arrayOfTables) _current = OpenArrayOfTables(key, hl, hc);
                else _current = OpenTable(key, hl, hc);

                DrainPendingTo(_current);   // whole-line comments above the header attach to this table/element
                _lastItem = _current;

                SkipInlineWs();
                if (Cur == '#') { string tc = CaptureComment(); _current.TrailingComment = tc; }
                if (Cur == '\n' || Cur == '\r') Adv();
            }

            private TomlTable OpenTable(List<string> key, int line, int col)
            {
                TomlTable t = _doc.Root;
                for (int k = 0; k < key.Count; k++)
                {
                    string seg = key[k];
                    TomlItem existing = t.Get(seg);
                    if (existing == null)
                    {
                        var nt = new TomlTable { Line = line, Column = col };
                        t.Set(seg, nt);
                        t = nt;
                    }
                    else if (existing is TomlTable et) { t = et; }
                    else if (existing is TomlTableArray ta && ta.Elements.Count > 0) { t = ta.Elements[ta.Elements.Count - 1]; }
                    else { Err(line, col, "TML002", "Key '" + seg + "' is already a value, cannot open a table"); return new TomlTable(); }
                }
                if (t.ExplicitlyDefined || t.FromInlineTable)
                    Err(line, col, "TML002", "Table [" + string.Join(".", key.ToArray()) + "] is defined more than once");
                t.ExplicitlyDefined = true;
                return t;
            }

            private TomlTable OpenArrayOfTables(List<string> key, int line, int col)
            {
                TomlTable parent = _doc.Root;
                for (int k = 0; k < key.Count - 1; k++)
                {
                    string seg = key[k];
                    TomlItem ex = parent.Get(seg);
                    if (ex is TomlTable et) parent = et;
                    else if (ex is TomlTableArray ta && ta.Elements.Count > 0) parent = ta.Elements[ta.Elements.Count - 1];
                    else if (ex == null) { var nt = new TomlTable { Line = line, Column = col }; parent.Set(seg, nt); parent = nt; }
                    else { Err(line, col, "TML002", "Key '" + seg + "' is not a table"); return new TomlTable(); }
                }
                string last = key[key.Count - 1];
                TomlItem cur = parent.Get(last);
                TomlTableArray arr = cur as TomlTableArray;
                if (arr == null)
                {
                    if (cur != null) { Err(line, col, "TML002", "Key '" + last + "' is not an array of tables"); }
                    arr = new TomlTableArray { Line = line, Column = col };
                    parent.Set(last, arr);
                }
                var elem = new TomlTable { Line = line, Column = col, ExplicitlyDefined = true };
                arr.Elements.Add(elem);
                return elem;
            }

            // ---- key = value ----
            private void ParseKeyValue(TomlTable table)
            {
                int kl = _line, kc = _col;
                List<string> key = ParseKeyPath();
                if (key.Count == 0) { Err(kl, kc, "TML003", "Expected a key"); SkipToLineEnd(); return; }
                SkipInlineWs();
                if (Cur != '=') { Err(_line, _col, "TML003", "Expected '=' after key"); SkipToLineEnd(); return; }
                Adv();
                SkipInlineWs();
                TomlValue val = ParseValue();
                if (val == null) { SkipToLineEnd(); return; }

                // Walk dotted key, creating intermediate tables.
                TomlTable t = table;
                for (int k = 0; k < key.Count - 1; k++)
                {
                    string seg = key[k];
                    TomlItem ex = t.Get(seg);
                    if (ex == null) { var nt = new TomlTable { Line = kl, Column = kc, DefinedByDottedKey = true }; t.Set(seg, nt); t = nt; }
                    else if (ex is TomlTable et)
                    {
                        if (et.FromInlineTable) { Err(kl, kc, "TML002", "Cannot extend inline table '" + seg + "'"); return; }
                        t = et;
                    }
                    else { Err(kl, kc, "TML002", "Key '" + seg + "' is not a table"); return; }
                }
                string leaf = key[key.Count - 1];
                if (t.Get(leaf) != null) { Err(kl, kc, "TML001", "Duplicate key '" + string.Join(".", key.ToArray()) + "'"); return; }
                t.Set(leaf, val);
                DrainPendingTo(val);   // whole-line comments above the key attach to its value
                _lastItem = val;
            }

            private List<string> ParseKeyPath()
            {
                var parts = new List<string>();
                while (true)
                {
                    SkipInlineWs();
                    string part = ParseKeyPart();
                    if (part == null) break;
                    parts.Add(part);
                    SkipInlineWs();
                    if (Cur == '.') { Adv(); continue; }
                    break;
                }
                return parts;
            }

            private string ParseKeyPart()
            {
                char c = Cur;
                if (c == '"' || c == '\'') { var v = ParseQuotedString(); return v; }
                var sb = new StringBuilder();
                while (true)
                {
                    char d = Cur;
                    if ((d >= 'A' && d <= 'Z') || (d >= 'a' && d <= 'z') || (d >= '0' && d <= '9') || d == '_' || d == '-')
                    { sb.Append(d); Adv(); }
                    else break;
                }
                return sb.Length == 0 ? null : sb.ToString();
            }

            // ---- values ----
            private TomlValue ParseValue()
            {
                int vl = _line, vc = _col;
                char c = Cur;
                if (c == '"' || c == '\'')
                {
                    string s = ParseStringValue(out string raw);
                    return new TomlValue { Kind = TomlValueKind.String, StringValue = s, Raw = raw, Line = vl, Column = vc };
                }
                if (c == '[') return ParseArray();
                if (c == '{') return ParseInlineTable();

                // bare token: read until a terminator, then classify.
                int start = _i;
                var sb = new StringBuilder();
                while (true)
                {
                    char d = Cur;
                    if (d == '\0' || d == '\n' || d == '\r' || d == ',' || d == ']' || d == '}' || d == '#') break;
                    sb.Append(d); Adv();
                }
                string tok = sb.ToString().TrimEnd(' ', '\t');
                if (tok.Length == 0) { Err(vl, vc, "TML003", "Expected a value"); return null; }
                return ClassifyBare(tok, vl, vc);
            }

            private TomlValue ClassifyBare(string tok, int line, int col)
            {
                if (tok == "true" || tok == "false")
                    return new TomlValue { Kind = TomlValueKind.Boolean, BoolValue = tok == "true", Raw = tok, Line = line, Column = col };

                // date-times (RFC 3339-ish)
                if (Regex.IsMatch(tok, @"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+\-]\d{2}:\d{2})$"))
                    return DateVal(TomlValueKind.OffsetDateTime, tok, line, col);
                if (Regex.IsMatch(tok, @"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d+)?$"))
                    return DateVal(TomlValueKind.LocalDateTime, tok, line, col);
                if (Regex.IsMatch(tok, @"^\d{4}-\d{2}-\d{2}$"))
                    return DateVal(TomlValueKind.LocalDate, tok, line, col);
                if (Regex.IsMatch(tok, @"^\d{2}:\d{2}:\d{2}(\.\d+)?$"))
                    return DateVal(TomlValueKind.LocalTime, tok, line, col);

                // integers: dec (with _ and sign), hex/oct/bin
                string bare = tok.Replace("_", "");
                if (Regex.IsMatch(tok, @"^[+\-]?(0x[0-9A-Fa-f_]+|0o[0-7_]+|0b[01_]+)$"))
                {
                    long iv = ParseRadix(bare);
                    return new TomlValue { Kind = TomlValueKind.Integer, IntValue = iv, Raw = tok, Line = line, Column = col };
                }
                if (Regex.IsMatch(tok, @"^[+\-]?\d[\d_]*$"))
                {
                    long iv; long.TryParse(bare, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv);
                    return new TomlValue { Kind = TomlValueKind.Integer, IntValue = iv, Raw = tok, Line = line, Column = col };
                }
                // floats (incl. inf/nan/exp)
                if (Regex.IsMatch(tok, @"^[+\-]?(inf|nan)$"))
                {
                    double f = tok.EndsWith("nan") ? double.NaN : (tok.StartsWith("-") ? double.NegativeInfinity : double.PositiveInfinity);
                    return new TomlValue { Kind = TomlValueKind.Float, FloatValue = f, Raw = tok, Line = line, Column = col };
                }
                if (Regex.IsMatch(tok, @"^[+\-]?\d[\d_]*(\.\d[\d_]*)?([eE][+\-]?\d[\d_]*)?$") && (tok.Contains(".") || tok.Contains("e") || tok.Contains("E")))
                {
                    double f; double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                    return new TomlValue { Kind = TomlValueKind.Float, FloatValue = f, Raw = tok, Line = line, Column = col };
                }

                Err(line, col, "TML004", "Unrecognized value '" + tok + "'");
                return new TomlValue { Kind = TomlValueKind.String, StringValue = tok, Raw = tok, Line = line, Column = col };
            }

            private static long ParseRadix(string bare)
            {
                bool neg = bare.StartsWith("-");
                string b = bare.TrimStart('+', '-');
                long v = 0;
                try
                {
                    if (b.StartsWith("0x")) v = Convert.ToInt64(b.Substring(2), 16);
                    else if (b.StartsWith("0o")) v = Convert.ToInt64(b.Substring(2), 8);
                    else if (b.StartsWith("0b")) v = Convert.ToInt64(b.Substring(2), 2);
                }
                catch { }
                return neg ? -v : v;
            }

            private static TomlValue DateVal(TomlValueKind k, string tok, int line, int col)
            {
                return new TomlValue { Kind = k, StringValue = tok, Raw = tok, Line = line, Column = col };
            }

            private TomlValue ParseArray()
            {
                int vl = _line, vc = _col;
                Adv(); // '['
                var items = new List<TomlValue>();
                while (true)
                {
                    SkipWsAndCommentsAndNewlines();
                    if (Cur == ']') { Adv(); break; }
                    if (Cur == '\0') { Err(vl, vc, "TML003", "Unterminated array"); break; }
                    TomlValue item = ParseValue();
                    if (item != null) items.Add(item);
                    SkipWsAndCommentsAndNewlines();
                    if (Cur == ',') { Adv(); continue; }
                    if (Cur == ']') { Adv(); break; }
                    if (Cur == '\0') { Err(vl, vc, "TML003", "Unterminated array"); break; }
                }
                return new TomlValue { Kind = TomlValueKind.Array, Array = items, Raw = "[...]", Line = vl, Column = vc };
            }

            private TomlValue ParseInlineTable()
            {
                int vl = _line, vc = _col;
                Adv(); // '{'
                var tbl = new TomlTable { Line = vl, Column = vc, FromInlineTable = true };
                SkipInlineWs();
                if (Cur == '}') { Adv(); return new TomlValue { Kind = TomlValueKind.InlineTable, Inline = tbl, Raw = "{}", Line = vl, Column = vc }; }
                while (true)
                {
                    SkipInlineWs();
                    ParseKeyValue(tbl);
                    SkipInlineWs();
                    if (Cur == ',') { Adv(); continue; }
                    if (Cur == '}') { Adv(); break; }
                    if (Cur == '\0' || Cur == '\n') { Err(vl, vc, "TML003", "Unterminated inline table"); break; }
                    Err(_line, _col, "TML003", "Expected ',' or '}' in inline table"); break;
                }
                return new TomlValue { Kind = TomlValueKind.InlineTable, Inline = tbl, Raw = "{...}", Line = vl, Column = vc };
            }

            private void SkipWsAndCommentsAndNewlines()
            {
                while (true)
                {
                    char c = Cur;
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Adv();
                    else if (c == '#') SkipComment();
                    else break;
                }
            }

            // ---- strings ----
            private string ParseStringValue(out string raw)
            {
                int start = _i;
                string v = ParseQuotedStringFull();
                raw = _s.Substring(start, _i - start);
                return v;
            }

            private string ParseQuotedString() { return ParseQuotedStringFull(); }

            private string ParseQuotedStringFull()
            {
                char q = Cur;
                bool basic = q == '"';
                // multiline?
                if (Peek(1) == q && Peek(2) == q)
                {
                    Adv(3);
                    // A newline immediately after the opening delimiter is trimmed.
                    if (Cur == '\r') Adv();
                    if (Cur == '\n') Adv();
                    var sb = new StringBuilder();
                    while (_i < _n)
                    {
                        if (Cur == q && Peek(1) == q && Peek(2) == q) { Adv(3); break; }
                        if (basic && Cur == '\\') { AppendEscape(sb); continue; }
                        sb.Append(Cur); Adv();
                    }
                    return sb.ToString();
                }
                Adv(); // opening quote
                var b = new StringBuilder();
                while (_i < _n && Cur != q && Cur != '\n')
                {
                    if (basic && Cur == '\\') { AppendEscape(b); continue; }
                    b.Append(Cur); Adv();
                }
                if (Cur == q) Adv();
                else Err(_line, _col, "TML003", "Unterminated string");
                return b.ToString();
            }

            private void AppendEscape(StringBuilder sb)
            {
                Adv(); // backslash
                char e = Cur;
                switch (e)
                {
                    case 'n': sb.Append('\n'); Adv(); break;
                    case 't': sb.Append('\t'); Adv(); break;
                    case 'r': sb.Append('\r'); Adv(); break;
                    case '"': sb.Append('"'); Adv(); break;
                    case '\\': sb.Append('\\'); Adv(); break;
                    case 'b': sb.Append('\b'); Adv(); break;
                    case 'f': sb.Append('\f'); Adv(); break;
                    case '\n': case '\r': case ' ': case '\t':
                        // line-ending backslash in multiline: trim following whitespace
                        while (Cur == '\n' || Cur == '\r' || Cur == ' ' || Cur == '\t') Adv();
                        break;
                    case 'u':
                    case 'U':
                        int len = e == 'u' ? 4 : 8;
                        Adv();
                        var hex = new StringBuilder();
                        for (int k = 0; k < len && IsHex(Cur); k++) { hex.Append(Cur); Adv(); }
                        int cp; if (int.TryParse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                        { try { sb.Append(char.ConvertFromUtf32(cp)); } catch { } }
                        break;
                    default: sb.Append('\\'); break;
                }
            }

            private static bool IsHex(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }
        }
    }
}
