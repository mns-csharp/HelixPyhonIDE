using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Toml
{
    /// <summary>
    /// FR-GEN-05: serializes a <see cref="TomlDocument"/>/<see cref="TomlTable"/> back to TOML text — the
    /// general serializer beyond the pyproject subset. Preserves key <b>order</b> (via
    /// <see cref="TomlTable.KeyOrder"/>) and <b>comments</b> (leading whole-line + trailing same-line
    /// <c>#</c> comments captured on <see cref="TomlItem.LeadingComments"/>/<see cref="TomlItem.TrailingComment"/>
    /// by the parser), and re-parses losslessly for structure/values (<c>Parse(Write(doc))</c> ≡ <c>doc</c>).
    /// Emits scalars before sub-table headers per the TOML grammar, dotted <c>[a.b]</c> headers, and
    /// <c>[[array-of-tables]]</c>. Satisfies NFR-04 round-trip safety for hand-authored config.
    /// </summary>
    public static class TomlWriter
    {
        public static string Write(TomlDocument doc)
        {
            var sb = new StringBuilder();
            if (doc != null) WriteTableBody(sb, doc.Root, "");
            return sb.ToString();
        }

        public static string Write(TomlTable table)
        {
            var sb = new StringBuilder();
            if (table != null) WriteTableBody(sb, table, "");
            return sb.ToString();
        }

        private static void WriteTableBody(StringBuilder sb, TomlTable table, string path)
        {
            // 1) Scalars / arrays / inline tables first (they belong to the current [header]).
            foreach (string key in table.KeyOrder)
            {
                var v = table.Get(key) as TomlValue;
                if (v == null) continue;
                EmitLeading(sb, v);
                sb.Append(FormatKey(key)).Append(" = ").Append(FormatValue(v)).Append(Trailing(v)).Append('\n');
            }

            // 2) Then sub-tables and arrays-of-tables, each under its own header.
            foreach (string key in table.KeyOrder)
            {
                TomlItem item = table.Get(key);
                string full = path.Length == 0 ? key : path + "." + FormatKey(key);
                var sub = item as TomlTable;
                var arr = item as TomlTableArray;
                if (sub != null)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    EmitLeading(sb, sub);
                    sb.Append('[').Append(full).Append(']').Append(Trailing(sub)).Append('\n');
                    WriteTableBody(sb, sub, full);
                }
                else if (arr != null)
                {
                    foreach (var elem in arr.Elements)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        EmitLeading(sb, elem);
                        sb.Append("[[").Append(full).Append("]]").Append(Trailing(elem)).Append('\n');
                        WriteTableBody(sb, elem, full);
                    }
                }
            }
        }

        // Emit whole-line leading comments captured on an item (each already includes the '#').
        private static void EmitLeading(StringBuilder sb, TomlItem item)
        {
            if (item == null || item.LeadingComments == null) return;
            foreach (string c in item.LeadingComments) sb.Append(c).Append('\n');
        }

        // A same-line trailing comment, spaced from the value/header (or empty).
        private static string Trailing(TomlItem item)
        {
            return item != null && !string.IsNullOrEmpty(item.TrailingComment) ? "  " + item.TrailingComment : "";
        }

        private static readonly Regex BareKey = new Regex(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

        private static string FormatKey(string key)
        {
            return BareKey.IsMatch(key ?? "") ? key : "\"" + EscapeBasic(key) + "\"";
        }

        private static string FormatValue(TomlValue v)
        {
            switch (v.Kind)
            {
                case TomlValueKind.String: return "\"" + EscapeBasic(v.StringValue ?? "") + "\"";
                case TomlValueKind.Integer: return v.IntValue.ToString(CultureInfo.InvariantCulture);
                case TomlValueKind.Boolean: return v.BoolValue ? "true" : "false";
                case TomlValueKind.Float:
                    if (double.IsNaN(v.FloatValue)) return "nan";
                    if (double.IsPositiveInfinity(v.FloatValue)) return "inf";
                    if (double.IsNegativeInfinity(v.FloatValue)) return "-inf";
                    string f = v.FloatValue.ToString("R", CultureInfo.InvariantCulture);
                    if (f.IndexOf('.') < 0 && f.IndexOf('e') < 0 && f.IndexOf('E') < 0) f += ".0";
                    return f;
                case TomlValueKind.OffsetDateTime:
                case TomlValueKind.LocalDateTime:
                case TomlValueKind.LocalDate:
                case TomlValueKind.LocalTime:
                    return v.StringValue ?? v.Raw;
                case TomlValueKind.Array:
                {
                    var parts = new List<string>();
                    if (v.Array != null) foreach (var e in v.Array) parts.Add(FormatValue(e));
                    return "[" + string.Join(", ", parts.ToArray()) + "]";
                }
                case TomlValueKind.InlineTable:
                {
                    var parts = new List<string>();
                    if (v.Inline != null)
                        foreach (string k in v.Inline.KeyOrder)
                        {
                            var iv = v.Inline.Get(k) as TomlValue;
                            if (iv != null) parts.Add(FormatKey(k) + " = " + FormatValue(iv));
                        }
                    return "{ " + string.Join(", ", parts.ToArray()) + " }";
                }
                default:
                    return "\"" + EscapeBasic(v.StringValue ?? v.Raw ?? "") + "\"";
            }
        }

        private static string EscapeBasic(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
